using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using PhotoScreener.Core;
using PhotoScreener.Desktop.Imaging;
using PhotoScreener.Infrastructure;
using Xunit;

namespace PhotoScreener.Desktop.Tests;
public sealed class PreviewCancellationTests
{
    private sealed class Scenario : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "lumapick-preview-cancel-" + Guid.NewGuid().ToString("N"));
        public Photo Old { get; }
        public Photo Next { get; }
        public Scenario()
        {
            Directory.CreateDirectory(Root);
            File.WriteAllText(Path.Combine(Root, "old.JPG"), "old fixture"); File.WriteAllText(Path.Combine(Root, "next.JPG"), "next fixture");
            var photos = new SourceCatalog().Enumerate(Root, default).ToArray();
            Old = photos.Single(p => p.Name == "old.JPG"); Next = photos.Single(p => p.Name == "next.JPG");
        }
        public void Dispose() => Directory.Delete(Root, true);
    }
    private static Bitmap Bitmap() => new WriteableBitmap(new PixelSize(8, 8), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Focus(PreviewScheduler images, Photo photo, long generation, TaskCompletionSource completed)
        => images.Focus(new(photo, 800, generation, false, (lease, _) => lease?.Dispose()),
            new(photo, 1024, generation, true, (lease, error) => { lease?.Dispose(); if (error == null) completed.TrySetResult(); else completed.TrySetException(new IOException(error)); }));

    [Fact]
    public void AlreadyCancelledDecodeDoesNotTryToOpenItsSource()
    {
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        var missingSource = Path.Combine(Path.GetTempPath(), "missing-lumapick-" + Guid.NewGuid().ToString("N"), "photo.JPG");
        Assert.ThrowsAny<OperationCanceledException>(() => ScaledDecoder.Decode(missingSource, 800, cancel.Token));
    }

    [AvaloniaFact]
    public async Task NewFocusCancelsActiveQuickAndReachesItsFullPreview()
    {
        using var scenario = new Scenario(); var started = Signal(); var finished = Signal();
        CancellationToken oldToken = default; var oldCallbacks = 0;
        await using var images = new PreviewScheduler(Path.Combine(scenario.Root, "cache"), (path, _, token, _) =>
        {
            if (path == scenario.Old.Path) { oldToken = token; started.TrySetResult(); token.WaitHandle.WaitOne(); token.ThrowIfCancellationRequested(); }
            return Bitmap();
        });
        images.SetContext(1, []);
        images.Focus(new(scenario.Old, 800, 1, false, (lease, _) => { lease?.Dispose(); Interlocked.Increment(ref oldCallbacks); }),
            new(scenario.Old, 1024, 1, true, (lease, _) => lease?.Dispose()));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(images.ForegroundPending);
        Focus(images, scenario.Next, 1, finished);
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(oldToken.IsCancellationRequested); Assert.Equal(0, oldCallbacks);
    }

    [AvaloniaFact]
    public async Task NewFocusCancelsActiveRefinement()
    {
        using var scenario = new Scenario(); var started = Signal(); var finished = Signal(); CancellationToken oldToken = default;
        await using var images = new PreviewScheduler(Path.Combine(scenario.Root, "cache"), (path, side, token, _) =>
        {
            if (path == scenario.Old.Path && side == 1024) { oldToken = token; started.TrySetResult(); token.WaitHandle.WaitOne(); token.ThrowIfCancellationRequested(); }
            return Bitmap();
        });
        images.SetContext(1, []); Focus(images, scenario.Old, 1, Signal());
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(images.ForegroundPending); // Active refinement still outranks comparison hashing.
        Focus(images, scenario.Next, 1, finished);
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(oldToken.IsCancellationRequested);
    }

    [AvaloniaFact]
    public async Task FolderChangeCancelsActiveWarmingAndSuppressesOldResults()
    {
        using var scenario = new Scenario(); var started = Signal(); var finished = Signal(); CancellationToken oldToken = default; var oldResults = 0;
        await using var images = new PreviewScheduler(Path.Combine(scenario.Root, "cache"), (path, _, token, _) =>
        {
            if (path == scenario.Old.Path) { oldToken = token; started.TrySetResult(); token.WaitHandle.WaitOne(); token.ThrowIfCancellationRequested(); }
            return Bitmap();
        });
        images.Decoded += (photo, _, _) => { if (photo == scenario.Old) Interlocked.Increment(ref oldResults); };
        images.SetContext(1, [scenario.Old]); images.ViewportReady();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        images.SetContext(2, []); Focus(images, scenario.Next, 2, finished);
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(oldToken.IsCancellationRequested); Assert.Equal(0, oldResults);
    }

    [AvaloniaFact]
    public async Task DetachedThumbnailCancelsActiveDecodeAndNextVisibleTileContinues()
    {
        using var scenario = new Scenario(); var started = Signal(); var finished = Signal(); CancellationToken oldToken = default; var oldCallbacks = 0;
        await using var images = new PreviewScheduler(Path.Combine(scenario.Root, "cache"), (path, _, token, _) =>
        {
            if (path == scenario.Old.Path) { oldToken = token; started.TrySetResult(); token.WaitHandle.WaitOne(); token.ThrowIfCancellationRequested(); }
            return Bitmap();
        });
        var owner = new object(); images.SetContext(1, []);
        images.Thumbnail(new(scenario.Old, 160, 1, false, (lease, _) => { lease?.Dispose(); Interlocked.Increment(ref oldCallbacks); }, owner));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        images.Thumbnail(new(scenario.Next, 160, 1, false, (lease, error) => { lease?.Dispose(); if (error == null) finished.TrySetResult(); else finished.TrySetException(new IOException(error)); }, new object()));
        images.CancelThumbnail(owner);
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(oldToken.IsCancellationRequested); Assert.Equal(0, oldCallbacks);
    }

    [AvaloniaFact]
    public async Task UninterruptibleRefinementKeepsTwoWorkersAndDiscardsItsLateBitmap()
    {
        using var scenario = new Scenario(); using var release = new ManualResetEventSlim();
        var started = Signal(); var nextQuick = Signal(); var finished = Signal(); var oldCallbacks = 0; var active = 0; var peak = 0;
        await using var images = new PreviewScheduler(Path.Combine(scenario.Root, "cache"), (path, side, token, _) =>
        {
            var count = Interlocked.Increment(ref active);
            int observed;
            do { observed = Volatile.Read(ref peak); } while (count > observed && Interlocked.CompareExchange(ref peak, count, observed) != observed);
            try
            {
                if (path == scenario.Old.Path && side == 1024) { started.TrySetResult(); release.Wait(); } // Models a native call that cannot observe cancellation yet.
                if (path == scenario.Next.Path && side == 800) nextQuick.TrySetResult();
                return Bitmap();
            }
            finally { Interlocked.Decrement(ref active); }
        });
        try
        {
            images.SetContext(1, []);
            images.Focus(new(scenario.Old, 800, 1, false, (lease, _) => lease?.Dispose()),
                new(scenario.Old, 1024, 1, true, (lease, _) => { lease?.Dispose(); Interlocked.Increment(ref oldCallbacks); }));
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            for (var i = 0; i < 100; i++) Focus(images, scenario.Next, 1, finished);
            await nextQuick.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(finished.Task.IsCompleted); Assert.InRange(images.QueueCount, 0, 2); Assert.Equal(2, peak);
            release.Set(); await finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, oldCallbacks);
            // Three successful 8x8 stages at most; the cancelled old full bitmap must not enter the cache.
            Assert.InRange(images.CacheBytes, 0, 3 * 8 * 8 * 4);
        }
        finally { release.Set(); }
    }

    [AvaloniaFact]
    public async Task InterruptedIdleThumbnailIsRetriedAfterFocusedWorkFinishes()
    {
        using var scenario = new Scenario(); var started = Signal(); var focused = Signal(); var warmed = Signal(); var attempts = 0;
        await using var images = new PreviewScheduler(Path.Combine(scenario.Root, "cache"), (path, _, token, _) =>
        {
            if (path == scenario.Old.Path && Interlocked.Increment(ref attempts) == 1)
            { started.TrySetResult(); token.WaitHandle.WaitOne(); token.ThrowIfCancellationRequested(); }
            return Bitmap();
        });
        images.Decoded += (photo, _, _) => { if (photo == scenario.Old) warmed.TrySetResult(); };
        images.SetContext(1, [scenario.Old]); images.ViewportReady();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Focus(images, scenario.Next, 1, focused);
        await focused.Task.WaitAsync(TimeSpan.FromSeconds(5)); await warmed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, attempts);
    }

    [AvaloniaFact]
    public async Task ClearingFocusCancelsItsDecodeWithoutBlockingVisibleThumbnails()
    {
        using var scenario = new Scenario(); var started = Signal(); var tile = Signal(); CancellationToken oldToken = default;
        await using var images = new PreviewScheduler(Path.Combine(scenario.Root, "cache"), (path, _, token, _) =>
        {
            if (path == scenario.Old.Path) { oldToken = token; started.TrySetResult(); token.WaitHandle.WaitOne(); token.ThrowIfCancellationRequested(); }
            return Bitmap();
        });
        images.SetContext(1, []); Focus(images, scenario.Old, 1, Signal());
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        images.Thumbnail(new(scenario.Next, 160, 1, false, (lease, error) => { lease?.Dispose(); if (error == null) tile.TrySetResult(); else tile.TrySetException(new IOException(error)); }, new object()));
        images.ClearFocus(); await tile.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(oldToken.IsCancellationRequested);
    }
}
