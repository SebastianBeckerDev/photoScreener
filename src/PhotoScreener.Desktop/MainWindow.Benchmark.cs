using System.Diagnostics;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.Media.Imaging;
using PhotoScreener.Desktop.Diagnostics;

namespace PhotoScreener.Desktop;
public partial class MainWindow
{
    private partial void StartBenchmark()
    {
        var feedback = new List<double>(); var handlers = new List<double>(); var frames = new List<double>();
        var lows = new List<double>(); var fulls = new List<double>();
        var stableLow = new List<double>(); var stableFull = new List<double>(); var deliveries = new List<double>();
        var clock = Stopwatch.StartNew(); var frameInputs = new Queue<double>();
        var usable = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource? fullReady = null; var focusedAt = 0d; var stableAt = 0d; var stable = false;
        var maxWorkingSet = 0L; var maxQueue = 0; var maxCache = 0L; var maxPending = 0; var maxRealized = 0;
        var errors = new List<string>(); var completed = 0; var totalOperations = 0;
        var ignoredKeys = 0; var ignoredContexts = new List<object>();
        var maintenanceErrors = new List<string>();
        mutations!.MaintenanceError += error => { lock (maintenanceErrors) maintenanceErrors.Add(error.ToString()); };
        var sections = new Dictionary<string, List<double>>();
        ActionSection += (section, elapsed) => { if (!sections.TryGetValue(section, out var values)) sections[section] = values = []; values.Add(elapsed); };
        var probe = new FrameProbe { IsHitTestVisible = false, Width = 1, Height = 1, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left };
        ((Grid)Surface.Parent!).Children.Add(probe);
        probe.Submitted = () => { while (frameInputs.TryDequeue(out var input)) frames.Add(clock.Elapsed.TotalMilliseconds - input); };
        FocusRequested += (_, _) => focusedAt = clock.Elapsed.TotalMilliseconds;
        PreviewPublished += (_, full) =>
        {
            usable.TrySetResult();
            if (stable)
            { (full ? stableFull : stableLow).Add(clock.Elapsed.TotalMilliseconds - stableAt); if (full) fullReady?.TrySetResult(); }
            else (full ? fulls : lows).Add(clock.Elapsed.TotalMilliseconds - focusedAt);
        };
        mutations!.Completed += result => { Interlocked.Increment(ref totalOperations); if (result.Error != null) lock (errors) errors.Add(result.Error); };
        var requestedActions = OptionInt("--actions", 1200); var stableSamples = OptionInt("--stable", 30);
        var outputIndex = Array.IndexOf(arguments, "--report");
        var output = outputIndex >= 0 && outputIndex + 1 < arguments.Length ? arguments[outputIndex + 1] : Path.Combine("artifacts", "performance", "window.json");
        var sampling = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        sampling.Tick += (_, _) =>
        {
            using var process = Process.GetCurrentProcess(); maxWorkingSet = Math.Max(maxWorkingSet, process.WorkingSet64);
            maxQueue = Math.Max(maxQueue, Images.QueueCount); maxCache = Math.Max(maxCache, Images.CacheBytes); maxPending = Math.Max(maxPending, mutations.PendingCount);
            maxRealized = Math.Max(maxRealized, Strip.GetVisualDescendants().Count(c => c is ThumbnailView));
        };
        sampling.Start();
        _ = Execute();
        async Task Execute()
        {
            try
            {
                await usable.Task.WaitAsync(TimeSpan.FromSeconds(20));
                var firstUsable = clock.Elapsed.TotalMilliseconds;
                object? metadataTree = null;
                var treeOption = Array.IndexOf(arguments, "--metadata-tree");
                if (treeOption >= 0)
                {
                    var treeRoot = Path.GetFullPath(arguments[treeOption + 1]);
                    if (!await Task.Run(() => Directory.Exists(treeRoot))) throw new DirectoryNotFoundException(treeRoot);
                    var reviewFolder = activeFolder!;
                    loading.Cancel();
                    DiscoverProject(treeRoot); ShowProjectPicks();
                    var treeStarted = clock.Elapsed.TotalMilliseconds;
                    while (!projectComplete)
                    {
                        if (clock.Elapsed.TotalMilliseconds - treeStarted > 300_000) throw new TimeoutException("Metadata tree scan did not complete in five minutes.");
                        await Task.Delay(20);
                    }
                    var treeFolders = folders.Keys.Where(folder => PhotoScreener.Infrastructure.PathGuard.Within(folder, treeRoot)).ToArray();
                    metadataTree = new { root = treeRoot, folders = treeFolders.Length, photos = treeFolders.Sum(folder => folders[folder].Count), picked = treeFolders.Sum(folder => counts[folder].Picked), unavailableFolders, scanMs = clock.Elapsed.TotalMilliseconds - treeStarted };
                    var reviewReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    void ReturnedPreview(PhotoRow row, bool full) { if (PhotoScreener.Infrastructure.PathGuard.Comparer.Equals(row.Photo.Folder, reviewFolder)) reviewReady.TrySetResult(); }
                    PreviewPublished += ReturnedPreview;
                    try { await OpenFolder(reviewFolder); await reviewReady.Task.WaitAsync(TimeSpan.FromSeconds(20)); }
                    finally { PreviewPublished -= ReturnedPreview; }
                }
                if (activeFolder != null && !projectView && filter == PhotoScreener.Core.ReviewFilter.All && rows.Count != folders[activeFolder].Count)
                    throw new InvalidOperationException("The visible All list does not match the current folder collection.");
                Activate(); Surface.Focus(); var inputStarted = clock.Elapsed.TotalMilliseconds;
                await Task.Run(async () =>
                {
                    using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(50));
                    for (var action = 0; action < requestedActions; action++)
                    {
                        await timer.WaitForNextTickAsync(); var arrived = clock.Elapsed.TotalMilliseconds; var ordinal = action;
                        Dispatcher.UIThread.Post(() =>
                        {
                            if (closing) return;
                            var began = clock.Elapsed.TotalMilliseconds; deliveries.Add(began - inputStarted);
                            frameInputs.Enqueue(arrived);
                            var key = ordinal % 5 == 0 ? Key.Space : ordinal / 100 % 2 == 0 ? Key.Right : Key.Left;
                            var keyEvent = new KeyEventArgs { RoutedEvent = KeyDownEvent, Key = key, Source = Surface };
                            Surface.RaiseEvent(keyEvent);
                            Surface.RaiseEvent(new KeyEventArgs { RoutedEvent = KeyUpEvent, Key = key, Source = Surface });
                            if (!keyEvent.Handled)
                            {
                                ignoredKeys++;
                                if (ignoredContexts.Count < 10) ignoredContexts.Add(new { ordinal, key = key.ToString(), active = IsActive, surfaceFocused = Surface.IsFocused, stripFocused = Strip.IsKeyboardFocusWithin, current = current?.Photo.Path });
                            }
                            handlers.Add(clock.Elapsed.TotalMilliseconds - began); feedback.Add(clock.Elapsed.TotalMilliseconds - arrived);
                            probe.InvalidateVisual(); Interlocked.Increment(ref completed);
                        }, DispatcherPriority.Input);
                    }
                });
                while (completed < requestedActions) await Task.Delay(10);
                if (ignoredKeys != 0) throw new InvalidOperationException($"The review surface ignored {ignoredKeys} of {completed} injected keys. This is not a valid input-feedback run.");
                var inputElapsed = (clock.Elapsed.TotalMilliseconds - inputStarted) / 1000;
                await mutations.DrainAsync().WaitAsync(TimeSpan.FromSeconds(60));
                var mixedSeconds = (clock.Elapsed.TotalMilliseconds - inputStarted) / 1000;
                var mixedCopyBytes = mutations.CopiedBytes; var mixedHashBytes = comparer.HashReadBytes;
                stable = true;
                for (var i = 0; i < stableSamples && rows.Count > 0; i++)
                {
                    await Task.Run(Images.ClearCache);
                    fullReady = new(TaskCreationOptions.RunContinuationsAsynchronously); stableAt = clock.Elapsed.TotalMilliseconds;
                    FocusPhoto(rows[(i * 37) % rows.Count]);
                    await fullReady.Task.WaitAsync(TimeSpan.FromSeconds(5));
                }
                sampling.Stop();
                var result = new
                {
                    kind = "real-window fixed-rate routed-key injection; drawing submission, not physical display",
                    timestampUtc = DateTime.UtcNow, os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
                    framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                    runtimeAssembly = typeof(object).Assembly.Location,
                    cpu = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER"), processors = Environment.ProcessorCount,
                    source = activeFolder, export = activeFolder == null ? null : bindings.Resolve(activeFolder).ExportRoot, photos = rows.Count, metadataTree,
                    previewPhysical = new { width = Surface.Bounds.Width * RenderScaling, height = Surface.Bounds.Height * RenderScaling },
                    firstUsableMs = firstUsable, actions = completed, ignoredKeys, inputElapsedSeconds = inputElapsed, deliveredPerSecond = completed / inputElapsed,
                    feedback = Summary(feedback), handler = Summary(handlers), frameSubmission = Summary(frames),
                    sections = sections.ToDictionary(e => e.Key, e => Summary(e.Value)),
                    navigatingLowPublication = Summary(lows), navigatingFullPublication = Summary(fulls),
                    stableDecodedCacheColdLow = Summary(stableLow), stableDecodedCacheColdFull = Summary(stableFull),
                    deliveredCadenceMs = Summary(deliveries.Zip(deliveries.Skip(1), (a, b) => b - a).ToList()),
                    peakSampledWorkingSetMiB = maxWorkingSet / 1048576d, maxQueue, maxCacheMiB = maxCache / 1048576d, maxPending, maxRealizedThumbnails = maxRealized,
                    totalOperations, copiedBytes = mutations.CopiedBytes, hashReadBytes = comparer.HashReadBytes, errors = errors.ToArray(),
                    mutationThroughput = new { mixedSeconds, copiedBytesPerSecond = mixedCopyBytes / mixedSeconds, hashBytesPerSecond = mixedHashBytes / mixedSeconds },
                    thumbnailDiskWrites = Images.ThumbnailWrites,
                    exportIndexScans = new { full = comparer.FullIndexScans, association = comparer.AssociationIndexScans, associationRevisionReads = comparer.AssociationRevisionReads },
                    metadataWrites = metadata.SuccessfulWrites, maintenanceErrors = maintenanceErrors.ToArray(),
                    limitation = "Synthetic diagnostics are not representative photographic evidence. Decoded-cache cold does not establish uncached SSD timing. No physical-display or Mac gate inferred."
                };
                await Write(result);
                var screenshot = Array.IndexOf(arguments, "--screenshot");
                if (screenshot >= 0 && screenshot + 1 < arguments.Length)
                {
                    fullReady = new(TaskCreationOptions.RunContinuationsAsynchronously); FocusPhoto(rows[0]);
                    await fullReady.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    if (arguments.Contains("--zoom-screenshot"))
                    {
                        fullReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
                        Preview.ZoomAt(new Avalonia.Point(Preview.Bounds.Width * .65, Preview.Bounds.Height * .55), 3);
                        await fullReady.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    }
                    for (var retry = 0; retry < 100; retry++)
                    {
                        await Task.Delay(10);
                        var tiles = Strip.GetVisualDescendants().OfType<ThumbnailView>().ToArray();
                        if (tiles.Length > 0 && tiles.All(t => t.Source != null)) break;
                    }
                    using var snapshot = new RenderTargetBitmap(new Avalonia.PixelSize((int)(Bounds.Width * RenderScaling), (int)(Bounds.Height * RenderScaling)), new Avalonia.Vector(96 * RenderScaling, 96 * RenderScaling));
                    snapshot.Render(this);
                    var path = Path.GetFullPath(arguments[screenshot + 1]);
                    await Task.Run(() => { Directory.CreateDirectory(Path.GetDirectoryName(path)!); snapshot.Save(path); });
                }
            }
            catch (Exception e) { sampling.Stop(); await Write(new { error = e.ToString(), actions = completed, ignoredKeys, ignoredContexts, folder = FolderStatus.Text }); }
            CancelPending(); Close();
        }
        Task Write(object result) => Task.Run(() => { Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!); File.WriteAllText(output, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true })); });
        int OptionInt(string key, int fallback) { var index = Array.IndexOf(arguments, key); return index >= 0 && index + 1 < arguments.Length ? int.Parse(arguments[index + 1]) : fallback; }
        static object Summary(List<double> values)
        {
            values.Sort(); double? Percentile(double p) => values.Count == 0 ? null : values[Math.Min(values.Count - 1, (int)Math.Ceiling(values.Count * p) - 1)];
            return new { count = values.Count, p50Ms = Percentile(.5), p95Ms = Percentile(.95), p99Ms = Percentile(.99), maxMs = values.Count == 0 ? (double?)null : values[^1] };
        }
    }
}
