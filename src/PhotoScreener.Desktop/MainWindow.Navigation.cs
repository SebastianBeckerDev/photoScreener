using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PhotoScreener.Core;
using PhotoScreener.Infrastructure;

namespace PhotoScreener.Desktop;

public partial class MainWindow
{
    internal sealed record RecentDirectory(string Path, bool Available)
    {
        public string Label => Path + (Available ? "" : " · unavailable");
        public double Opacity => Available ? 1 : .45;
        public override string ToString() => Label;
    }
    internal sealed class DirectoryItem(string path) : TreeViewItem
    {
        protected override Type StyleKeyOverride => typeof(TreeViewItem);
        public string Path { get; } = path;
        public ObservableCollection<TreeViewItem> Children { get; } = [];
        public bool ChildrenLoaded { get; set; }
        public bool Busy { get; set; }
        public bool Available { get; set; } = true;
        public int Offset { get; set; }
    }
    private readonly List<string> recentSources = [], recentExports = [];
    private CancellationTokenSource navigationContext = new();
    private string? candidateExport, navigationRoot, navigationSource;
    private string? observedExport;
    private bool? exportAvailable;
    private bool updatingNavigation, restoringLayout;
    private readonly SemaphoreSlim navigationIo = new(2, 2);
    private bool ExportMode => DirectoryRole.SelectedIndex == 1;
    private async Task<T> ReadDirectories<T>(Func<T> read, CancellationToken token)
    {
        await navigationIo.WaitAsync(token);
        try { return await Task.Run(read, token); }
        finally { navigationIo.Release(); }
    }

    private void InitializeNavigation()
    {
        FoldersResizeThumb.DragDelta += (_, e) => FoldersPanel.Width = Math.Clamp(FoldersPanel.Width + e.Vector.X, 180, 420);
        InfoResizeThumb.DragDelta += (_, e) => InfoPanel.Width = Math.Clamp(InfoPanel.Width - e.Vector.X, 180, 360);
        FoldersResizeThumb.DragCompleted += (_, _) => { SaveLayout(); Surface.Focus(); };
        InfoResizeThumb.DragCompleted += (_, _) => { SaveLayout(); Surface.Focus(); };
        RecentFolders.ItemTemplate = new FuncDataTemplate<RecentDirectory>((entry, _) =>
        {
            if (entry == null) return new TextBlock();
            var text = new TextBlock { Text = entry.Label, Opacity = entry.Opacity, TextTrimming = TextTrimming.CharacterEllipsis };
            ToolTip.SetTip(text, entry.Label);
            var open = new MenuItem { Header = OperatingSystem.IsMacOS() ? "Open in Finder" : "Open in Explorer" };
            ToolTip.SetTip(open, "Open this recent directory in the system file manager.");
            open.Click += async (_, _) => await RevealDirectory(entry.Path);
            text.ContextMenu = new ContextMenu { ItemsSource = new[] { open } }; return text;
        });
        FoldersToggleButton.Click += (_, _) =>
        {
            FoldersPanel.IsVisible = !FoldersPanel.IsVisible;
            FoldersToggleButton.Content = FoldersPanel.IsVisible ? "‹" : "›";
            SaveLayout(); Surface.Focus();
        };
        DirectoryRole.SelectionChanged += (_, _) => { if (!updatingNavigation) UpdateDirectoryContext(true); };
        RecentFolders.SelectionChanged += async (_, _) =>
        {
            if (updatingNavigation || RecentFolders.SelectedItem is not RecentDirectory recent) return;
            if (!recent.Available) { Text("FolderStatus", "Directory unavailable · " + recent.Path + " · Connect the drive and Refresh."); return; }
            await SelectDirectory(recent.Path);
        };
        DirectoryTree.AddHandler(PointerReleasedEvent, async (_, e) =>
        {
            if (updatingNavigation || e.InitialPressMouseButton != MouseButton.Left || e.Source is not Visual visual) return;
            if (visual is Button || visual.GetVisualAncestors().OfType<Button>().Any()) return;
            var item = visual as DirectoryItem ?? visual.GetVisualAncestors().OfType<DirectoryItem>().FirstOrDefault();
            if (item != null && DirectoryTree.SelectedItem == item) await SelectDirectory(item.Path);
        }, RoutingStrategies.Bubble, handledEventsToo: true);
        DirectoryTree.AddHandler(KeyDownEvent, async (_, e) =>
        {
            if (e.Source is Button) return;
            if (e.Key == Key.Enter && DirectoryTree.SelectedItem is DirectoryItem item)
            { e.Handled = true; await SelectDirectory(item.Path); }
        }, RoutingStrategies.Tunnel);
        DirectoryUpButton.Click += async (_, _) =>
        {
            var path = ExportMode ? candidateExport : activeFolder;
            if (path != null && Path.GetDirectoryName(path) is { } parent) await SelectDirectory(parent);
        };
        OpenButton.Click += async (_, _) =>
        {
            if (!ExportMode) await ChooseSource();
            else
            {
                var selected = await StorageProvider.OpenFolderPickerAsync(new() { Title = "Browse export target candidate", AllowMultiple = false });
                if (selected.FirstOrDefault()?.TryGetLocalPath() is { } path) await SelectDirectory(path);
                Surface.Focus();
            }
        };
        TargetButton.Click += async (_, _) => { if (candidateExport != null) await ApplyExportTarget(candidateExport); };
        RefreshButton.Click += async (_, _) =>
        {
            UpdateDirectoryContext(true);
            if (!ExportMode && activeFolder != null) await OpenFolder(activeFolder, true);
            Surface.Focus();
        };
        SourceLink.Click += async (_, _) => { if (activeFolder != null) await RevealDirectory(activeFolder); };
        ExportLink.Click += async (_, _) => { if (activeFolder != null) await RevealDirectory(bindings.Resolve(activeFolder).ExportRoot); };
        Preview.ContextMenu = new ContextMenu();
        var repair = new MenuItem { Header = "Retry selection / repair NEF companion" };
        ToolTip.SetTip(repair, "Retry the current request or copy a missing same-stem NEF for an already picked JPEG. Ambiguous exports require an ownership choice.");
        repair.Click += async (_, _) =>
        {
            if (current is not { IsExportOnly: false } row) return;
            if (row.Observation.Status == PickStatus.Ambiguous) await ResolveOwnership(row);
            else
            {
                if (!row.Intent.Pending && row.Picked.HasValue) row.Intent.AdoptRequest(row.Picked.Value, ++sequence);
                row.Notify(); RequestMutation(row);
            }
            Surface.Focus();
        };
        Preview.ContextMenu.ItemsSource = new[] { repair };
        var project = new MenuItem { Header = "Review project picked photos" };
        ToolTip.SetTip(project, "Progressively review picks across discovered source folders, using each folder's effective target.");
        project.Click += (_, _) => ShowProjectPicks();
        SourceLink.ContextMenu = new ContextMenu { ItemsSource = new[] { project } };
        UpdateCounts();
    }
    internal async Task SelectDirectory(string path)
    {
        var export = ExportMode; var source = activeFolder; var token = navigationContext.Token;
        bool available;
        try { available = await ReadDirectories(() => DirectoryAvailable(path), token); }
        catch (OperationCanceledException) { return; }
        if (closing || token.IsCancellationRequested || source != activeFolder || export != ExportMode) return;
        if (!available)
        {
            Text("FolderStatus", "Directory unavailable · " + path + " · Connect the drive and Refresh.");
            foreach (var item in TreeItems()) if (PathGuard.Comparer.Equals(item.Path, path)) { item.Available = false; StyleDirectory(item); }
            return;
        }
        if (export) { candidateExport = PathGuard.Canonical(path); UpdateDirectoryContext(true, false); }
        else if (!PathGuard.Comparer.Equals(path, activeFolder)) await OpenSourceSelection(path);
        Surface.Focus();
    }
    private static bool DirectoryAvailable(string path)
    {
        try
        {
            PathGuard.ValidateNoLinks(path);
            using var entries = Directory.EnumerateFileSystemEntries(path).GetEnumerator(); entries.MoveNext(); return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or System.ComponentModel.Win32Exception) { return false; }
    }
    private async Task RevealDirectory(string path)
    {
        try
        {
            if (!await Task.Run(() => DirectoryAvailable(path)))
            { Text("FolderStatus", "Directory unavailable or not created yet · " + path + " · Export directories are created on the first pick."); return; }
            await Task.Run(() =>
            {
                var start = OperatingSystem.IsMacOS() ? new ProcessStartInfo("/usr/bin/open") { UseShellExecute = false, CreateNoWindow = true }
                    : new ProcessStartInfo("explorer.exe") { UseShellExecute = false, CreateNoWindow = true };
                start.ArgumentList.Add(path); using var process = Process.Start(start);
            });
        }
        catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception or InvalidOperationException) { Text("FolderStatus", "Could not open directory · " + e.Message); }
        Surface.Focus();
    }
    private IEnumerable<DirectoryItem> TreeItems()
    {
        IEnumerable<DirectoryItem> Walk(IEnumerable<TreeViewItem> items)
        {
            foreach (var item in items.OfType<DirectoryItem>()) { yield return item; foreach (var child in Walk(item.Children)) yield return child; }
        }
        return Walk((DirectoryTree.ItemsSource as IEnumerable<TreeViewItem>) ?? []);
    }
    private void StyleDirectory(DirectoryItem item)
    {
        var active = activeFolder == null ? null : ExportMode ? bindings.Resolve(activeFolder).ExportRoot : activeFolder;
        var selected = PathGuard.Comparer.Equals(item.Path, active);
        var candidate = ExportMode && PathGuard.Comparer.Equals(item.Path, candidateExport) && !selected;
        var name = Path.GetFileName(item.Path); if (name.Length == 0) name = item.Path;
        item.Header = new TextBlock { Text = name + (item.Available ? "" : " · unavailable"), TextTrimming = TextTrimming.CharacterEllipsis };
        item.Background = selected ? new SolidColorBrush(Color.Parse("#204D43")) : Brushes.Transparent;
        item.BorderBrush = candidate ? new SolidColorBrush(Color.Parse("#50E5C3")) : Brushes.Transparent;
        item.BorderThickness = new Thickness(1); item.Opacity = item.Available ? 1 : .45;
        ToolTip.SetTip(item, item.Path + (selected ? " · current directory" : candidate ? " · candidate, not applied" : ""));
    }
    private DirectoryItem MakeDirectory(string path, CancellationToken token)
    {
        var item = new DirectoryItem(path); item.ItemsSource = item.Children;
        item.Children.Add(new TreeViewItem { Header = "Expand to load…", IsEnabled = false }); StyleDirectory(item);
        item.PropertyChanged += async (_, change) =>
        {
            if (change.Property == TreeViewItem.IsExpandedProperty && item.IsExpanded && !item.ChildrenLoaded && !item.Busy && !token.IsCancellationRequested)
                await LoadChildren(item, token);
        };
        var open = new MenuItem { Header = OperatingSystem.IsMacOS() ? "Open in Finder" : "Open in Explorer" };
        ToolTip.SetTip(open, "Open this directory in the system file manager without changing the current selection.");
        open.Click += async (_, _) => await RevealDirectory(path);
        item.ContextMenu = new ContextMenu { ItemsSource = new[] { open } }; return item;
    }
    private async Task LoadChildren(DirectoryItem parent, CancellationToken token)
    {
        parent.Busy = true;
        var export = ExportMode;
        var currentPath = export ? candidateExport : activeFolder;
        var excluded = export ? [] : folders.Keys.Append(activeFolder ?? parent.Path).Select(bindings.Resolve).Select(b => b.ExportRoot).ToArray();
        try
        {
            var offset = parent.Offset;
            var page = await ReadDirectories(() =>
            {
                PathGuard.ValidateNoLinks(parent.Path);
                var result = new List<string>(201); var skipped = 0;
                foreach (var path in Directory.EnumerateDirectories(parent.Path))
                {
                    token.ThrowIfCancellationRequested();
                    if (excluded.Any(root => PathGuard.Within(path, root))) continue;
                    if (skipped++ < offset) continue;
                    result.Add(path); if (result.Count == 201) break;
                }
                return result;
            }, token);
            if (closing || token.IsCancellationRequested) return;
            var currentAvailable = !export || currentPath == null || page.Contains(currentPath, PathGuard.Comparer)
                || await ReadDirectories(() => DirectoryAvailable(currentPath), token);
            if (closing || token.IsCancellationRequested) return;
            updatingNavigation = true;
            if (parent.Offset == 0) parent.Children.Clear();
            else if (parent.Children.LastOrDefault() is { } more) parent.Children.Remove(more);
            foreach (var path in page.Take(200))
                if (!parent.Children.OfType<DirectoryItem>().Any(item => PathGuard.Comparer.Equals(item.Path, path))) parent.Children.Add(MakeDirectory(path, token));
            parent.Offset += Math.Min(200, page.Count); parent.ChildrenLoaded = true;
            if (page.Count > 200)
            {
                var moreButton = new Button { Content = "Load more directories…" };
                ToolTip.SetTip(moreButton, "Load the next 200 directories without scanning descendants.");
                moreButton.Click += async (_, _) => { if (!parent.Busy) await LoadChildren(parent, token); };
                parent.Children.Add(new TreeViewItem { Header = moreButton });
            }
            var currentItem = parent.Children.OfType<DirectoryItem>().FirstOrDefault(i => PathGuard.Comparer.Equals(i.Path, currentPath));
            if (currentPath != null && PathGuard.Comparer.Equals(Path.GetDirectoryName(currentPath), parent.Path) && currentItem == null)
            {
                currentItem = MakeDirectory(currentPath, token);
                currentItem.Available = currentAvailable;
                StyleDirectory(currentItem); parent.Children.Insert(0, currentItem);
            }
            if (currentItem is { Available: true })
            {
                currentItem.IsExpanded = true;
                Dispatcher.UIThread.Post(() =>
                {
                    if (!closing && !token.IsCancellationRequested) currentItem.BringIntoView(new Rect(0, 0, currentItem.Bounds.Width, 32));
                }, DispatcherPriority.Loaded);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            if (!token.IsCancellationRequested)
            {
                parent.Available = false; StyleDirectory(parent); parent.Children.Clear();
                parent.Children.Add(new TreeViewItem { Header = "Unavailable · Refresh to retry", IsEnabled = false });
                Text("FolderStatus", "Directory unavailable · " + e.Message);
            }
        }
        finally { parent.Busy = false; updatingNavigation = false; }
    }
    private void UpdateDirectoryContext(bool rebuild = false, bool resetCandidate = true)
    {
        if (closing) return;
        if (activeFolder != navigationSource) { navigationSource = activeFolder; candidateExport = activeFolder == null ? null : bindings.Resolve(activeFolder).ExportRoot; rebuild = true; }
        if (resetCandidate && candidateExport == null && activeFolder != null) candidateExport = bindings.Resolve(activeFolder).ExportRoot;
        var activeExport = activeFolder == null ? null : bindings.Resolve(activeFolder).ExportRoot;
        if (observedExport != activeExport || rebuild) { observedExport = activeExport; exportAvailable = null; }
        SourceLink.Content = new TextBlock { Text = "Source: " + DirectoryCaption(activeFolder), TextTrimming = TextTrimming.CharacterEllipsis };
        ExportLink.Content = new TextBlock { Text = "Export: " + DirectoryCaption(activeExport), TextTrimming = TextTrimming.CharacterEllipsis };
        SourceLink.IsEnabled = ExportLink.IsEnabled = activeFolder != null;
        ToolTip.SetTip(SourceLink, "Open source in Explorer / Finder · " + activeFolder);
        ToolTip.SetTip(ExportLink, "Open export in Explorer / Finder · " + activeExport + " · Created on first pick if missing.");
        TargetButton.IsVisible = ExportCandidateLabel.IsVisible = ExportMode;
        TargetButton.IsEnabled = activeFolder != null && candidateExport != null && !PathGuard.Comparer.Equals(candidateExport, activeExport);
        Text("ExportCandidateLabel", "Candidate: " + (candidateExport ?? "Choose a directory") + "\nCurrent target: " + (activeExport ?? "—"));
        Text("DirectoryContext", ExportMode ? "Export for: " + (activeFolder ?? "Open a source folder first") : activeFolder ?? "Choose a source folder");
        var selected = ExportMode ? candidateExport : activeFolder;
        var root = selected == null ? null : Path.GetDirectoryName(selected) ?? selected;
        if (!rebuild && navigationRoot == root)
        {
            foreach (var item in TreeItems()) StyleDirectory(item);
            _ = RefreshExportAvailability(navigationContext.Token); return;
        }
        navigationRoot = root; navigationContext.Cancel(); navigationContext.Dispose(); navigationContext = new();
        var token = navigationContext.Token;
        updatingNavigation = true;
        DirectoryTree.ItemsSource = root == null ? Array.Empty<TreeViewItem>() : new TreeViewItem[] { MakeDirectory(root, token) };
        if (DirectoryTree.ItemsSource is TreeViewItem[] { Length: > 0 } roots) roots[0].IsExpanded = true;
        updatingNavigation = false;
        _ = RefreshRecents(token);
        _ = RefreshExportAvailability(token);
    }
    private static string DirectoryCaption(string? path)
    {
        if (path == null) return "—";
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        return name.Length > 0 ? name : path;
    }
    private async Task RefreshExportAvailability(CancellationToken token)
    {
        var source = activeFolder; var target = source == null ? null : bindings.Resolve(source).ExportRoot;
        if (target == null) return;
        try
        {
            var available = exportAvailable == true || await ReadDirectories(() => DirectoryAvailable(target), token);
            if (closing || token.IsCancellationRequested || source != activeFolder || target != bindings.Resolve(source!).ExportRoot) return;
            var changed = exportAvailable != available; exportAvailable = available;
            ExportLink.Content = new TextBlock { Text = "Export: " + DirectoryCaption(target) + (available ? "" : " · missing"), TextTrimming = TextTrimming.CharacterEllipsis };
            ExportLink.Foreground = new SolidColorBrush(Color.Parse(available ? "#BCC5D4" : "#E8B889"));
            ToolTip.SetTip(ExportLink, "Open export in Explorer / Finder · " + target +
                (available ? "" : " · Unavailable or not created yet. Export directories are created on the first pick."));
            if (changed && ExportMode) _ = RefreshRecents(token);
        }
        catch (OperationCanceledException) { }
    }
    private async Task RefreshRecents(CancellationToken token)
    {
        var paths = (ExportMode ? recentExports : recentSources).ToArray();
        try
        {
            var items = await ReadDirectories(() => paths.Select(path => { token.ThrowIfCancellationRequested(); return new RecentDirectory(path, DirectoryAvailable(path)); }).ToArray(), token);
            if (closing || token.IsCancellationRequested) return;
            updatingNavigation = true;
            RecentFolders.ItemsSource = items; RecentFolders.SelectedIndex = -1;
            updatingNavigation = false;
        }
        catch (OperationCanceledException) { }
    }
    private static void RememberRecent(List<string> entries, string folder)
    {
        entries.RemoveAll(path => PathGuard.Comparer.Equals(path, folder)); entries.Insert(0, folder);
        if (entries.Count > 10) entries.RemoveRange(10, entries.Count - 10);
    }
    private void RememberExportDirectory(string folder)
    {
        RememberRecent(recentExports, folder);
        if (sessionSettings != null) mutations?.RunIdle("recent-export", () => sessionSettings.RememberExport(folder));
    }
    private SessionSettingsStore.Preferences LayoutPreferences() => new(recentSources.ToArray(), recentExports.ToArray(),
        FoldersPanel.IsVisible, InfoPanel.IsVisible, Strip.IsVisible, FoldersPanel.Width, InfoPanel.Width);
    private void SaveLayout()
    {
        if (restoringLayout || sessionSettings == null || mutations == null) return;
        var snapshot = LayoutPreferences(); mutations.RunIdle("layout", () => sessionSettings.RememberLayout(snapshot));
    }
    private Task FlushLayout()
    {
        var snapshot = LayoutPreferences();
        return sessionSettings == null ? Task.CompletedTask : Task.Run(() => sessionSettings.RememberLayout(snapshot));
    }
    private async Task RestoreLayout()
    {
        if (sessionSettings == null) return;
        var preferences = await Task.Run(sessionSettings.ReadPreferences);
        if (closing) return;
        restoringLayout = true;
        recentSources.AddRange(preferences.RecentSources); recentExports.AddRange(preferences.RecentExports);
        FoldersPanel.IsVisible = preferences.DirectoriesVisible; FoldersPanel.Width = preferences.DirectoryWidth;
        InfoPanel.IsVisible = preferences.InfoVisible; InfoPanel.Width = preferences.InfoWidth; Strip.IsVisible = preferences.StripVisible;
        CurrentPhotoBadge.IsVisible = !Strip.IsVisible && current != null;
        FoldersToggleButton.Content = FoldersPanel.IsVisible ? "‹" : "›"; InfoToggleButton.Content = InfoPanel.IsVisible ? "›" : "‹";
        StripToggleButton.Content = Strip.IsVisible ? "⌄" : "⌃";
        restoringLayout = false; UpdateDirectoryContext(true);
    }
}
