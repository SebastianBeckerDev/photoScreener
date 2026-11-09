using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PhotoScreener.Desktop.Imaging;

namespace PhotoScreener.Desktop;
public sealed class ThumbnailView : Image
{
    private ImageLease? lease;
    private long request;
    public ThumbnailView()
    {
        Stretch = Avalonia.Media.Stretch.Uniform;
        AttachedToVisualTree += (_, _) => Load();
        DetachedFromVisualTree += (_, _) => Clear();
        DataContextChanged += (_, _) => { Clear(); if (this.GetVisualRoot() != null) Load(); };
    }
    private void Clear() { request++; this.FindAncestorOfType<MainWindow>()?.Images.CancelThumbnail(this); Source = null; lease?.Dispose(); lease = null; }
    private void Load()
    {
        if (DataContext is not PhotoRow row || this.FindAncestorOfType<MainWindow>() is not { } window) return;
        var version = ++request;
        window.Images.Thumbnail(new(row.Photo, 160, window.ImageContext, false, (result, _) => Dispatcher.UIThread.Post(() =>
        {
            if (version != request || DataContext != row || this.GetVisualRoot() == null) { result?.Dispose(); return; }
            Source = null; lease?.Dispose(); lease = result; Source = result?.Bitmap;
        }, DispatcherPriority.Background), this, Order: window.ThumbnailOrder(row)));
    }
}
