using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace PhotoScreener.Desktop;

public sealed class ZoomImage : Control
{
    public static readonly StyledProperty<IImage?> SourceProperty = AvaloniaProperty.Register<ZoomImage, IImage?>(nameof(Source));
    public IImage? Source { get => GetValue(SourceProperty); set => SetValue(SourceProperty, value); }
    static ZoomImage() { AffectsRender<ZoomImage>(SourceProperty); }
    private Vector offset;
    private Point? dragStart;
    private Vector dragOffset;
    public double Zoom { get; private set; } = 1;
    public event Action? ZoomChanged;
    public Rect ImageRectangle
    {
        get
        {
            if (Source == null || Source.Size.Width <= 0 || Source.Size.Height <= 0) return default;
            var fit = Math.Min(Bounds.Width / Source.Size.Width, Bounds.Height / Source.Size.Height);
            var width = Source.Size.Width * fit * Zoom; var height = Source.Size.Height * fit * Zoom;
            return new((Bounds.Width - width) / 2 + offset.X, (Bounds.Height - height) / 2 + offset.Y, width, height);
        }
    }
    public ZoomImage() { ClipToBounds = true; }
    public override void Render(DrawingContext context)
    {
        if (Source == null) return;
        using (context.PushClip(new Rect(Bounds.Size))) context.DrawImage(Source, new Rect(Source.Size), ImageRectangle);
    }
    public void ZoomAt(Point anchor, double factor)
    {
        if (Source == null || !double.IsFinite(factor) || factor <= 0) return;
        var next = Math.Clamp(Zoom * factor, 1, 16);
        if (next == Zoom) return;
        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var relative = anchor - center;
        offset = relative - (relative - offset) * (next / Zoom);
        Zoom = next; ClampOffset(); UpdateCursor(); InvalidateVisual(); ZoomChanged?.Invoke();
    }
    public void ResetView(bool notify = false)
    {
        var changed = Zoom != 1;
        Zoom = 1; offset = default; dragStart = null;
        UpdateCursor(); InvalidateVisual(); if (notify && changed) ZoomChanged?.Invoke();
    }
    private void ClampOffset()
    {
        if (Source == null) return;
        var rect = ImageRectangle;
        var x = Math.Max(0, (rect.Width - Bounds.Width) / 2);
        var y = Math.Max(0, (rect.Height - Bounds.Height) / 2);
        offset = new(Math.Clamp(offset.X, -x, x), Math.Clamp(offset.Y, -y, y));
    }
    private void UpdateCursor() => Cursor = new Cursor(Zoom > 1 ? StandardCursorType.Hand : StandardCursorType.Arrow);
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (Source == null || e.Delta.Y == 0) return;
        ZoomAt(e.GetPosition(this), Math.Pow(1.2, e.Delta.Y)); e.Handled = true;
    }
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (Source == null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (e.ClickCount == 2) { ResetView(true); e.Pointer.Capture(null); e.Handled = true; return; }
        if (Zoom <= 1) return;
        dragStart = e.GetPosition(this); dragOffset = offset; e.Pointer.Capture(this); e.Handled = true;
    }
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (dragStart is not { } start) return;
        offset = dragOffset + (e.GetPosition(this) - start); ClampOffset(); InvalidateVisual(); e.Handled = true;
    }
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e); dragStart = null; e.Pointer.Capture(null);
    }
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e) { base.OnPointerCaptureLost(e); dragStart = null; }
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BoundsProperty) { ClampOffset(); InvalidateVisual(); }
    }
}
