using Avalonia.Controls;
using Avalonia.Threading;

namespace PhotoScreener.Desktop;

// The first window stays lightweight. No timer or minimum display duration
// delays the review window or its existing asynchronous folder startup.
public partial class SplashWindow : Window
{
    private bool closed;
    public SplashWindow()
    {
        InitializeComponent();
        Closed += (_, _) => closed = true;
    }
    public SplashWindow(Action<Window> adoptMainWindow, Func<Window> createMainWindow) : this()
    {
        Opened += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            if (closed) return;
            var main = createMainWindow();
            // Assign/show the replacement before closing the splash so desktop
            // lifetime cannot shut down when the first window disappears.
            adoptMainWindow(main);
            main.Show();
            Close();
        }, DispatcherPriority.Background);
    }
}
