using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using PhotoScreener.Core;
using PhotoScreener.Desktop.Imaging;
using PhotoScreener.Infrastructure;

namespace PhotoScreener.Desktop;
public partial class MainWindow : Window
{
    public PreviewScheduler Images { get; } = new();
    public long ImageContext { get; private set; }
    private readonly ExportBindingResolver bindings = new();
    private readonly Dictionary<string, ObservableCollection<PhotoRow>> folders = new(PathGuard.Comparer);
    private readonly ObservableCollection<PhotoRow> rows = [];
    private readonly SourceCatalog catalog = new();
    private CancellationTokenSource loading = new();
    private ImageLease? previewLease;
    private PhotoRow? current;
    private string? activeFolder;
    private long focusGeneration, sequence;
    private bool spaceDown, applyingSelection, closing, projectView;
    private ReviewFilter filter;
    private readonly string[] arguments;
    private ListBox Strip => this.FindControl<ListBox>("Filmstrip")!;
    private Control Surface => this.FindControl<Control>("ReviewSurface")!;
    private void Text(string name, string value) => this.FindControl<TextBlock>(name)!.Text = value;
    public MainWindow() : this([]) { }
    public MainWindow(string[] args)
    {
        arguments = args;
        InitializeComponent();
        Strip.ItemsSource = rows;
        this.FindControl<Button>("OpenButton")!.Click += async (_, _) => await ChooseSource();
        this.FindControl<Button>("TargetButton")!.Click += async (_, _) => await ChooseTarget();
        this.FindControl<Button>("RefreshButton")!.Click += async (_, _) => { if (activeFolder != null) await OpenFolder(activeFolder, true); Surface.Focus(); };
        this.FindControl<Button>("StripButton")!.Click += (_, _) => { Strip.IsVisible = !Strip.IsVisible; Surface.Focus(); };
        this.FindControl<Button>("ProjectPickedButton")!.Click += (_, _) => ShowProjectPicks();
        this.FindControl<Button>("RetryButton")!.Click += (_, _) => { if (current != null) RequestMutation(current); Surface.Focus(); };
        this.FindControl<Button>("CancelButton")!.Click += (_, _) => CancelPending();
        this.FindControl<ComboBox>("FilterBox")!.SelectionChanged += (_, _) =>
        {
            filter = (ReviewFilter)this.FindControl<ComboBox>("FilterBox")!.SelectedIndex;
            ApplyFilter(); Surface.Focus();
        };
        Strip.SelectionChanged += (_, _) => { if (!applyingSelection && Strip.SelectedItem is PhotoRow row) { FocusPhoto(row); Surface.Focus(); } };
        AddHandler(KeyDownEvent, HandleKeyDown, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, (_, e) => { if (e.Key == Key.Space) spaceDown = false; }, RoutingStrategies.Tunnel);
        Deactivated += (_, _) => spaceDown = false;
        SizeChanged += (_, _) => { if (current != null) RequestPreview(); };
        Closing += async (_, e) =>
        {
            if (closing) return;
            e.Cancel = true; closing = true;
            loading.Cancel(); focusGeneration++; ImageContext++; Images.SetContext(ImageContext, []);
            await DrainOperations();
            Preview.Source = null; previewLease?.Dispose(); previewLease = null;
            await Images.DisposeAsync(); Close();
        };
        Opened += async (_, _) =>
        {
            Surface.Focus();
            if (args.FirstOrDefault(a => !a.StartsWith("--")) is { } source) await OpenFolder(source);
            if (args.Contains("--benchmark")) StartBenchmark();
        };
    }
    private async Task ChooseSource()
    {
        var selected = await StorageProvider.OpenFolderPickerAsync(new() { Title = "Choose photo folder", AllowMultiple = false });
        if (selected.FirstOrDefault()?.TryGetLocalPath() is { } path) await OpenFolder(path);
        Surface.Focus();
    }
    private async Task ChooseTarget()
    {
        if (activeFolder == null) return;
        var selected = await StorageProvider.OpenFolderPickerAsync(new() { Title = "Choose this folder's export target", AllowMultiple = false });
        if (selected.FirstOrDefault()?.TryGetLocalPath() is { } target)
        {
            try { await DrainOperations(); bindings.Bind(activeFolder, target, folders.Keys); await OpenFolder(activeFolder, true); }
            catch (Exception e) { Text("FolderStatus", e.Message); }
        }
        Surface.Focus();
    }
    public async Task OpenFolder(string folder, bool refresh = false)
    {
        loading.Cancel(); loading.Dispose(); loading = new();
        var token = loading.Token; var context = ++ImageContext;
        activeFolder = PathGuard.Canonical(folder); projectView = false;
        rows.Clear(); ClearFocus(); filter = ReviewFilter.All;
        this.FindControl<ComboBox>("FilterBox")!.SelectedIndex = 0;
        Images.SetContext(context, []);
        Text("FolderStatus", activeFolder + " · discovering…");
        if (!refresh && folders.TryGetValue(activeFolder, out var warm))
        {
            foreach (var row in warm) rows.Add(row);
            if (rows.Count > 0) FocusPhoto(rows[0]);
            Images.SetWarmPhotos(rows.Select(r => r.Photo).ToArray());
            BeginComparison(warm, context); return;
        }
        var captured = activeFolder;
        var collection = new ObservableCollection<PhotoRow>(); folders[captured] = collection;
        try
        {
            var binding = bindings.Resolve(captured);
            await Task.Run(async () =>
            {
                PathGuard.ValidateBinding(binding, folders.Keys.ToArray());
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
                        foreach (var row in discovered) { collection.Add(row); rows.Add(row); }
                        if (current == null && rows.Count > 0) FocusPhoto(rows[0]);
                        UpdateCounts();
                    }, DispatcherPriority.Background);
                }
            }, token);
            if (context != ImageContext) return;
            Images.SetWarmPhotos(collection.Select(r => r.Photo).ToArray());
            Text("FolderStatus", captured + " → " + binding.ExportRoot + " · review history Unknown");
            BeginComparison(collection, context);
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { if (context == ImageContext) Text("FolderStatus", "Unavailable · " + e.Message + " · Retry or choose source"); }
    }
    private void HandleKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Source is TextBox || !Surface.IsFocused && !Strip.IsKeyboardFocusWithin) return;
        if (e.Key is Key.Left or Key.Right) { Navigate(e.Key == Key.Right ? 1 : -1); e.Handled = true; }
        else if (e.Key == Key.Space)
        {
            e.Handled = true; if (spaceDown) return; spaceDown = true;
            if (current == null) return;
            current.Toggle(++sequence); Text("CurrentStatus", current.Status);
            if (projectView || filter != ReviewFilter.All)
            {
                projectView = false; filter = ReviewFilter.All;
                this.FindControl<ComboBox>("FilterBox")!.SelectedIndex = 0;
                ShowFolderRows(current.Photo.Folder, current);
            }
            RequestMutation(current); UpdateCounts();
        }
    }
    private void Navigate(int direction)
    {
        if (current == null) return;
        var index = rows.IndexOf(current) + direction;
        while (index >= 0 && index < rows.Count)
        {
            if (ReviewRules.Matches(filter, rows[index].Picked)) { FocusPhoto(rows[index]); return; }
            index += direction;
        }
    }
    private void FocusPhoto(PhotoRow row)
    {
        current = row; focusGeneration++;
        Text("CurrentName", row.Name); Text("CurrentStatus", row.Status);
        applyingSelection = true; Strip.SelectedItem = row; Strip.ScrollIntoView(row); applyingSelection = false;
        RequestPreview();
    }
    private void RequestPreview()
    {
        if (current == null) return;
        var row = current; var focus = ++focusGeneration; var context = ImageContext;
        Preview.Source = null; previewLease?.Dispose(); previewLease = null;
        Text("PreviewMessage", "Loading…");
        var fullPublished = false;
        void Publish(ImageLease? lease, string? error, bool full) => Dispatcher.UIThread.Post(() =>
        {
            if (closing || focus != focusGeneration || context != ImageContext || current != row || !full && fullPublished)
            { lease?.Dispose(); return; }
            if (lease == null) { if (previewLease == null) Text("PreviewMessage", error ?? "Preview unavailable"); return; }
            if (full) fullPublished = true;
            Preview.Source = null; previewLease?.Dispose(); previewLease = lease; Preview.Source = lease.Bitmap;
            Text("PreviewMessage", ""); Images.ViewportReady();
            PreviewPublished?.Invoke(row, full);
        }, DispatcherPriority.Render);
        var scaling = RenderScaling;
        var side = Math.Clamp((int)(Math.Max(Preview.Bounds.Width, Preview.Bounds.Height) * scaling), 1024, 3840);
        Images.Focus(new(row.Photo, 800, context, false, (lease, error) => Publish(lease, error, false)),
            new(row.Photo, side, context, true, (lease, error) => Publish(lease, error, true)));
    }
    private void ClearFocus()
    {
        current = null; focusGeneration++; Preview.Source = null; previewLease?.Dispose(); previewLease = null;
        Text("CurrentName", ""); Text("CurrentStatus", ""); Text("PreviewMessage", "No photos in this view");
    }
    private void ApplyFilter()
    {
        var oldIndex = current == null ? 0 : rows.IndexOf(current);
        var chosen = ReviewRules.ChooseAfterFilter(oldIndex, rows.Count, i => ReviewRules.Matches(filter, rows[i].Picked));
        if (chosen < 0) ClearFocus(); else FocusPhoto(rows[chosen]);
        // Hide rows via a new lightweight filtered view, retaining identity in the folder collection.
        if (!projectView && activeFolder != null && folders.TryGetValue(activeFolder, out var folder))
        {
            var retain = current; rows.Clear(); foreach (var row in folder.Where(r => ReviewRules.Matches(filter, r.Picked))) rows.Add(row);
            if (retain != null && rows.Contains(retain)) { applyingSelection = true; Strip.SelectedItem = retain; applyingSelection = false; }
        }
        UpdateCounts();
    }
    private void ShowFolderRows(string folder, PhotoRow retain)
    {
        activeFolder = folder; rows.Clear(); foreach (var row in folders[folder]) rows.Add(row);
        current = retain; applyingSelection = true; Strip.SelectedItem = retain; Strip.ScrollIntoView(retain); applyingSelection = false;
    }
    private void ShowProjectPicks()
    {
        projectView = true; filter = ReviewFilter.Picked; rows.Clear();
        foreach (var folder in folders.Values) foreach (var row in folder.Where(r => r.Picked == true)) rows.Add(row);
        if (rows.Count > 0) FocusPhoto(rows[0]); else ClearFocus();
        Text("FolderStatus", "Project picks · discovered folders only · total discovery Unknown"); Surface.Focus(); UpdateCounts();
    }
    private void UpdateCounts() => Text("Counts", $"{rows.Count(r => r.Picked == true)} picked / {rows.Count} · {rows.Count(r => !r.Picked.HasValue)} checking");
    // Extended by the comparison/mutation and performance slices.
    private partial void BeginComparison(ObservableCollection<PhotoRow> collection, long context);
    private partial void RequestMutation(PhotoRow row);
    private partial void CancelPending();
    private partial Task DrainOperations();
    private partial void StartBenchmark();
    public event Action<PhotoRow, bool>? PreviewPublished;
}
