using Avalonia.Media.Imaging;
using PhotoScreener.Core;
using PhotoScreener.Infrastructure;

namespace PhotoScreener.Desktop.Imaging;

public sealed class ImageLease : IDisposable
{
    private SharedImage? image;
    internal ImageLease(SharedImage image) { this.image = image; image.Retain(); }
    public Bitmap Bitmap => image!.Bitmap;
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
public sealed record ImageRequest(Photo Photo, int Side, long Generation, bool Full, Action<ImageLease?, string?> Complete, object? Owner = null, bool Relaxed = false);

// Two fixed workers: latest focused low preview, and fair full/visible/idle work.
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
    private readonly Task[] workers;
    private ImageRequest? low, full;
    private ImageRequest? focusedQuick;
    private bool awaitingQuick;
    private IReadOnlyList<Photo> warming = [];
    private int warmCursor, visibleRun;
    private long generation, bytes;
    private bool firstViewportReady;
    public int QueueCount { get { lock (gate) return visible.Count + adjacent.Count + (low is null ? 0 : 1) + (full is null ? 0 : 1); } }
    public long CacheBytes { get { lock (gate) return bytes; } }
    public int ThumbnailWrites => diskCache.SuccessfulWrites;
    public bool ForegroundPending { get { lock (gate) return low != null || full != null || visible.Count > 0; } }
    public event Action<Photo, bool, double>? Decoded;
    public PreviewScheduler(string? cacheDirectory = null) { diskCache = new(cacheDirectory); workers = [Task.Run(UrgentLoop), Task.Run(SecondaryLoop)]; }
    public void SetContext(long value, IReadOnlyList<Photo> photos)
    {
        lock (gate) { generation = value; warming = photos; warmCursor = 0; low = full = focusedQuick = null; awaitingQuick = false; visible.Clear(); adjacent.Clear(); firstViewportReady = false; }
    }
    public void SetWarmPhotos(IReadOnlyList<Photo> photos) { lock (gate) warming = photos; Wake(secondarySignal); }
    public void Focus(ImageRequest quick, ImageRequest refinement)
    {
        lock (gate) { low = focusedQuick = quick; full = refinement; awaitingQuick = true; }
        Wake(urgentSignal); Wake(secondarySignal);
    }
    public void Thumbnail(ImageRequest request)
    {
        lock (gate)
        {
            if (request.Generation != generation) return;
            // DataContext and visual attachment can request the same tile twice. Keep the newest
            // callback so its control generation receives the result instead of discarding it.
            for (var node = visible.First; node != null; node = node.Next)
                if (request.Owner != null ? ReferenceEquals(node.Value.Owner, request.Owner) : node.Value.Owner == null && node.Value.Photo.Path == request.Photo.Path) { visible.Remove(node); break; }
            if (visible.Count >= 24) visible.RemoveLast();
            visible.AddFirst(request);
        }
        Wake(secondarySignal);
    }
    public void CancelThumbnail(object owner)
    {
        lock (gate) for (var node = visible.First; node != null;)
        { var next = node.Next; if (ReferenceEquals(node.Value.Owner, owner)) visible.Remove(node); node = next; }
    }
    public void ViewportReady() { lock (gate) firstViewportReady = true; Wake(secondarySignal); }
    public void Prefetch(IEnumerable<Photo> photos)
    {
        lock (gate)
        {
            adjacent.Clear(); foreach (var photo in photos.Take(4)) adjacent.Enqueue(new(photo, 800, generation, false, (lease, _) => lease?.Dispose(), Relaxed: true));
        }
        Wake(secondarySignal);
    }
    private static void Wake(SemaphoreSlim signal) { if (signal.CurrentCount == 0) try { signal.Release(); } catch (SemaphoreFullException) { } }
    private async Task UrgentLoop()
    {
        try { while (!stop.IsCancellationRequested) { await urgentSignal.WaitAsync(stop.Token); ImageRequest? request; lock (gate) { request = low; low = null; } if (request != null) Execute(request); } }
        catch (OperationCanceledException) { }
    }
    private async Task SecondaryLoop()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                await secondarySignal.WaitAsync(stop.Token);
                ImageRequest? request = null;
                lock (gate)
                {
                    // Never refine ahead of the focused quick image. At most four visible tiles precede full.
                    if (!awaitingQuick && full != null && (visible.Count == 0 || visibleRun >= 4)) { request = full; full = null; visibleRun = 0; }
                    else if (!awaitingQuick && visible.First != null) { request = visible.First.Value; visible.RemoveFirst(); visibleRun++; }
                    else if (!awaitingQuick && adjacent.Count > 0) request = adjacent.Dequeue();
                    else if (!awaitingQuick && firstViewportReady && warmCursor < warming.Count)
                        request = new(warming[warmCursor++], 160, generation, false, (lease, _) => lease?.Dispose(), Relaxed: true);
                }
                if (request != null) Execute(request);
                bool more; lock (gate) more = low != null || full != null || visible.Count > 0 || adjacent.Count > 0 || firstViewportReady && warmCursor < warming.Count;
                if (more && request != null) Wake(secondarySignal);
            }
        }
        catch (OperationCanceledException) { }
    }
    private string Key(ImageRequest r) => $"{r.Photo.Path}|{r.Photo.Revision}|{r.Side}|oriented-v1";
    private void Execute(ImageRequest request)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        ImageLease? lease = null;
        ImageLease? cacheWriteLease = null;
        try
        {
            using var backgroundPriority = request.Relaxed ? PlatformFileSystem.BackgroundWork() : null;
            if (PathGuard.Revision(request.Photo.Path) != request.Photo.Revision) throw new IOException("Source changed; refresh the folder.");
            lock (gate)
            {
                if (request.Generation != generation) return;
                if (request.Photo.Revision.ChangeToken != null && cache.TryGetValue(Key(request), out var hit))
                { recency.Remove(hit.Node); recency.AddFirst(hit.Node); lease = new(hit.Image); }
            }
            if (lease == null)
            {
                var keyForDisk = Key(request);
                var bitmap = request.Side == 160 && request.Photo.Revision.ChangeToken != null ? diskCache.Read(keyForDisk) : null;
                var diskHit = bitmap != null;
                bitmap ??= ScaledDecoder.Decode(request.Photo.Path, request.Side, stop.Token);
                if (PathGuard.Revision(request.Photo.Path) != request.Photo.Revision) { bitmap.Dispose(); throw new IOException("Source changed during preview decoding."); }
                var image = new SharedImage(bitmap);
                lock (gate)
                {
                    if (request.Generation != generation) { image.Release(); return; }
                    var key = Key(request);
                    if (cache.Remove(key, out var old)) { bytes -= old.Image.Bytes; recency.Remove(old.Node); old.Image.Release(); }
                    cache[key] = (image, recency.AddFirst(key)); bytes += image.Bytes;
                    lease = new(image);
                    if (request.Side == 160 && !diskHit && request.Photo.Revision.ChangeToken != null) cacheWriteLease = new(image);
                    while (bytes > 64L * 1024 * 1024 && recency.Last is { } last)
                    { var evicted = cache[last.Value]; cache.Remove(last.Value); recency.RemoveLast(); bytes -= evicted.Image.Bytes; evicted.Image.Release(); }
                }
            }
            Decoded?.Invoke(request.Photo, request.Full, watch.Elapsed.TotalMilliseconds);
            request.Complete(lease, null); lease = null;
            if (cacheWriteLease != null)
            { using var background = PlatformFileSystem.BackgroundWork(); diskCache.Write(Key(request), cacheWriteLease.Bitmap); }
        }
        catch (Exception e) when (e is not OutOfMemoryException) { request.Complete(null, e.Message); }
        finally
        {
            lease?.Dispose(); cacheWriteLease?.Dispose();
            lock (gate) if (ReferenceEquals(focusedQuick, request)) awaitingQuick = false;
            Wake(secondarySignal);
        }
    }
    public async ValueTask DisposeAsync()
    {
        stop.Cancel(); await Task.WhenAll(workers);
        lock (gate) { foreach (var entry in cache.Values) entry.Image.Release(); cache.Clear(); recency.Clear(); }
        stop.Dispose(); urgentSignal.Dispose(); secondarySignal.Dispose();
    }
    public void ClearCache()
    {
        lock (gate) { foreach (var entry in cache.Values) entry.Image.Release(); cache.Clear(); recency.Clear(); bytes = 0; }
    }
}
