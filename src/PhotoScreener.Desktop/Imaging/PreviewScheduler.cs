using Avalonia.Media.Imaging;
using PhotoScreener.Core;
using PhotoScreener.Infrastructure;

namespace PhotoScreener.Desktop.Imaging;

public sealed class ImageLease : IDisposable
{
    private SharedImage? image;
    internal ImageLease(SharedImage image) { this.image = image; image.Retain(); }
    public Bitmap Bitmap => image!.Bitmap;
    internal ImageLease Fork() => new(image ?? throw new ObjectDisposedException(nameof(ImageLease)));
    public void Dispose() => Interlocked.Exchange(ref image, null)?.Release();
}
internal sealed class SharedImage(Bitmap bitmap)
{
    private int references = 1;
    public Bitmap Bitmap { get; } = bitmap;
    public long Bytes => (long)Bitmap.PixelSize.Width * Bitmap.PixelSize.Height * 4;
    public void Retain() => Interlocked.Increment(ref references);
    public void Release() { if (Interlocked.Decrement(ref references) == 0) Bitmap.Dispose(); }
}
public sealed record ImageRequest(Photo Photo, int Side, long Generation, bool Full, Action<ImageLease?, string?> Complete, object? Owner = null, bool Relaxed = false, bool IncludeHistogram = false, int? Order = null);
public sealed record ImageWorkTiming(Photo Photo, int Side, bool Full, double RevisionMs, double DecodeMs, double TotalMs, bool CacheHit);

// Two fixed workers: urgent focused preview/detail, and full/directional tiles/idle work.
public sealed class PreviewScheduler : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly SemaphoreSlim urgentSignal = new(0, 1), secondarySignal = new(0, 1);
    private readonly CancellationTokenSource stop = new();
    private readonly Dictionary<string, (SharedImage Image, LinkedListNode<string> Node)> cache = [];
    private readonly LinkedList<string> recency = [];
    private readonly LinkedList<ImageRequest> visible = [];
    private readonly Queue<ImageRequest> adjacent = new();
    private readonly ThumbnailDiskCache diskCache;
    private readonly Func<string, int, CancellationToken, bool, Bitmap> decode;
    private readonly Task[] workers;
    private bool disposed;
    private Task? disposal;
    private sealed class ActiveWork(ImageRequest request, CancellationTokenSource cancellation, int warmIndex)
    {
        public ImageRequest Request { get; } = request;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public int WarmIndex { get; } = warmIndex;
        public bool Delivered { get; set; }
    }
    private ActiveWork? activeQuick, activeSecondary;
    private ImageRequest? low, full;
    private ImageRequest? focusedQuick;
    private bool awaitingQuick;
    private IReadOnlyList<Photo> warming = [];
    private int warmCursor;
    private Photo? priorityPhoto;
    private int priorityOrder, navigationDirection = 1;
    private string? protectedFocusKey;
    private int protectedFocusSide;
    private long generation, bytes;
    private bool firstViewportReady;
    public int QueueCount { get { lock (gate) return visible.Count + adjacent.Count + (low is null ? 0 : 1) + (full is null ? 0 : 1); } }
    public long CacheBytes { get { lock (gate) return bytes; } }
    public int ThumbnailWrites => diskCache.SuccessfulWrites;
    public bool ForegroundPending { get { lock (gate) return low != null || full != null || visible.Count > 0 ||
        activeQuick != null || activeSecondary is { Request.Relaxed: false }; } }
    public event Action<Photo, bool, double>? Decoded;
    public event Action<ImageWorkTiming>? WorkMeasured;
    public event Action<Photo, long, IReadOnlyList<int>?>? HistogramReady;
    public PreviewScheduler(string? cacheDirectory = null) : this(cacheDirectory, ScaledDecoder.Decode) { }
    internal PreviewScheduler(string? cacheDirectory, Func<string, int, CancellationToken, bool, Bitmap> decoder)
    { diskCache = new(cacheDirectory); decode = decoder; workers = [Task.Run(UrgentLoop), Task.Run(SecondaryLoop)]; }
    public void SetContext(long value, IReadOnlyList<Photo> photos)
    {
        lock (gate)
        {
            if (disposed) return;
            activeQuick?.Cancellation.Cancel(); activeSecondary?.Cancellation.Cancel();
            generation = value; warming = photos; warmCursor = 0;
            priorityPhoto = null; protectedFocusKey = null; protectedFocusSide = 0;
            low = full = focusedQuick = null; awaitingQuick = false; visible.Clear(); adjacent.Clear(); firstViewportReady = false;
        }
    }
    public void SetWarmPhotos(IReadOnlyList<Photo> photos) { lock (gate) { if (disposed) return; warming = photos; } Wake(secondarySignal); }
    public void SetNavigationPriority(Photo photo, int order, int direction)
    {
        lock (gate)
        {
            if (disposed) return;
            if (priorityPhoto != photo) { protectedFocusKey = null; protectedFocusSide = 0; }
            priorityPhoto = photo; priorityOrder = order; navigationDirection = direction < 0 ? -1 : 1;
        }
    }
    private (int Group, int Distance) VisibleRank(ImageRequest request)
    {
        if (request.Photo == priorityPhoto) return (0, 0);
        if (request.Order is not { } order || priorityPhoto == null) return (3, 0);
        var distance = order - priorityOrder;
        return (Math.Sign(distance) == navigationDirection ? 1 : 2, Math.Abs(distance));
    }
    private LinkedListNode<ImageRequest>? BestVisible(bool worst = false)
    {
        var chosen = visible.First;
        for (var node = chosen?.Next; node != null; node = node.Next)
        {
            var comparison = VisibleRank(node.Value).CompareTo(VisibleRank(chosen!.Value));
            if (worst ? comparison >= 0 : comparison < 0) chosen = node;
        }
        return chosen;
    }
    private void TrimVisible()
    {
        while (visible.Count > 24 && BestVisible(true) is { } worst) visible.Remove(worst);
    }
    private void InterruptSecondary()
    {
        if (activeSecondary is not { } work) return;
        var request = work.Request;
        if (!work.Delivered && request.Side == 160 && !request.Full && !request.Relaxed && request.Generation == generation &&
            !visible.Any(r => request.Owner != null ? ReferenceEquals(r.Owner, request.Owner) : r.Owner == null && r.Photo == request.Photo))
        { visible.AddLast(request); TrimVisible(); }
        work.Cancellation.Cancel();
    }
    public void Focus(ImageRequest quick, ImageRequest refinement)
    {
        lock (gate)
        {
            if (disposed || quick.Generation != generation || refinement.Generation != generation) return;
            if (priorityPhoto != quick.Photo)
            { priorityPhoto = quick.Photo; priorityOrder = quick.Order ?? 0; protectedFocusKey = null; protectedFocusSide = 0; }
            activeQuick?.Cancellation.Cancel();
            InterruptSecondary();
            low = focusedQuick = quick; full = refinement; awaitingQuick = true;
        }
        Wake(urgentSignal); Wake(secondarySignal);
    }
    // The focused photo is already visible. Detail/zoom refinement can use the
    // urgent worker directly, without rereading a quick preview or waiting for tiles.
    public void Refine(ImageRequest request)
    {
        lock (gate)
        {
            if (disposed || !request.Full || request.Generation != generation) return;
            if (priorityPhoto != null && priorityPhoto != request.Photo) return;
            priorityPhoto ??= request.Photo;
            activeQuick?.Cancellation.Cancel(); InterruptSecondary();
            low = focusedQuick = request; full = null; awaitingQuick = true;
        }
        Wake(urgentSignal); Wake(secondarySignal);
    }
    public void ClearFocus()
    {
        lock (gate)
        {
            if (disposed) return;
            activeQuick?.Cancellation.Cancel();
            if (activeSecondary is { Request.Full: true }) activeSecondary.Cancellation.Cancel();
            low = full = focusedQuick = null; awaitingQuick = false;
            priorityPhoto = null; protectedFocusKey = null; protectedFocusSide = 0;
        }
        Wake(secondarySignal);
    }
    public void Thumbnail(ImageRequest request)
    {
        lock (gate)
        {
            if (disposed || request.Generation != generation) return;
            if (request.Owner != null && ReferenceEquals(activeSecondary?.Request.Owner, request.Owner)) activeSecondary.Cancellation.Cancel();
            // DataContext and visual attachment can request the same tile twice. Keep the newest
            // callback so its control generation receives the result instead of discarding it.
            for (var node = visible.First; node != null; node = node.Next)
                if (request.Owner != null ? ReferenceEquals(node.Value.Owner, request.Owner) : node.Value.Owner == null && node.Value.Photo.Path == request.Photo.Path) { visible.Remove(node); break; }
            visible.AddLast(request); TrimVisible();
        }
        Wake(secondarySignal);
    }
    public void CancelThumbnail(object owner)
    {
        lock (gate)
        {
            if (disposed) return;
            if (ReferenceEquals(activeSecondary?.Request.Owner, owner)) activeSecondary!.Cancellation.Cancel();
            for (var node = visible.First; node != null;)
            { var next = node.Next; if (ReferenceEquals(node.Value.Owner, owner)) visible.Remove(node); node = next; }
        }
    }
    public void ViewportReady() { lock (gate) { if (disposed) return; firstViewportReady = true; } Wake(secondarySignal); }
    public void Prefetch(IEnumerable<Photo> photos)
    {
        lock (gate)
        {
            if (disposed) return;
            if (activeSecondary is { Request.Relaxed: true, Request.Side: 800 }) activeSecondary.Cancellation.Cancel();
            adjacent.Clear(); foreach (var photo in photos.Take(4)) adjacent.Enqueue(new(photo, 800, generation, false, (lease, _) => lease?.Dispose(), Relaxed: true));
        }
        Wake(secondarySignal);
    }
    private static void Wake(SemaphoreSlim signal)
    {
        try { if (signal.CurrentCount == 0) signal.Release(); }
        catch (SemaphoreFullException) { }
        catch (ObjectDisposedException) { } // Disposal may follow a valid request before its wake outside gate.
    }
    // Called while holding gate, so replacing a request cannot miss work between dequeue and decode.
    private ActiveWork? Begin(ImageRequest? request, bool urgent, int warmIndex = -1)
    {
        if (request == null || request.Generation != generation || stop.IsCancellationRequested) return null;
        var work = new ActiveWork(request, CancellationTokenSource.CreateLinkedTokenSource(stop.Token), warmIndex);
        if (urgent) activeQuick = work; else activeSecondary = work;
        return work;
    }
    private async Task UrgentLoop()
    {
        try { while (!stop.IsCancellationRequested) { await urgentSignal.WaitAsync(stop.Token); ActiveWork? work; lock (gate) { work = Begin(low, true); low = null; } if (work != null) Execute(work); } }
        catch (OperationCanceledException) { }
    }
    private async Task SecondaryLoop()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                await secondarySignal.WaitAsync(stop.Token);
                ImageRequest? request = null; ActiveWork? work; var warmIndex = -1;
                lock (gate)
                {
                    // A canceled native detail call may still occupy the urgent
                    // worker. The other fixed worker can show the newly focused
                    // quick image immediately instead of waiting for that call.
                    if (activeQuick is { Request.Full: true } blocked && blocked.Cancellation.IsCancellationRequested && low is { Full: false })
                    { request = low; low = null; }
                    // The current quick/full image precedes unrelated visible tiles.
                    else if (!awaitingQuick && full != null) { request = full; full = null; }
                    else if (!awaitingQuick && BestVisible() is { } tile) { request = tile.Value; visible.Remove(tile); }
                    else if (!awaitingQuick && adjacent.Count > 0) request = adjacent.Dequeue();
                    else if (!awaitingQuick && firstViewportReady && warmCursor < warming.Count)
                    {
                        warmIndex = warmCursor;
                        request = new(warming[warmCursor++], 160, generation, false, (lease, _) => lease?.Dispose(), Relaxed: true);
                    }
                    work = Begin(request, false, warmIndex);
                }
                if (work != null) Execute(work);
                bool more; lock (gate) more = low != null || full != null || visible.Count > 0 || adjacent.Count > 0 || firstViewportReady && warmCursor < warming.Count;
                if (more && request != null) Wake(secondarySignal);
            }
        }
        catch (OperationCanceledException) { }
    }
    private string Key(ImageRequest r) => $"{r.Photo.Path}|{r.Photo.Revision}|{r.Side}|oriented-v2";
    private void Execute(ActiveWork work)
    {
        var request = work.Request; var token = work.Cancellation.Token;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Bitmap? bitmap = null;
        ImageLease? lease = null;
        ImageLease? cacheWriteLease = null;
        ImageLease? histogramLease = null;
        var revisionMs = 0d; var decodeMs = 0d; var cacheHit = false;
        try
        {
            token.ThrowIfCancellationRequested();
            using var backgroundPriority = request.Relaxed ? PlatformFileSystem.BackgroundWork() : null;
            if (PathGuard.Revision(request.Photo.Path) != request.Photo.Revision) throw new IOException("Source changed; refresh the folder.");
            revisionMs = watch.Elapsed.TotalMilliseconds;
            lock (gate)
            {
                token.ThrowIfCancellationRequested();
                if (request.Generation != generation) return;
                if (request.Photo.Revision.ChangeToken != null && cache.TryGetValue(Key(request), out var hit))
                { recency.Remove(hit.Node); recency.AddFirst(hit.Node); lease = new(hit.Image); cacheHit = true; }
                else if (request.Side == 160 && !request.Relaxed && request.Photo.Revision.ChangeToken != null && cache.TryGetValue(Key(request with { Side = 800 }), out var quick))
                { recency.Remove(quick.Node); recency.AddFirst(quick.Node); lease = new(quick.Image); cacheHit = true; }
                if (lease != null && request.Full && request.Photo == priorityPhoto && request.Side >= protectedFocusSide)
                { protectedFocusKey = Key(request); protectedFocusSide = request.Side; }
            }
            if (lease == null)
            {
                var keyForDisk = Key(request);
                bitmap = request.Side == 160 && request.Photo.Revision.ChangeToken != null ? diskCache.Read(keyForDisk) : null;
                var diskHit = bitmap != null;
                cacheHit = diskHit;
                var decodeStarted = watch.Elapsed.TotalMilliseconds;
                token.ThrowIfCancellationRequested();
                bitmap ??= decode(request.Photo.Path, request.Side, token, !request.Full);
                decodeMs = watch.Elapsed.TotalMilliseconds - decodeStarted;
                token.ThrowIfCancellationRequested();
                if (PathGuard.Revision(request.Photo.Path) != request.Photo.Revision) throw new IOException("Source changed during preview decoding.");
                var image = new SharedImage(bitmap); bitmap = null;
                lock (gate)
                {
                    if (token.IsCancellationRequested || request.Generation != generation) { image.Release(); return; }
                    var key = Key(request);
                    if (cache.Remove(key, out var old)) { bytes -= old.Image.Bytes; recency.Remove(old.Node); old.Image.Release(); }
                    cache[key] = (image, recency.AddFirst(key)); bytes += image.Bytes;
                    if (request.Full && request.Photo == priorityPhoto && request.Side >= protectedFocusSide)
                    { protectedFocusKey = key; protectedFocusSide = request.Side; }
                    lease = new(image);
                    if (request.Side == 160 && !diskHit && request.Photo.Revision.ChangeToken != null) cacheWriteLease = new(image);
                    while (bytes > 64L * 1024 * 1024 && recency.Last is { } last)
                    {
                        if (last.Value == protectedFocusKey && last.Previous != null) last = last.Previous;
                        var evicted = cache[last.Value]; cache.Remove(last.Value); recency.Remove(last); bytes -= evicted.Image.Bytes; evicted.Image.Release();
                    }
                }
            }
            token.ThrowIfCancellationRequested();
            if (request.IncludeHistogram) histogramLease = lease!.Fork();
            Decoded?.Invoke(request.Photo, request.Full, watch.Elapsed.TotalMilliseconds);
            WorkMeasured?.Invoke(new(request.Photo, request.Side, request.Full, revisionMs, decodeMs, watch.Elapsed.TotalMilliseconds, cacheHit));
            // A queued current thumbnail can share the just-decoded quick image:
            // it appears with the main preview instead of opening the JPEG again.
            List<ImageRequest> currentTiles = [];
            lock (gate)
            {
                work.Delivered = true;
                if (!request.Full && request.Side == 800 && request.Photo == priorityPhoto)
                    for (var node = visible.First; node != null;)
                    {
                        var next = node.Next;
                        if (node.Value.Photo == request.Photo) { currentTiles.Add(node.Value); visible.Remove(node); }
                        node = next;
                    }
            }
            foreach (var tile in currentTiles)
            {
                var tileLease = lease!.Fork();
                try { tile.Complete(tileLease, null); }
                catch { tileLease.Dispose(); throw; }
            }
            request.Complete(lease, null); lease = null;
            if (histogramLease != null)
            {
                IReadOnlyList<int>? histogram = null;
                try { histogram = PreviewHistogram.Read(histogramLease.Bitmap); } catch (Exception e) when (e is not OutOfMemoryException) { }
                if (!token.IsCancellationRequested) HistogramReady?.Invoke(request.Photo, request.Generation, histogram);
                histogramLease.Dispose(); histogramLease = null;
            }
            token.ThrowIfCancellationRequested();
            if (cacheWriteLease != null)
            { using var background = PlatformFileSystem.BackgroundWork(); diskCache.Write(Key(request), cacheWriteLease.Bitmap); }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception e) when (e is not OutOfMemoryException) { if (!token.IsCancellationRequested) request.Complete(null, e.Message); }
        finally
        {
            bitmap?.Dispose(); lease?.Dispose(); cacheWriteLease?.Dispose(); histogramLease?.Dispose();
            lock (gate)
            {
                if (ReferenceEquals(focusedQuick, request)) awaitingQuick = false;
                if (ReferenceEquals(activeQuick, work)) activeQuick = null;
                if (ReferenceEquals(activeSecondary, work)) activeSecondary = null;
                // Interrupted idle work is eligible again after interaction subsides.
                if (token.IsCancellationRequested && work.WarmIndex >= 0 && request.Generation == generation)
                    warmCursor = Math.Min(warmCursor, work.WarmIndex);
                work.Cancellation.Dispose();
            }
            Wake(secondarySignal);
        }
    }
    public ValueTask DisposeAsync()
    {
        lock (gate)
        {
            disposed = true;
            return new(disposal ??= DisposeCore());
        }
    }
    private async Task DisposeCore()
    {
        stop.Cancel(); await Task.WhenAll(workers);
        lock (gate)
        {
            foreach (var entry in cache.Values) entry.Image.Release(); cache.Clear(); recency.Clear(); bytes = 0;
            visible.Clear(); adjacent.Clear(); warming = []; low = full = focusedQuick = null; awaitingQuick = false;
        }
        stop.Dispose(); urgentSignal.Dispose(); secondarySignal.Dispose();
    }
    public void ClearCache()
    {
        lock (gate) { foreach (var entry in cache.Values) entry.Image.Release(); cache.Clear(); recency.Clear(); bytes = 0; protectedFocusKey = null; protectedFocusSide = 0; }
    }
}
