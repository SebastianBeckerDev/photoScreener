using System.Collections.Concurrent;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using PhotoScreener.Core;
using PhotoScreener.Desktop.Imaging;
using PhotoScreener.Infrastructure;
using Xunit;

namespace PhotoScreener.Desktop.Tests;

public sealed class PreviewPriorityTests
{
    private sealed class Corpus : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "lumapick-priority-" + Guid.NewGuid().ToString("N"));
        public Photo[] Photos { get; }
        public Corpus()
        {
            Directory.CreateDirectory(Root);
            for (var i = 0; i < 7; i++) File.WriteAllText(Path.Combine(Root, i + ".JPG"), "controlled decoder fixture " + i);
            Photos = new SourceCatalog().Enumerate(Root, default).OrderBy(p => p.Name).ToArray();
        }
        public void Dispose() => Directory.Delete(Root, true);
    }
    private static Bitmap Pixels() => new WriteableBitmap(new PixelSize(8, 8), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [AvaloniaFact]
    public async Task NavigationCanUseTheOtherFixedWorkerWhileCanceledDetailIsStillInNativeCode()
    {
        using var corpus = new Corpus(); using var release = new ManualResetEventSlim();
        var fit = Signal(); var detailStarted = Signal(); var next = Signal(); var obsoleteCallbacks = 0;
        await using var images = new PreviewScheduler(Path.Combine(corpus.Root, "cache"), (path, side, _, _) =>
        {
            if (path == corpus.Photos[3].Path && side == 3840) { detailStarted.TrySetResult(); release.Wait(); }
            return Pixels();
        });
        images.SetContext(1, []); images.SetNavigationPriority(corpus.Photos[3], 3, 1);
        images.Focus(new(corpus.Photos[3], 800, 1, false, (lease, _) => lease?.Dispose()),
            new(corpus.Photos[3], 1024, 1, true, (lease, _) => { lease?.Dispose(); fit.TrySetResult(); }));
        try
        {
            await fit.Task.WaitAsync(TimeSpan.FromSeconds(5));
            images.Refine(new(corpus.Photos[3], 3840, 1, true, (lease, _) => { lease?.Dispose(); Interlocked.Increment(ref obsoleteCallbacks); }));
            await detailStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            images.SetNavigationPriority(corpus.Photos[4], 4, 1);
            images.Focus(new(corpus.Photos[4], 800, 1, false, (lease, _) => lease?.Dispose()),
                new(corpus.Photos[4], 1024, 1, true, (lease, error) =>
                { lease?.Dispose(); if (error == null) next.TrySetResult(); else next.TrySetException(new IOException(error)); }));
            await next.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, obsoleteCallbacks); Assert.InRange(images.CacheBytes, 0, 4 * 8 * 8 * 4);
        }
        finally { release.Set(); }
    }

    [AvaloniaTheory]
    [InlineData(1)] [InlineData(-1)]
    public async Task CurrentThumbnailAndFullPreviewPrecedeDirectionalNeighborsRegardlessOfArrivalOrder(int direction)
    {
        using var corpus = new Corpus(); using var release = new ManualResetEventSlim(); var started = Signal(); var done = Signal();
        var publications = new ConcurrentQueue<string>(); var decoded = new ConcurrentQueue<(string Name, int Side)>(); var tiles = 0;
        await using var images = new PreviewScheduler(Path.Combine(corpus.Root, "cache"), (path, side, token, _) =>
        {
            decoded.Enqueue((Path.GetFileName(path), side));
            if (path == corpus.Photos[3].Path && side == 800) { started.TrySetResult(); release.Wait(token); }
            return Pixels();
        });
        images.SetContext(1, []); images.SetNavigationPriority(corpus.Photos[3], 3, direction);
        images.Focus(new(corpus.Photos[3], 800, 1, false, (lease, _) => lease?.Dispose()),
            new(corpus.Photos[3], 1024, 1, true, (lease, _) => { lease?.Dispose(); publications.Enqueue("full"); }));
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            // The most recently attached/rightmost tile must not jump the queue.
            foreach (var index in new[] { 6, 0, 5, 1, 4, 2, 3 })
            {
                var captured = index;
                images.Thumbnail(new(corpus.Photos[index], 160, 1, false, (lease, error) =>
                {
                    lease?.Dispose();
                    if (error != null) { done.TrySetException(new IOException(error)); return; }
                    publications.Enqueue(captured.ToString()); if (Interlocked.Increment(ref tiles) == 7) done.TrySetResult();
                }, new object(), Order: index));
            }
            release.Set(); await done.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(direction == 1 ? new[] { "3", "full", "4", "5", "6", "2", "1", "0" }
                : new[] { "3", "full", "2", "1", "0", "4", "5", "6" }, publications.ToArray());
            Assert.DoesNotContain(decoded, item => item.Name == "3.JPG" && item.Side == 160);
        }
        finally { release.Set(); }
    }

    [AvaloniaFact]
    public async Task DetailUsesTheUrgentWorkerWithoutRepeatingQuickOrWaitingForAnUninterruptibleVisibleDecode()
    {
        using var corpus = new Corpus(); using var release = new ManualResetEventSlim();
        var fit = Signal(); var visibleStarted = Signal(); var detail = Signal(); var retriedTile = Signal();
        var quickCount = 0; var blockOnce = 0; var tileCallbacks = 0; var active = 0; var peak = 0;
        await using var images = new PreviewScheduler(Path.Combine(corpus.Root, "cache"), (path, side, token, _) =>
        {
            var count = Interlocked.Increment(ref active); int observed;
            do { observed = Volatile.Read(ref peak); } while (count > observed && Interlocked.CompareExchange(ref peak, count, observed) != observed);
            try
            {
                if (side == 800) Interlocked.Increment(ref quickCount);
                if (path == corpus.Photos[0].Path && side == 160 && Interlocked.Exchange(ref blockOnce, 1) == 0)
                { visibleStarted.TrySetResult(); release.Wait(); } // A native call cannot observe cancellation yet.
                return Pixels();
            }
            finally { Interlocked.Decrement(ref active); }
        });
        images.SetContext(1, []); images.SetNavigationPriority(corpus.Photos[3], 3, 1);
        images.Focus(new(corpus.Photos[3], 800, 1, false, (lease, _) => lease?.Dispose()),
            new(corpus.Photos[3], 1024, 1, true, (lease, _) => { lease?.Dispose(); fit.TrySetResult(); }));
        try
        {
            await fit.Task.WaitAsync(TimeSpan.FromSeconds(5));
            images.Thumbnail(new(corpus.Photos[0], 160, 1, false, (lease, error) =>
            {
                lease?.Dispose(); Interlocked.Increment(ref tileCallbacks);
                if (error == null) retriedTile.TrySetResult(); else retriedTile.TrySetException(new IOException(error));
            }, new object(), Order: 0));
            await visibleStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            images.Refine(new(corpus.Photos[3], 3840, 1, true, (lease, error) =>
            {
                lease?.Dispose(); if (error == null) detail.TrySetResult(); else detail.TrySetException(new IOException(error));
            }));
            await detail.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, quickCount); Assert.Equal(2, peak); Assert.Equal(0, tileCallbacks);
            release.Set(); await retriedTile.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, tileCallbacks); // The canceled tile is retried rather than lost or published twice.
            Assert.InRange(images.CacheBytes, 0, 64L * 1024 * 1024);
        }
        finally { release.Set(); }
    }
}
