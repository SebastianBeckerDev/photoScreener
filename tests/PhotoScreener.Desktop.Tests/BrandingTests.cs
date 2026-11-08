using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using PhotoScreener.Desktop;
using Xunit;

namespace PhotoScreener.Desktop.Tests;
public sealed class BrandingTests
{
    [AvaloniaFact]
    public async Task SplashHandsOffToReviewWithoutLosingFocusOrBrandAssets()
    {
        MainWindow? main = null; Window? adopted = null; SplashWindow? splash = null;
        try
        {
            splash = new SplashWindow(window => adopted = window, () =>
            {
                Assert.True(splash!.IsVisible);
                // Optional rendered artifact for visual review, independent of photo benchmarks.
                if (Environment.GetEnvironmentVariable("LUMAPICK_BRAND_PREVIEW") is { Length: > 0 } preview)
                {
                    using var render = new RenderTargetBitmap(new PixelSize(1040, 700), new Vector(192, 192));
                    render.Render(splash); render.Save(preview);
                }
                return main = new MainWindow();
            });
            splash.Show(); Dispatcher.UIThread.RunJobs();
            Assert.NotNull(main); Assert.Same(main, adopted);
            Assert.False(splash.IsVisible); Assert.True(main.IsVisible);
            Assert.Equal("LumaPick", main.Title); Assert.NotNull(main.Icon); Assert.NotNull(splash.Icon);
            Assert.True(main.FindControl<Control>("WelcomeBrand")!.IsVisible);
            Assert.True(main.FindControl<Control>("ReviewSurface")!.IsFocused);
        }
        finally
        {
            splash?.Close();
            if (main != null)
            {
                main.Close();
                for (var i = 0; i < 300 && main.IsVisible; i++) { await Task.Delay(10); Dispatcher.UIThread.RunJobs(); }
                Assert.False(main.IsVisible);
            }
        }
    }

    [AvaloniaFact]
    public void ClosingSplashBeforeHandoffDoesNotOpenAnotherWindow()
    {
        var created = false;
        var splash = new SplashWindow(_ => Assert.Fail("A closed splash must not adopt a window."), () => { created = true; return new Window(); });
        splash.Show(); splash.Close(); Dispatcher.UIThread.RunJobs();
        Assert.False(created); Assert.False(splash.IsVisible);
    }
}
