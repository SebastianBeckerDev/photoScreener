using Avalonia.Media.Imaging;
using PhotoScreener.Core;

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
public sealed record ImageRequest(Photo Photo, int Side, long Generation, bool Full, Action<ImageLease?, string?> Complete);

// Two fixed workers: latest focused low preview, and fair full/visible/idle work.
public sealed class PreviewScheduler : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly SemaphoreSlim urgentSignal = new(0, 1), secondarySignal = new(0, 1);
    private readonly CancellationTokenSource stop = new();
    private readonly Dictionary<string, (SharedImage Image, LinkedListNode<string> Node)> cache = [];
    private readonly LinkedList<string> recency = [];
    private readonly LinkedList<ImageRequest> visible = [];
    private readonly Task[] workers;
    private ImageRequest? low, full;
    private IReadOnlyList<Photo> warming = [];
    private int warmCursor, visibleRun;
    private long generation, bytes;
    private bool firstViewportReady;
    public int QueueCount { get { lock (gate) return visible.Count + (low is null ? 0 : 1) + (full is null ? 0 : 1); } }
    public long CacheBytes { get { lock (gate) return bytes; } }
    public bool ForegroundPending { get { lock (gate) return low != null || full != null || visible.Count > 0; } }
    public event Action<Photo, bool, double>? Decoded;
    public PreviewScheduler() => workers = [Task.Run(UrgentLoop), Task.Run(SecondaryLoop)];
    public void SetContext(long value, IReadOnlyList<Photo> photos)
    {
        lock (gate) { generation = value; warming = photos; warmCursor = 0; low = full = null; visible.Clear(); firstViewportReady = false; }
    }
    public void SetWarmPhotos(IReadOnlyList<Photo> photos) { lock (gate) warming = photos; Wake(secondarySignal); }
    public void Focus(ImageRequest quick, ImageRequest refinement)
    {
        lock (gate) { low = quick; full = refinement; }
        Wake(urgentSignal); Wake(secondarySignal);
    }
    public void Thumbnail(ImageRequest request)
    {
        lock (gate)
        {
            if (request.Generation != generation) return;
            if (visible.Any(r => r.Photo.Path == request.Photo.Path)) return;
            if (visible.Count >= 32) visible.RemoveLast();
            visible.AddFirst(request);
        }
        Wake(secondarySignal);
    }
    public void ViewportReady() { lock (gate) firstViewportReady = true; Wake(secondarySignal); }
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
                    if (low == null && full != null && (visible.Count == 0 || visibleRun >= 4)) { request = full; full = null; visibleRun = 0; }
                    else if (low == null && visible.First != null) { request = visible.First.Value; visible.RemoveFirst(); visibleRun++; }
                    else if (low == null && firstViewportReady && warmCursor < warming.Count)
                        request = new(warming[warmCursor++], 160, generation, false, (lease, _) => lease?.Dispose());
                }
                if (request != null) Execute(request);
                bool more; lock (gate) more = low != null || full != null || visible.Count > 0 || firstViewportReady && warmCursor < warming.Count;
                if (more) { if (request == null) await Task.Delay(1, stop.Token); Wake(secondarySignal); }
            }
        }
        catch (OperationCanceledException) { }
    }
    private string Key(ImageRequest r) => $"{r.Photo.Path}|{r.Photo.Revision}|{r.Side}|oriented-v1";
    private void Execute(ImageRequest request)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        ImageLease? lease = null;
        try
        {
            lock (gate)
            {
                if (request.Generation != generation) return;
                if (cache.TryGetValue(Key(request), out var hit))
                { recency.Remove(hit.Node); recency.AddFirst(hit.Node); lease = new(hit.Image); }
            }
            if (lease == null)
            {
                var bitmap = ScaledDecoder.Decode(request.Photo.Path, request.Side, stop.Token);
                var image = new SharedImage(bitmap);
                lock (gate)
                {
                    if (request.Generation != generation) { image.Release(); return; }
                    var key = Key(request);
                    if (cache.Remove(key, out var old)) { bytes -= old.Image.Bytes; recency.Remove(old.Node); old.Image.Release(); }
                    cache[key] = (image, recency.AddFirst(key)); bytes += image.Bytes;
                    lease = new(image);
                    while (bytes > 128L * 1024 * 1024 && recency.Last is { } last)
                    { var evicted = cache[last.Value]; cache.Remove(last.Value); recency.RemoveLast(); bytes -= evicted.Image.Bytes; evicted.Image.Release(); }
                }
            }
            Decoded?.Invoke(request.Photo, request.Full, watch.Elapsed.TotalMilliseconds);
            request.Complete(lease, null); lease = null;
        }
        catch (Exception e) when (e is not OutOfMemoryException) { request.Complete(null, e.Message); }
        finally { lease?.Dispose(); Wake(secondarySignal); }
    }
    public async ValueTask DisposeAsync()
    {
        stop.Cancel(); await Task.WhenAll(workers);
        lock (gate) { foreach (var entry in cache.Values) entry.Image.Release(); cache.Clear(); recency.Clear(); }
        stop.Dispose(); urgentSignal.Dispose(); secondarySignal.Dispose();
    }
}
