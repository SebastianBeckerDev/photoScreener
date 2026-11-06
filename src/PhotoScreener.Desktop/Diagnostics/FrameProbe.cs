using Avalonia.Controls;
using Avalonia.Media;

namespace PhotoScreener.Desktop.Diagnostics;
// Measures the actual window's drawing submission, not physical display latency.
public sealed class FrameProbe : Control
{
    public Action? Submitted { get; set; }
    public override void Render(DrawingContext context)
    {
        context.DrawRectangle(Brushes.Transparent, null, Bounds.WithX(0).WithY(0));
        Submitted?.Invoke(); base.Render(context);
    }
}
