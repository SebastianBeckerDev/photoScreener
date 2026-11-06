using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace PhotoScreener.Desktop;
public sealed class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime lifetime)
            lifetime.MainWindow = new MainWindow(Program.Arguments, new PhotoScreener.Infrastructure.SessionSettingsStore());
        base.OnFrameworkInitializationCompleted();
    }
}
