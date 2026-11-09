using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PhotoScreener.Core;
using PhotoScreener.Desktop.Imaging;
using PhotoScreener.Infrastructure;

namespace PhotoScreener.Desktop;
public partial class MainWindow : Window
{
    public PreviewScheduler Images { get; }
    public long ImageContext { get; private set; }
    private readonly ExportBindingResolver bindings = new();
    private readonly Dictionary<string, ObservableCollection<PhotoRow>> folders = new(PathGuard.Comparer);
    private readonly HashSet<string> completeFolders = new(PathGuard.Comparer);
    private readonly ObservableCollection<PhotoRow> rows = [];
    private readonly SourceCatalog catalog = new();
    private CancellationTokenSource loading = new();
    private ImageLease? previewLease;
    private Photo? displayedPhoto;
    private bool displayedFull;
    private PhotoRow? current;
    private string? activeFolder;
    private long focusGeneration, sequence, interactionGeneration;
    private PhotoRow? scrollRequest;
    private bool scrollPosted;
    private bool spaceDown, applyingSelection, closing, projectView, revealing;
    private ReviewFilter filter;
    private readonly string[] arguments;
    private readonly SessionSettingsStore? sessionSettings;
    private long folderOpenRequest;
    private Point? detailAnchor;
    private long targetApplyRequest;
    private long sourceSelectionRequest;
    private readonly SemaphoreSlim bindingChanges = new(1, 1);
    private int navigationDirection = 1;
    internal int ThumbnailOrder(PhotoRow row) => rows.IndexOf(row);
    private ListBox Strip => this.FindControl<ListBox>("Filmstrip")!;
    private Control Surface => this.FindControl<Control>("ReviewSurface")!;
    private void Text(string name, string value)
    {
        var control = this.FindControl<TextBlock>(name)!; control.Text = value;
        if (name is "FolderStatus" or "CurrentStatus") ToolTip.SetTip(control, value);
    }
    public MainWindow() : this([]) { }
    public MainWindow(string[] args, SessionSettingsStore? settings = null) : this(args, settings, null) { }
    internal MainWindow(string[] args, SessionSettingsStore? settings, PreviewScheduler? images)
    {
        arguments = args;
        sessionSettings = args.Contains("--benchmark") ? null : settings;
        var cacheOption = args.Contains("--benchmark") ? Array.IndexOf(args, "--cache-dir") : -1;
        Images = images ?? new(cacheOption >= 0 && cacheOption + 1 < args.Length ? args[cacheOption + 1] : null);
        InitializeComponent();
        InitializeInformation();
        comparer.ForegroundPending = () => Images.ForegroundPending; mutations = CreateMutations();
        InitializeReconciliation();
        InitializeNavigation();
        Strip.ItemsSource = rows;
        Preview.AddHandler(PointerPressedEvent, (_, _) => Surface.Focus(), handledEventsToo: true);
        Preview.ZoomChanged += () => { detailAnchor = null; UpdateZoomStatus(); RequestPreview(); };
        Preview.DetailRequested += anchor =>
        {
            if (current == null || displayedPhoto != current.Photo) return;
            if (detailAnchor.HasValue) { detailAnchor = null; Text("ZoomStatus", "Fit"); RequestPreview(); return; }
            detailAnchor = anchor; Text("ZoomStatus", "Loading 100% preview…"); RequestPreview();
        };
        ZoomFitButton.Click += (_, _) => { detailAnchor = null; Preview.ResetView(true); UpdateZoomStatus(); RequestPreview(); Surface.Focus(); };
        ZoomInButton.Click += (_, _) => { Preview.ZoomAt(new(Preview.Bounds.Width / 2, Preview.Bounds.Height / 2), 1.25); Surface.Focus(); };
        ZoomOutButton.Click += (_, _) => { Preview.ZoomAt(new(Preview.Bounds.Width / 2, Preview.Bounds.Height / 2), .8); Surface.Focus(); };
        StripToggleButton.Click += (_, _) => { Strip.IsVisible = !Strip.IsVisible; CurrentPhotoBadge.IsVisible = !Strip.IsVisible && current != null; StripToggleButton.Content = Strip.IsVisible ? "⌄" : "⌃"; if (Strip.IsVisible && current != null) QueueStripScroll(current); SaveLayout(); Surface.Focus(); };
        ActivityButton.Click += (_, _) => { WorkPanel.IsVisible = !WorkPanel.IsVisible; Surface.Focus(); };
        WorkList.ItemsSource = operationRows;
        WorkList.SelectionChanged += (_, _) => { if (WorkList.SelectedItem is PhotoRow requested) { var row = registered.GetValueOrDefault(requested.Photo.Path) ?? requested; interactionGeneration++; filter = ReviewFilter.All; revealing = true; FilterBox.SelectedIndex = 0; revealing = false; ShowFolderRows(row.Photo.Folder, row); FocusPhoto(row); Surface.Focus(); } };
        RetryButton.Click += (_, _) => { foreach (var row in operationRows.Where(r => r.HasError).ToArray()) RequestMutation(row); Surface.Focus(); };
        this.FindControl<Button>("CancelButton")!.Click += (_, _) => CancelPending();
        this.FindControl<ComboBox>("FilterBox")!.SelectionChanged += (_, _) =>
        {
            filter = (ReviewFilter)this.FindControl<ComboBox>("FilterBox")!.SelectedIndex;
            if (!revealing)
            {
                interactionGeneration++;
                if (projectView && filter != ReviewFilter.Picked && current != null)
                {
                    var retain = current.IsExportOnly ? folders.GetValueOrDefault(current.Photo.Folder)?.FirstOrDefault() : current;
                    if (retain != null) ShowFolderRows(retain.Photo.Folder, retain);
                    else { projectView = false; pickedContext.Cancel(); projectGeneration++; rows.Clear(); ClearFocus(); }
                }
                ApplyFilter();
            }
            Surface.Focus();
        };
        Strip.SelectionChanged += (_, _) => { if (!applyingSelection && Strip.SelectedItem is PhotoRow row) { interactionGeneration++; FocusPhoto(row); Surface.Focus(); } };
        AddHandler(KeyDownEvent, HandleKeyDown, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, (_, e) => { if (e.Key == Key.Space) spaceDown = false; }, RoutingStrategies.Tunnel);
        Deactivated += (_, _) => spaceDown = false;
        Surface.SizeChanged += (_, _) => { if (current != null) { RequestPreview(); QueueStripScroll(current); } };
        Closing += async (_, e) =>
        {
            if (closing) return;
            e.Cancel = true; closing = true;
            await DrainOperations();
            await FlushLastFolder();
            await FlushLayout();
            if (operationRows.Any(r => r.HasError))
            {
                var decision = await ChooseFailedChanges("Some export changes failed. Completed files are safe; choose how to handle the failed requests.");
                if (decision != "Cancel and close")
                {
                    closing = false;
                    if (decision == "Retry") foreach (var row in operationRows.ToArray()) RequestMutation(row);
                    RequestPreview(); return;
                }
                CancelPending();
            }
            loading.Cancel(); focusGeneration++; ImageContext++; Images.SetContext(ImageContext, []); reconcileTimer?.Stop();
            if (comparisons != null) await comparisons.DisposeAsync();
            if (mutations != null) await mutations.DisposeAsync();
            pickedContext.Cancel();
            navigationContext.Cancel();
            if (projectPicks != null) await projectPicks.DisposeAsync();
            watchers.Dispose();
            Preview.Source = null; previewLease?.Dispose(); previewLease = null;
            await Images.DisposeAsync(); await information.DisposeAsync(); Close();
        };
        Opened += async (_, _) =>
        {
            Surface.Focus();
            // Window initialization can assign the first toolbar control after Opened.
            // Restore review focus once that initial layout/focus work has finished.
            Dispatcher.UIThread.Post(() => { if (!closing) Surface.Focus(); }, DispatcherPriority.Loaded);
            await RestoreLayout();
            if (args.Contains("--benchmark"))
            {
                await ConfigureBenchmarkViewport();
                StartBenchmark();
            }
            if (args.FirstOrDefault(a => !a.StartsWith("--")) is { } source)
            {
                var exportOption = Array.IndexOf(args, "--export");
                if (exportOption >= 0 && exportOption + 1 < args.Length)
                    await Task.Run(() => bindings.Bind(source, args[exportOption + 1], [source]));
                await OpenFolder(source);
            }
            else await RestoreLastFolder();
        };
    }
    private async Task ChooseSource()
    {
        var selected = await StorageProvider.OpenFolderPickerAsync(new() { Title = "Choose photo folder", AllowMultiple = false });
        if (selected.FirstOrDefault()?.TryGetLocalPath() is { } path)
            await OpenSourceSelection(path);
        Surface.Focus();
    }
    private async Task OpenSourceSelection(string path)
    {
        var request = ++sourceSelectionRequest;
        var priorOpen = folderOpenRequest;
        if (projectRoot != null && !PathGuard.Within(path, projectRoot))
        {
            await DrainOperations();
            if (closing || request != sourceSelectionRequest || priorOpen != folderOpenRequest) return;
            if (operationRows.Any(r => r.HasError))
            {
                if (await ChooseFailedChanges("Finish or cancel failed changes before opening a different project.") != "Cancel and close") return;
                CancelPending();
            }
        }
        if (closing || request != sourceSelectionRequest || priorOpen != folderOpenRequest) return;
        await OpenFolder(path);
        Surface.Focus();
    }
    internal async Task ApplyExportTarget(string target)
    {
        if (activeFolder == null) return;
        var capturedFolder = activeFolder;
        var request = ++targetApplyRequest;
        await bindingChanges.WaitAsync();
        try
        {
            if (closing || request != targetApplyRequest || activeFolder != capturedFolder) return;
            await DrainOperations();
            if (closing || request != targetApplyRequest || activeFolder != capturedFolder) return;
            if (operationRows.Any(r => r.Photo.Folder == capturedFolder && r.HasError))
            {
                if (await ChooseFailedChanges("Finish or cancel this folder's failed changes before changing its export target.") != "Cancel and close") return;
                CancelPending();
            }
            var protectedFolders = folders.Keys.ToArray();
            await Task.Run(() => bindings.Bind(capturedFolder, target, protectedFolders));
            if (closing || request != targetApplyRequest || activeFolder != capturedFolder) return;
            candidateExport = target; RememberExportDirectory(target); await OpenFolder(capturedFolder, true);
        }
        catch (Exception e) { if (!closing) Text("FolderStatus", e.Message); }
        finally { bindingChanges.Release(); }
        Surface.Focus();
    }
    public async Task OpenFolder(string folder, bool refresh = false)
    {
        var request = ++folderOpenRequest;
        var canonical = PathGuard.Canonical(folder);
        var knownBindings = folders.Keys.Select(bindings.Resolve).ToArray();
        bool excluded;
        try
        {
            excluded = await Task.Run(() =>
            {
                BindingIdentityGuard.Validate(bindings.Resolve(canonical), knownBindings);
                return bindings.Excluded(canonical);
            });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        { if (request == folderOpenRequest && !closing) Text("FolderStatus", "Unavailable · " + e.Message); return; }
        if (request != folderOpenRequest || closing) return;
        if (excluded) { Text("FolderStatus", "Export folders are excluded from source review. Choose the original source folder."); return; }
        loading.Cancel(); loading.Dispose(); loading = new();
        var token = loading.Token; var context = ++ImageContext;
        pendingRestore = null; comparisonCompletedContext = -1;
        activeFolder = canonical; projectView = false;
        UpdateDirectoryContext();
        mutations?.RegisterBinding(bindings.Resolve(canonical));
        pickedContext.Cancel(); projectGeneration++;
        DiscoverProject(activeFolder);
        rows.Clear(); ClearFocus(retainPreview: true); filter = ReviewFilter.All;
        // A programmatic reset must not repopulate the old cached rows while the
        // new folder collection is being established below.
        revealing = true; FilterBox.SelectedIndex = 0; revealing = false;
        // SelectionChanged does not run when All was already selected. Folder loading
        // must establish keyboard focus independently, before the first photo arrives.
        Surface.Focus();
        var folderInteraction = interactionGeneration;
        Images.SetContext(context, []);
        Text("FolderStatus", activeFolder + " · discovering…");
        if (!refresh && completeFolders.Contains(activeFolder) && folders.TryGetValue(activeFolder, out var warm))
        {
            foreach (var row in warm) { RegisterRow(row); rows.Add(row); }
            if (rows.Count > 0) FocusPhoto(rows.FirstOrDefault(r => cursors.GetValueOrDefault(activeFolder) == r.Photo.Path) ?? rows[0]);
            else ClearFocus();
            Images.SetWarmPhotos(rows.Select(r => r.Photo).ToArray());
            RememberLastFolder(activeFolder);
            BeginComparison(warm, context); ReadMetadataAndWatch(bindings.Resolve(activeFolder), context, folderInteraction); return;
        }
        var captured = activeFolder;
        completeFolders.Remove(captured);
        var preferredCursor = cursors.GetValueOrDefault(captured);
        foreach (var previous in registered.Values.Where(r => PathGuard.Comparer.Equals(r.Photo.Folder, captured)).ToArray())
        { UnregisterRow(previous); }
        counts[captured] = (0, 0, 0);
        var collection = new ObservableCollection<PhotoRow>(); folders[captured] = collection;
        var remembered = false;
        try
        {
            var binding = bindings.Resolve(captured); var protectedFolders = folders.Keys.ToArray();
            await Task.Run(async () =>
            {
                PathGuard.ValidateBinding(binding, protectedFolders);
                var batch = new List<PhotoRow>(32);
                foreach (var photo in catalog.Enumerate(captured, token))
                {
                    batch.Add(new(photo, binding));
                    if (batch.Count == 32 || collection.Count == 0) { await Publish(batch.ToArray()); batch.Clear(); }
                }
                if (batch.Count > 0) await Publish(batch.ToArray());
                async Task Publish(PhotoRow[] discovered)
                {
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        if (token.IsCancellationRequested || context != ImageContext) return;
                        foreach (var row in discovered) { collection.Add(row); RegisterRow(row); rows.Add(row); }
                        if (!remembered) { remembered = true; RememberLastFolder(captured); }
                        if (current == null && rows.Count > 0) FocusPhoto(rows[0]);
                        if (preferredCursor != null && discovered.FirstOrDefault(r => PathGuard.Comparer.Equals(r.Photo.Path, preferredCursor)) is { } preferred) FocusPhoto(preferred);
                        UpdateCounts();
                    }, DispatcherPriority.Background);
                }
            }, token);
            if (context != ImageContext) return;
            completeFolders.Add(captured);
            if (!remembered) RememberLastFolder(captured);
            if (collection.Count == 0) ClearFocus();
            Images.SetWarmPhotos(collection.Select(r => r.Photo).ToArray());
            Text("FolderStatus", captured + " → " + binding.ExportRoot + " · review history Unknown");
            BeginComparison(collection, context);
            ReadMetadataAndWatch(binding, context, folderInteraction);
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            if (context != ImageContext) return;
            Text("FolderStatus", "Unavailable · " + e.Message + " · Refresh or choose source");
            if (current == null && PreviewPending.IsVisible)
            {
                Text("PendingPreviewTarget", "Directory unavailable · " + captured);
                Text("PendingPreviewError", e.Message); PendingPreviewError.IsVisible = true; PendingPreviewProgress.IsVisible = false;
            }
        }
    }
    private void HandleKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Source is TextBox || !Surface.IsFocused && !Strip.IsKeyboardFocusWithin) return;
        if (e.Key is Key.Left or Key.Right) { Navigate(e.Key == Key.Right ? 1 : -1); e.Handled = true; }
        else if (e.Key == Key.Space)
        {
            e.Handled = true; if (spaceDown) return; spaceDown = true;
            if (current == null) return;
            if (current.IsExportOnly) { Text("CurrentStatus", "Unmatched export preserved · Open its original source to review"); return; }
            interactionGeneration++;
            current.Toggle(++sequence); Text("CurrentStatus", current.Status);
            if (projectView || filter != ReviewFilter.All)
            {
                filter = ReviewFilter.All;
                revealing = true; this.FindControl<ComboBox>("FilterBox")!.SelectedIndex = 0; revealing = false;
                ShowFolderRows(current.Photo.Folder, current);
            }
            Timed("mutation-request", () => RequestMutation(current)); Timed("review-metadata-request", () => RecordReview(current)); Timed("counts", UpdateCounts);
        }
    }
    private void Navigate(int direction)
    {
        if (current == null) return;
        interactionGeneration++;
        var index = rows.IndexOf(current) + direction;
        while (index >= 0 && index < rows.Count)
        {
            if (ReviewRules.Matches(filter, rows[index].Picked)) { RecordReview(current, rows[index]); FocusPhoto(rows[index]); return; }
            index += direction;
        }
    }
    private void FocusPhoto(PhotoRow row)
    {
        var index = rows.IndexOf(row);
        var previousIndex = current == null ? -1 : rows.IndexOf(current);
        if (previousIndex >= 0 && index >= 0 && index != previousIndex) navigationDirection = index > previousIndex ? 1 : -1;
        Images.SetNavigationPriority(row.Photo, index, navigationDirection);
        WelcomeBrand.IsVisible = false;
        if (current?.Photo != row.Photo) { detailAnchor = null; Preview.ResetView(); Text("ZoomStatus", "Fit"); LoadInformation(row); }
        current = row; focusGeneration++;
        CurrentPhotoBadge.IsVisible = !Strip.IsVisible;
        Text("CurrentName", row.Name); Text("CurrentStatus", row.Status);
        applyingSelection = true; Timed("selection", () => Strip.SelectedItem = row); QueueStripScroll(row); applyingSelection = false;
        Timed("preview-request", RequestPreview);
        Timed("prefetch-request", () => Images.Prefetch(Enumerable.Range(1, 4).Select(step => index + step * navigationDirection).Where(i => i >= 0 && i < rows.Count).Select(i => rows[i].Photo)));
    }
    private void QueueStripScroll(PhotoRow row)
    {
        scrollRequest = row;
        if (scrollPosted) return;
        scrollPosted = true;
        Dispatcher.UIThread.Post(() =>
        {
            scrollPosted = false;
            var requested = scrollRequest; scrollRequest = null;
            if (closing || requested == null || current != requested || !rows.Contains(requested)) return;
            Timed("deferred-strip-scroll", () => Strip.ScrollIntoView(requested));
            Dispatcher.UIThread.Post(() =>
            {
                if (closing || current != requested || !Strip.IsVisible || !rows.Contains(requested)) return;
                var viewer = Strip.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
                var container = Strip.ContainerFromItem(requested);
                if (viewer == null || container == null || viewer.Viewport.Width <= 0) return;
                var center = container.TranslatePoint(new Avalonia.Point(container.Bounds.Width / 2, 0), viewer);
                if (center == null) return;
                var offset = viewer.Offset.X + center.Value.X - viewer.Viewport.Width / 2;
                viewer.Offset = new(Math.Clamp(offset, 0, Math.Max(0, viewer.Extent.Width - viewer.Viewport.Width)), viewer.Offset.Y);
            }, DispatcherPriority.Loaded);
        }, DispatcherPriority.Background);
    }
    private void RequestPreview()
    {
        if (current == null) return;
        var row = current; var focus = ++focusGeneration; var context = ImageContext;
        var retainingPrevious = previewLease != null && displayedPhoto != row.Photo;
        PreviewPending.IsVisible = retainingPrevious;
        Preview.Opacity = retainingPrevious ? .72 : 1;
        Preview.IsHitTestVisible = !retainingPrevious;
        PendingPreviewProgress.IsVisible = retainingPrevious;
        PendingPreviewError.IsVisible = false;
        if (retainingPrevious)
        {
            Text("PendingPreviewTarget", $"Loading {row.Name} · {row.Photo.Folder}…");
            Text("PendingPreviewShown", $"Still showing {displayedPhoto!.Name} · {displayedPhoto.Folder}");
        }
        Text("PreviewMessage", previewLease == null ? "Loading…" : "");
        // A retained full-quality image belongs to the old photo. It must never
        // suppress the new photo's quick preview, even during resize/zoom requests.
        var fullPublished = previewLease != null && displayedPhoto == row.Photo && displayedFull;
        void Publish(ImageLease? lease, string? error, bool full) => Dispatcher.UIThread.Post(() =>
        {
            if (closing || focus != focusGeneration || context != ImageContext || current != row || !full && fullPublished)
            { lease?.Dispose(); return; }
            if (lease == null)
            {
                if (full && detailAnchor.HasValue)
                {
                    detailAnchor = null; Text("ZoomStatus", "Detail unavailable");
                    PreviewPending.IsVisible = true; Text("PendingPreviewTarget", "Detail preview unavailable · " + row.Name);
                    Text("PendingPreviewShown", "The available preview remains visible.");
                    Text("PendingPreviewError", error ?? "Could not load a detailed preview.");
                    PendingPreviewError.IsVisible = true; PendingPreviewProgress.IsVisible = false;
                }
                if (previewLease == null) Text("PreviewMessage", error ?? "Preview unavailable");
                else if (displayedPhoto != row.Photo)
                {
                    Text("PendingPreviewTarget", $"Preview unavailable: {row.Name}");
                    Text("PendingPreviewError", error ?? "Could not load this photo.");
                    PendingPreviewError.IsVisible = true; PendingPreviewProgress.IsVisible = false;
                }
                Images.ViewportReady(); return;
            }
            if (full) fullPublished = true;
            var previous = previewLease;
            Preview.Source = lease.Bitmap; previewLease = lease; displayedPhoto = row.Photo; displayedFull = full;
            if (full && detailAnchor is { } anchor)
            {
                detailAnchor = null; Preview.ShowDetailAt(anchor, RenderScaling); UpdateZoomStatus();
            }
            if (!detailAnchor.HasValue) UpdateZoomStatus();
            Preview.Opacity = 1; Preview.IsHitTestVisible = true; PreviewPending.IsVisible = false;
            previous?.Dispose();
            Text("PreviewMessage", ""); Images.ViewportReady();
            PreviewPublished?.Invoke(row, full);
        }, DispatcherPriority.Render);
        var side = PreviewSide();
        FocusRequested?.Invoke(row, focus);
        Timed("preview-queue", () =>
        {
            if (previewLease != null && displayedPhoto == row.Photo)
                Images.Refine(new(row.Photo, side, context, true, (lease, error) => Publish(lease, error, true), IncludeHistogram: InfoPanel.IsVisible && Histogram.Values == null));
            else Images.Focus(new(row.Photo, 800, context, false, (lease, error) => Publish(lease, error, false), IncludeHistogram: InfoPanel.IsVisible),
                new(row.Photo, side, context, true, (lease, error) => Publish(lease, error, true)));
        });
    }
    private void UpdateZoomStatus() => Text("ZoomStatus", Preview.Zoom == 1 ? "Fit" : Preview.Source is Avalonia.Media.Imaging.Bitmap bitmap
        ? $"{Preview.ImageRectangle.Width * RenderScaling / bitmap.PixelSize.Width * 100:0}% preview" : $"{Preview.Zoom * 100:0}% of fit");
    private int PreviewSide() => detailAnchor.HasValue ? 3840 : Math.Clamp((int)(Math.Max(Surface.Bounds.Width, Surface.Bounds.Height) * RenderScaling * Preview.Zoom), 1024, 3840);
    private void ClearFocus(bool retainPreview = false)
    {
        Images.ClearFocus();
        navigationDirection = 1;
        retainPreview &= previewLease != null;
        WelcomeBrand.IsVisible = !retainPreview;
        ClearInformation();
        detailAnchor = null; Preview.ResetView(); Text("ZoomStatus", "Fit");
        current = null; focusGeneration++;
        CurrentPhotoBadge.IsVisible = false;
        if (!retainPreview) { Preview.Source = null; previewLease?.Dispose(); previewLease = null; displayedPhoto = null; displayedFull = false; }
        Preview.Opacity = retainPreview ? .72 : 1; Preview.IsHitTestVisible = !retainPreview; PreviewPending.IsVisible = retainPreview;
        if (retainPreview)
        {
            Text("PendingPreviewTarget", "Opening " + activeFolder + "…");
            Text("PendingPreviewShown", $"Still showing {displayedPhoto!.Name} · {displayedPhoto.Folder}");
            PendingPreviewProgress.IsVisible = true; PendingPreviewError.IsVisible = false;
        }
        Text("CurrentName", ""); Text("CurrentStatus", ""); Text("PreviewMessage", retainPreview ? "" : "No photos in this view");
    }
    private void ApplyFilter()
    {
        var source = !projectView && activeFolder != null && folders.TryGetValue(activeFolder, out var folder) ? folder.ToArray() : rows.ToArray();
        var oldIndex = current == null ? 0 : Array.IndexOf(source, current);
        var chosen = ReviewRules.ChooseAfterFilter(oldIndex, source.Length, i => ReviewRules.Matches(filter, source[i].Picked));
        applyingSelection = true; rows.Clear(); foreach (var row in source.Where(r => ReviewRules.Matches(filter, r.Picked))) rows.Add(row); applyingSelection = false;
        if (chosen < 0) ClearFocus(); else FocusPhoto(source[chosen]);
        UpdateCounts();
    }
    private void ShowFolderRows(string folder, PhotoRow retain)
    {
        var folderChanged = !PathGuard.Comparer.Equals(activeFolder, folder);
        if (projectView) { pickedContext.Cancel(); projectGeneration++; }
        projectView = false; activeFolder = folder;
        UpdateDirectoryContext();
        applyingSelection = true; rows.Clear(); foreach (var row in folders[folder]) rows.Add(row);
        current = retain; Strip.SelectedItem = retain; applyingSelection = false; QueueStripScroll(retain);
        Images.SetWarmPhotos(folders[folder].Select(r => r.Photo).ToArray());
        if (folderChanged) ReadMetadataAndWatch(retain.Binding, ImageContext, interactionGeneration);
    }
    internal void ShowProjectPicks()
    {
        projectView = true; filter = ReviewFilter.Picked; rows.Clear();
        revealing = true; FilterBox.SelectedIndex = 1; revealing = false;
        foreach (var folder in folders.Where(f => projectRoot == null || PathGuard.Within(f.Key, projectRoot)).Select(f => f.Value)) foreach (var row in folder.Where(r => r.Picked == true)) rows.Add(row);
        if (rows.Count > 0) FocusPhoto(rows[0]); else ClearFocus();
        Text("FolderStatus", "Project picks · discovered folders only · total discovery Unknown"); Surface.Focus(); UpdateCounts();
        ScanProjectPicks();
    }
    private void UpdateCounts()
    {
        var projectCounts = projectView ? counts.Where(c => projectRoot == null || PathGuard.Within(c.Key, projectRoot)).Select(c => c.Value).ToArray() : [];
        var count = projectView ? (projectCounts.Sum(c => c.Total), projectCounts.Sum(c => c.Picked), projectCounts.Sum(c => c.Checking)) : activeFolder != null ? counts.GetValueOrDefault(activeFolder) : (0, 0, 0);
        Text("Counts", $"{count.Item2} picked / {count.Item1}{(projectView && !projectComplete ? "+" : "")} · {count.Item3} checking · {mutations?.PendingCount ?? 0} pending" + (projectView && unmatchedProjectRows.Count > 0 ? $" · {unmatchedProjectRows.Count} unmatched exports" : ""));
        var errors = operationRows.Count(r => r.HasError);
        ActivityButton.Content = errors > 0 ? $"{errors} failed · {mutations?.PendingCount ?? 0} pending" : mutations?.PendingCount > 0 ? $"{mutations.PendingCount} pending" : "No pending changes";
        RetryButton.IsEnabled = errors > 0; CancelButton.IsEnabled = operationRows.Count > 0;
    }
    // Extended by the comparison/mutation and performance slices.
    private partial void BeginComparison(ObservableCollection<PhotoRow> collection, long context);
    private partial void RequestMutation(PhotoRow row);
    private partial void CancelPending();
    private partial Task DrainOperations();
    private partial void StartBenchmark();
    public event Action<PhotoRow, bool>? PreviewPublished;
    public event Action<PhotoRow, long>? FocusRequested;
    public event Action<string, double>? ActionSection;
    private void Timed(string section, Action action)
    {
        if (ActionSection == null) { action(); return; }
        var start = System.Diagnostics.Stopwatch.GetTimestamp(); action();
        ActionSection.Invoke(section, System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds);
    }
}
