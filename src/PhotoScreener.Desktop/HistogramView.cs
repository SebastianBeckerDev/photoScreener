using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace PhotoScreener.Desktop;

public sealed class HistogramView : Control
{
    public static readonly StyledProperty<IReadOnlyList<int>?> ValuesProperty = AvaloniaProperty.Register<HistogramView, IReadOnlyList<int>?>(nameof(Values));
    public IReadOnlyList<int>? Values { get => GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }
    static HistogramView() { AffectsRender<HistogramView>(ValuesProperty); }
    private static readonly IBrush Fill = new SolidColorBrush(Color.Parse("#8BBAD0"));
    public override void Render(DrawingContext context)
    {
        context.DrawRectangle(new SolidColorBrush(Color.Parse("#10131A")), null, new Rect(Bounds.Size));
        if (Values == null || Values.Count != 256 || Bounds.Width <= 0 || Bounds.Height <= 0) return;
        var max = Values.Max(); if (max <= 0) return;
        var geometry = new StreamGeometry();
        using (var figure = geometry.Open())
        {
            figure.BeginFigure(new Point(0, Bounds.Height), true);
            for (var i = 0; i < 256; i++)
            {
                var y = Bounds.Height * (1 - (double)Values[i] / max);
                figure.LineTo(new Point(i * Bounds.Width / 256, y));
                figure.LineTo(new Point((i + 1) * Bounds.Width / 256, y));
            }
            figure.LineTo(new Point(Bounds.Width, Bounds.Height)); figure.EndFigure(true);
        }
        context.DrawGeometry(Fill, null, geometry);
    }
}
