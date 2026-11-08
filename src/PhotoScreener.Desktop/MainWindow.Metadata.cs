using Avalonia.Controls;
using Avalonia.Threading;
using PhotoScreener.Core;
using PhotoScreener.Infrastructure;

namespace PhotoScreener.Desktop;
public partial class MainWindow
{
    private readonly ReviewMetadataStore metadata = new();
    private readonly FolderWatchers watchers = new();
    private readonly Dictionary<string, string> cursors = new(PathGuard.Comparer);
    private readonly Dictionary<string, HashSet<string>> reviewed = new(PathGuard.Comparer);
    private DispatcherTimer? reconcileTimer;
    private long comparisonCompletedContext = -1;
    private (FolderReview Review, long Context, long Interaction, string Folder)? pendingRestore;
    private void InitializeReconciliation()
    {
        mutations!.MaintenanceFailed += error => Dispatcher.UIThread.Post(() => { if (!closing) Text("FolderStatus", "Position/history not saved · " + error); });
        reconcileTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(750) };
        reconcileTimer.Tick += async (_, _) =>
        {
            if (closing || mutations.PendingCount > 0) return;
            foreach (var hint in watchers.TakeHints())
            {
                var dirty = hint.Folder;
                if (hint.PhotosChanged && folders.ContainsKey(dirty)) completeFolders.Remove(dirty);
                if (activeFolder == null || !folders.TryGetValue(activeFolder, out var collection)) continue;
                if (PathGuard.Comparer.Equals(dirty, activeFolder))
                {
                    if (hint.PhotosChanged) { if (current != null) cursors[activeFolder] = current.Photo.Path; await OpenFolder(activeFolder, true); break; }
                    var binding = bindings.Resolve(activeFolder);
                    mutations.RunIdle("watch:" + binding.ExportRoot, () => watchers.Watch(binding.ExportRoot));
                    BeginComparison(collection, ImageContext); continue;
                }
                if (PathGuard.Comparer.Equals(dirty, bindings.Resolve(activeFolder).ExportRoot)) BeginComparison(collection, ImageContext);
            }
        };
        reconcileTimer.Start();
    }
    private void ReadMetadataAndWatch(ExportBinding binding, long context, long capturedSequence)
    {
        mutations!.RunIdle("read:" + binding.SourceFolder, () =>
        {
            FolderReview? review = null; string? error = null;
            try { watchers.Watch(binding.SourceFolder); watchers.Watch(binding.ExportRoot); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { error = "watch unavailable: " + e.Message; }
            try { review = metadata.Read(binding); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { error = e.Message; }
            Dispatcher.UIThread.Post(() =>
            {
                if (closing || context != ImageContext || activeFolder != binding.SourceFolder) return;
                if (review != null)
                {
                    if (!reviewed.TryGetValue(binding.SourceFolder, out var savedHistory)) reviewed[binding.SourceFolder] = savedHistory = [];
                    savedHistory.UnionWith(review.ReviewedRevisions);
                    if (interactionGeneration == capturedSequence && !cursors.ContainsKey(binding.SourceFolder))
                    {
                        pendingRestore = (review, context, capturedSequence, binding.SourceFolder);
                        TryRestoreReview();
                    }
                }
                Text("FolderStatus", binding.SourceFolder + " → " + binding.ExportRoot + (error != null ? " · history Unknown: " + error : reviewed.TryGetValue(binding.SourceFolder, out var history) ? $" · {history.Count} assessed revisions" : " · review history Unknown"));
            }, DispatcherPriority.Background);
        });
    }
    private void TryRestoreReview()
    {
        if (pendingRestore is not { } restore || comparisonCompletedContext != restore.Context) return;
        pendingRestore = null;
        if (closing || restore.Context != ImageContext || interactionGeneration != restore.Interaction || activeFolder != restore.Folder || !folders.TryGetValue(restore.Folder, out var collection)) return;
        // Cursor metadata is a convenience, not content-verification authority. A copied
        // photo has a new native identity/change token while its name, size and mtime survive.
        var row = collection.FirstOrDefault(r => r.Name == restore.Review.CursorName &&
            r.Photo.Revision.Length == restore.Review.CursorRevision?.Length &&
            r.Photo.Revision.ModifiedTicks == restore.Review.CursorRevision?.ModifiedTicks);
        if (row != null) FocusPhoto(row);
        if (restore.Review.Filter != ReviewFilter.All) this.FindControl<ComboBox>("FilterBox")!.SelectedIndex = (int)restore.Review.Filter;
    }
    private void RecordReview(PhotoRow row, PhotoRow? cursor = null)
    {
        if (row.IsExportOnly) return;
        if (!reviewed.TryGetValue(row.Photo.Folder, out var history)) reviewed[row.Photo.Folder] = history = [];
        history.Add(ReviewMetadataStore.RevisionKey(row.Photo));
        cursor ??= row;
        if (cursor.IsExportOnly || !PathGuard.Comparer.Equals(cursor.Photo.Folder, row.Photo.Folder)) cursor = row;
        cursors[row.Photo.Folder] = cursor.Photo.Path;
        var review = new FolderReview(cursor.Name, cursor.Photo.Revision, filter, history.TakeLast(4096).ToArray());
        var protectedFolders = folders.Keys.ToArray();
        var sourceIdentities = comparer.ProtectedDirectoryIdentities;
        mutations!.RunIdle("write:" + row.Photo.Folder, () => metadata.Write(row.Binding, review, protectedFolders, sourceIdentities));
    }
}
