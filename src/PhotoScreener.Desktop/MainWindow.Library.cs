using System.Collections.ObjectModel;
using Avalonia.Threading;
using PhotoScreener.Infrastructure;

namespace PhotoScreener.Desktop;
public sealed record FolderEntry(string Path, string Label);
public partial class MainWindow
{
    private readonly ObservableCollection<FolderEntry> folderEntries = [];
    private readonly HashSet<string> discoveredFolders = new(PathGuard.Comparer);
    private LibraryWorker? library, projectPicks;
    private CancellationTokenSource libraryContext = new(), pickedContext = new();
    private string? projectRoot;
    private long projectGeneration;
    private bool projectComplete;
    private int unavailableFolders;
    private readonly Dictionary<string, PhotoRow> unmatchedProjectRows = new(PathGuard.Comparer);
    private readonly HashSet<string> matchedProjectExports = new(PathGuard.Comparer);
    private void DiscoverProject(string root)
    {
        if (projectRoot != null && PathGuard.Within(root, projectRoot)) return;
        libraryContext.Cancel(); libraryContext.Dispose(); libraryContext = new();
        var token = libraryContext.Token;
        projectRoot = root; discoveredFolders.Clear(); folderEntries.Clear();
        library ??= new(bindings, comparer);
        library.Submit(new(root, false, batch => Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (token.IsCancellationRequested || !PathGuard.Comparer.Equals(projectRoot, root)) return;
            if (!batch.Complete && discoveredFolders.Add(batch.Folder)) folderEntries.Add(new(batch.Folder, batch.Folder == projectRoot ? Path.GetFileName(projectRoot) : Path.GetRelativePath(projectRoot, batch.Folder)));
        }, DispatcherPriority.Background).GetTask(), token, []));
    }
    private void ScanProjectPicks()
    {
        if (projectRoot == null) return;
        pickedContext.Cancel(); pickedContext.Dispose(); pickedContext = new();
        var token = pickedContext.Token; var generation = ++projectGeneration;
        projectComplete = false; unavailableFolders = 0; projectPicks ??= new(bindings, comparer);
        unmatchedProjectRows.Clear(); matchedProjectExports.Clear();
        var capturedSequence = sequence;
        projectPicks.Submit(new(projectRoot, true, batch => Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (closing || token.IsCancellationRequested || generation != projectGeneration || !projectView) return;
            if (batch.Complete) { projectComplete = true; Text("FolderStatus", $"Project picks · scan complete · {unavailableFolders} unavailable folders"); UpdateCounts(); return; }
            if (!batch.FolderDone && batch.Rows.Count == 0) completeFolders.Remove(batch.Folder);
            if (batch.Error != null) { unavailableFolders++; completeFolders.Remove(batch.Folder); }
            if (!folders.TryGetValue(batch.Folder, out var folder)) { folders[batch.Folder] = folder = []; counts[batch.Folder] = (0, 0, 0); }
            foreach (var item in batch.Rows)
            {
                if (!registered.TryGetValue(item.Photo.Path, out var row) || row.Photo.Revision != item.Photo.Revision)
                {
                    if (row != null) { folder.Remove(row); rows.Remove(row); }
                    row = new(item.Photo, item.Binding); folder.Add(row); RegisterRow(row);
                }
                if (row.Intent.Sequence <= capturedSequence && !row.Intent.Pending) { row.Observe(item.Observation); RegisterAssociation(row); }
                if (row.Picked == true && !rows.Contains(row)) rows.Add(row);
                else if (row.Picked != true && !row.Intent.Pending) rows.Remove(row);
            }
            if (batch.FolderDone && batch.FolderPhotos != null)
            {
                var paths = batch.FolderPhotos.Select(photo => photo.Path).ToHashSet(PathGuard.Comparer);
                foreach (var old in folder.Where(row => !paths.Contains(row.Photo.Path)).ToArray())
                { UnregisterRow(old); folder.Remove(old); rows.Remove(old); }
                if (batch.Error == null) completeFolders.Add(batch.Folder);
                matchedProjectExports.UnionWith(batch.MatchedExportPaths);
                foreach (var path in batch.MatchedExportPaths)
                    if (unmatchedProjectRows.Remove(path, out var resolved)) rows.Remove(resolved);
                var binding = bindings.Resolve(batch.Folder);
                foreach (var export in batch.UnmatchedExports)
                {
                    if (matchedProjectExports.Contains(export.Path) || unmatchedProjectRows.ContainsKey(export.Path)) continue;
                    var unmatched = new PhotoRow(new(export.Path, batch.Folder, export.Name, export.Revision), binding) { IsExportOnly = true };
                    unmatchedProjectRows[export.Path] = unmatched; rows.Add(unmatched);
                }
            }
            else if (batch.FolderDone && batch.Error != null)
                foreach (var unavailable in folder)
                { unavailable.Observe(new(PhotoScreener.Core.PickStatus.Unavailable, unavailable.Observation.Copies, Error: batch.Error)); ForgetAssociation(unavailable); }
            if (current == null || !rows.Contains(current)) { if (rows.Count > 0) FocusPhoto(rows[0]); else ClearFocus(); }
            Text("FolderStatus", $"Project picks · scanning {batch.Folder} · totals incomplete · {unavailableFolders} unavailable"); UpdateCounts();
        }, DispatcherPriority.Background).GetTask(), token, registered.Values.Select(r => (r.Photo, r.Binding)).ToArray()));
    }
}
