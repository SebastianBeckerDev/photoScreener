using System.Collections.ObjectModel;
using Avalonia.Threading;
using Avalonia.Controls;
using Avalonia.Layout;
using PhotoScreener.Infrastructure;

namespace PhotoScreener.Desktop;
public partial class MainWindow
{
    private readonly FolderComparer comparer = new();
    private ComparisonWorker? comparisons;
    private MutationCoordinator? mutations;
    private readonly Dictionary<string, PhotoRow> registered = new(PathGuard.Comparer);
    private readonly Dictionary<string, (int Total, int Picked, int Checking)> counts = new(PathGuard.Comparer);
    private readonly ObservableCollection<PhotoRow> operationRows = [];
    private readonly record struct AssociationId(string Root, string Name, string Digest);
    private readonly Dictionary<AssociationId, HashSet<PhotoRow>> associations = [];
    private readonly Dictionary<PhotoRow, AssociationId> rowAssociations = [];
    private AssociationId? AssociationKey(PhotoRow row) => row.IsExportOnly || row.VerifiedSourceSha256 == null ? null : new(OperatingSystem.IsWindows() ? row.Binding.ExportRoot.ToUpperInvariant() : row.Binding.ExportRoot, row.Name, row.VerifiedSourceSha256);
    private void ForgetAssociation(PhotoRow row)
    {
        if (row.SetSharedFolderCount(0) && current == row) Text("CurrentStatus", row.Status);
        if (!rowAssociations.Remove(row, out var key) || !associations.TryGetValue(key, out var group)) return;
        group.Remove(row); if (group.Count == 0) associations.Remove(key); else UpdateSharedGroup(group);
    }
    private void RegisterAssociation(PhotoRow row)
    {
        if (AssociationKey(row) is not { } key) { ForgetAssociation(row); return; }
        if (rowAssociations.GetValueOrDefault(row) != key) ForgetAssociation(row);
        if (!associations.TryGetValue(key, out var group)) associations[key] = group = [];
        var added = group.Add(row); rowAssociations[row] = key;
        if (added) UpdateSharedGroup(group);
    }
    private void UpdateSharedGroup(HashSet<PhotoRow> group)
    {
        var count = group.Select(row => row.Photo.Folder).Distinct(PathGuard.Comparer).Count();
        foreach (var row in group)
            if (row.SetSharedFolderCount(count) && current == row) Text("CurrentStatus", row.Status);
    }
    private void UnregisterRow(PhotoRow row)
    {
        if (registered.GetValueOrDefault(row.Photo.Path) != row) return;
        registered.Remove(row.Photo.Path); ForgetAssociation(row);
        var count = counts.GetValueOrDefault(row.Photo.Folder);
        counts[row.Photo.Folder] = (count.Total - 1, count.Picked - (row.Picked == true ? 1 : 0), count.Checking - (!row.Picked.HasValue ? 1 : 0));
    }
    private void RegisterRow(PhotoRow row)
    {
        if (row.IsExportOnly || registered.GetValueOrDefault(row.Photo.Path) == row) return;
        if (registered.TryGetValue(row.Photo.Path, out var replaced)) UnregisterRow(replaced);
        registered[row.Photo.Path] = row; mutations?.RegisterPhoto(row.Photo, row.Binding);
        var previous = counts.GetValueOrDefault(row.Photo.Folder);
        counts[row.Photo.Folder] = (previous.Total + 1, previous.Picked + (row.Picked == true ? 1 : 0), previous.Checking + (!row.Picked.HasValue ? 1 : 0));
        row.PickedChanged += (changed, oldValue, newValue) =>
        {
            if (registered.GetValueOrDefault(changed.Photo.Path) != changed) return;
            var count = counts[changed.Photo.Folder];
            counts[changed.Photo.Folder] = (count.Total, count.Picked - (oldValue == true ? 1 : 0) + (newValue == true ? 1 : 0), count.Checking - (!oldValue.HasValue ? 1 : 0) + (!newValue.HasValue ? 1 : 0));
        };
    }
    private partial void BeginComparison(ObservableCollection<PhotoRow> collection, long context)
    {
        comparer.ForegroundPending = () => Images.ForegroundPending;
        comparisons ??= new(comparer);
        var captured = collection.ToDictionary(r => r.Photo.Path, r => (Row: r, Sequence: r.Intent.Sequence), PathGuard.Comparer);
        var bound = folders.Values.SelectMany(f => f).Select(r => (r.Photo, r.Binding)).ToArray();
        comparisons.Submit(new(collection.Select(r => (r.Photo, r.Binding)).ToArray(), bound, observations => Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (closing) return;
            if (observations.Count == 0)
            {
                if (context == ImageContext) { comparisonCompletedContext = context; TryRestoreReview(); }
                return;
            }
            foreach (var (photo, observation) in observations)
            {
                if (!captured.TryGetValue(photo.Path, out var snapshot)) continue;
                var row = snapshot.Row;
                if (registered.GetValueOrDefault(photo.Path) != row || row.Intent.Sequence != snapshot.Sequence || row.Intent.Pending) continue;
                row.Observe(observation); RegisterAssociation(row); SynchronizeFilteredRow(row);
                if (current == row) Text("CurrentStatus", row.Status);
            }
            UpdateCounts();
        }, DispatcherPriority.Background).GetTask(), loading.Token));
    }
    private partial void RequestMutation(PhotoRow row)
    {
        if (row.IsExportOnly) return;
        if (registered.TryGetValue(row.Photo.Path, out var live) && live.Photo.Revision != row.Photo.Revision)
        { row.Fail("Source revision changed; review the current version before requesting a change."); return; }
        mutations ??= CreateMutations();
        RegisterRow(row);
        foreach (var old in operationRows.Where(old => old != row && PathGuard.Comparer.Equals(old.Photo.Path, row.Photo.Path) && old.Intent.Sequence < row.Intent.Sequence).ToArray()) operationRows.Remove(old);
        var desired = row.Intent.Requested;
        if (!operationRows.Contains(row) && operationRows.Count < 1024) operationRows.Add(row);
        if (desired.HasValue && AssociationKey(row) is { } key && associations.TryGetValue(key, out var group))
            foreach (var associated in group)
                if (registered.GetValueOrDefault(associated.Photo.Path) == associated)
                { associated.Intent.AdoptRequest(desired.Value, row.Intent.Sequence); associated.Notify(); }
        if (!mutations.Submit(new(row.Photo, row.Binding, desired, row.Intent.UnknownParity, row.Intent.Sequence, row.Observation.Sha256)))
            row.Fail("Pending-operation limit reached. Retry after queued work finishes.");
    }
    private MutationCoordinator CreateMutations()
    {
        var coordinator = new MutationCoordinator(comparer);
        coordinator.Completed += result => Dispatcher.UIThread.Post(() =>
        {
            var propagated = new HashSet<PhotoRow>();
            foreach (var photo in result.Photos)
            {
                registered.TryGetValue(photo.Path, out var requested);
                if (requested?.Photo.Revision != photo.Revision) requested = operationRows.FirstOrDefault(row => PathGuard.Comparer.Equals(row.Photo.Path, photo.Path) && row.Photo.Revision == photo.Revision);
                if (requested != null && AssociationKey(requested) is { } key && associations.TryGetValue(key, out var group))
                    propagated.UnionWith(group.Where(row => row.Intent.Sequence == result.Sequence));
            }
            foreach (var photo in result.Photos)
            {
                registered.TryGetValue(photo.Path, out var row);
                if (row?.Photo.Revision != photo.Revision) row = operationRows.FirstOrDefault(item => PathGuard.Comparer.Equals(item.Photo.Path, photo.Path) && item.Photo.Revision == photo.Revision);
                if (row == null) continue;
                var newer = row.Intent.Sequence > result.Sequence;
                if (!newer && result.Desired.HasValue) row.Intent.AdoptRequest(result.Desired.Value, result.Sequence);
                row.Observe(result.Observation);
                if (registered.GetValueOrDefault(row.Photo.Path) == row) SynchronizeFilteredRow(row);
                RegisterAssociation(row);
                if (result.Observation.Picked.HasValue)
                { var target = row.Binding.ExportRoot; coordinator.RunIdle("watch:" + target, () => watchers.Watch(target)); }
                if (result.Error != null && !newer) row.Fail(result.Error);
                else if (result.Canceled && !newer) { row.Intent.Cancel(); row.Notify(); }
                else if (result.Observation.Picked.HasValue && !newer) { row.Intent.Complete(result.Observation.Picked.Value, result.Sequence); row.Notify(); }
                if (current == row) Text("CurrentStatus", row.Status);
                if (!newer && result.Error == null) operationRows.Remove(row);
            }
            foreach (var stale in propagated.Where(row => row.Intent.Sequence == result.Sequence && !result.Photos.Any(photo => PathGuard.Comparer.Equals(photo.Path, row.Photo.Path) && photo.Revision == row.Photo.Revision)))
            {
                // The worker's fresh source census can invalidate an optimistic
                // shared-row request after an edit or binding change.
                stale.Observe(new(PhotoScreener.Core.PickStatus.Checking, stale.Observation.Copies, Error: "Shared association changed; Refresh this folder before requesting another change."));
                stale.Intent.Cancel(); stale.Notify(); ForgetAssociation(stale); completeFolders.Remove(stale.Photo.Folder);
                if (!operationRows.Contains(stale) && operationRows.Count < 1024) operationRows.Add(stale);
                if (current == stale) Text("CurrentStatus", stale.Status);
            }
            UpdateCounts();
        }, DispatcherPriority.Normal);
        return coordinator;
    }
    private void SynchronizeFilteredRow(PhotoRow row)
    {
        if (filter == PhotoScreener.Core.ReviewFilter.All || projectView || row.Photo.Folder != activeFolder) return;
        if (PhotoScreener.Core.ReviewRules.Matches(filter, row.Picked))
        { if (!rows.Contains(row)) rows.Add(row); if (current == null) FocusPhoto(row); }
        else if (rows.Contains(row))
        {
            var index = rows.IndexOf(row); applyingSelection = true; rows.Remove(row); applyingSelection = false;
            if (current == row) { if (rows.Count == 0) ClearFocus(); else FocusPhoto(rows[Math.Min(index, rows.Count - 1)]); }
        }
    }
    private partial void CancelPending()
    {
        mutations?.CancelAll();
        foreach (var row in folders.Values.SelectMany(f => f).Concat(operationRows).Distinct()) { row.Intent.Cancel(); row.Notify(); }
        operationRows.Clear();
        if (current != null) Text("CurrentStatus", current.Status); Surface.Focus();
    }
    private partial async Task DrainOperations()
    {
        if (mutations != null)
        {
            await mutations.DrainAsync();
        }
    }
    private async Task<string?> ChooseFailedChanges(string message)
    {
        var dialog = new Window { Title = "Pending export changes", Width = 470, Height = 200, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var label in new[] { "Keep reviewing", "Retry", "Cancel and close" })
        {
            var button = new Button { Content = label };
            ToolTip.SetTip(button, label switch
            {
                "Keep reviewing" => "Return to the photos and keep failed requests available for retry.",
                "Retry" => "Retry the failed export changes before continuing.",
                _ => "Discard unfinished requests and continue closing or switching. Completed export changes remain on disk."
            });
            button.Click += (_, _) => dialog.Close(label); buttons.Children.Add(button);
        }
        dialog.Content = new StackPanel { Margin = new Avalonia.Thickness(20), Spacing = 24, Children = { new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap }, buttons } };
        return await dialog.ShowDialog<string?>(this);
    }
}
