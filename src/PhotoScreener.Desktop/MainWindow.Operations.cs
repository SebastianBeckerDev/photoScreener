using System.Collections.ObjectModel;
using Avalonia.Threading;
using PhotoScreener.Infrastructure;

namespace PhotoScreener.Desktop;
public partial class MainWindow
{
    private readonly FolderComparer comparer = new();
    private ComparisonWorker? comparisons;
    private partial void BeginComparison(ObservableCollection<PhotoRow> collection, long context)
    {
        comparer.ForegroundPending = () => Images.ForegroundPending;
        comparisons ??= new(comparer);
        var captured = collection.ToDictionary(r => r.Photo.Path, PathGuard.Comparer);
        var bound = folders.Values.SelectMany(f => f).Select(r => (r.Photo, r.Binding)).ToArray();
        comparisons.Submit(new(collection.Select(r => (r.Photo, r.Binding)).ToArray(), bound, (photo, observation) => Dispatcher.UIThread.Post(() =>
        {
            if (closing || !captured.TryGetValue(photo.Path, out var row) || !folders.TryGetValue(photo.Folder, out var live) || !live.Contains(row)) return;
            row.Observe(observation);
            if (current == row) Text("CurrentStatus", row.Status);
            if (row.Intent.Pending) RequestMutation(row);
            UpdateCounts();
        }, DispatcherPriority.Background), loading.Token));
    }
    private partial void RequestMutation(PhotoRow row) { }
    private partial void CancelPending() { foreach (var row in folders.Values.SelectMany(f => f)) { row.Intent.Cancel(); row.Notify(); } Surface.Focus(); }
    private partial async Task DrainOperations()
    {
        if (closing && comparisons != null) await comparisons.DisposeAsync();
    }
}
