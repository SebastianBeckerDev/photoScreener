using System.Diagnostics;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using PhotoScreener.Desktop.Diagnostics;

namespace PhotoScreener.Desktop;
public partial class MainWindow
{
    private partial void StartBenchmark()
    {
        var feedback = new List<double>(); var frames = new List<double>();
        var lows = new List<double>(); var fulls = new List<double>();
        var clock = Stopwatch.StartNew(); var started = clock.Elapsed.TotalMilliseconds;
        double? pendingFrame = null; string? pendingPhoto = null;
        var maxWorkingSet = 0L; var maxQueue = 0; var maxCache = 0L; var actions = 0; var ready = false;
        var probe = new FrameProbe { IsHitTestVisible = false, Width = 1, Height = 1, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left };
        ((Grid)Surface.Parent!).Children.Add(probe);
        probe.Submitted = () => { if (pendingFrame.HasValue) { frames.Add(clock.Elapsed.TotalMilliseconds - pendingFrame.Value); pendingFrame = null; } };
        PreviewPublished += (row, full) =>
        {
            if (pendingPhoto != row.Photo.Path) return;
            (full ? fulls : lows).Add(clock.Elapsed.TotalMilliseconds - started);
        };
        var countIndex = Array.IndexOf(arguments, "--actions");
        var requestedActions = countIndex >= 0 && countIndex + 1 < arguments.Length ? int.Parse(arguments[countIndex + 1]) : 1200;
        var outputIndex = Array.IndexOf(arguments, "--report");
        var output = outputIndex >= 0 && outputIndex + 1 < arguments.Length ? arguments[outputIndex + 1] : Path.Combine("artifacts", "performance", "window.json");
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        timer.Tick += async (_, _) =>
        {
            if (current == null)
            {
                if (clock.Elapsed.TotalSeconds > 10)
                {
                    timer.Stop(); Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
                    File.WriteAllText(output, JsonSerializer.Serialize(new { error = "No focused photo", folder = this.FindControl<TextBlock>("FolderStatus")!.Text })); Close();
                }
                return;
            }
            if (!ready) { ready = true; clock.Restart(); }
            using var process = Process.GetCurrentProcess();
            maxWorkingSet = Math.Max(maxWorkingSet, process.WorkingSet64); maxQueue = Math.Max(maxQueue, Images.QueueCount); maxCache = Math.Max(maxCache, Images.CacheBytes);
            if (actions >= requestedActions)
            {
                timer.Stop();
                var result = new
                {
                    kind = "real-window routed-key injection; drawing submission, not physical display",
                    timestampUtc = DateTime.UtcNow, os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
                    framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                    cpu = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER"), processors = Environment.ProcessorCount,
                    source = activeFolder, photos = rows.Count, previewPhysical = new { width = Preview.Bounds.Width * RenderScaling, height = Preview.Bounds.Height * RenderScaling },
                    actions, elapsedSeconds = clock.Elapsed.TotalSeconds,
                    feedback = Summary(feedback), frameSubmission = Summary(frames), lowPublication = Summary(lows), fullPublication = Summary(fulls),
                    peakSampledWorkingSetMiB = maxWorkingSet / 1048576d, maxQueue, maxCacheMiB = maxCache / 1048576d,
                    limitation = "Synthetic diagnostic unless supplied corpus meets reference contract. No physical-display, uncached-SSD or Mac gate inferred."
                };
                await Task.Run(() => { Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!); File.WriteAllText(output, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true })); });
                Close(); return;
            }
            started = clock.Elapsed.TotalMilliseconds; pendingFrame = started;
            var key = actions % 5 == 0 ? Key.Space : actions / 100 % 2 == 0 ? Key.Right : Key.Left;
            Surface.RaiseEvent(new KeyEventArgs { RoutedEvent = KeyDownEvent, Key = key, Source = Surface });
            Surface.RaiseEvent(new KeyEventArgs { RoutedEvent = KeyUpEvent, Key = key, Source = Surface });
            feedback.Add(clock.Elapsed.TotalMilliseconds - started); pendingPhoto = current?.Photo.Path;
            probe.InvalidateVisual(); actions++;
        };
        timer.Start();
        static object Summary(List<double> values)
        {
            values.Sort();
            double? Percentile(double p) => values.Count == 0 ? null : values[Math.Min(values.Count - 1, (int)Math.Ceiling(values.Count * p) - 1)];
            return new { count = values.Count, p50Ms = Percentile(.5), p95Ms = Percentile(.95), p99Ms = Percentile(.99), maxMs = values.Count == 0 ? (double?)null : values[^1] };
        }
    }
}
