using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Updatr.App.ViewModels;
using Updatr.App.Views;
using Updatr.Core.Diagnostics;
using Updatr.Core.Preflight;
using Updatr.Platform;

namespace Updatr.App;

public partial class App : Application
{
    private UpdatrHost? _host;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            desktop.ShutdownRequested += (_, _) => _host?.DisposeAsync().AsTask().GetAwaiter().GetResult();

            AppDomain.CurrentDomain.UnhandledException += (_, e) => DiagLog.UnhandledException("app domain", (Exception)e.ExceptionObject);
            TaskScheduler.UnobservedTaskException += (_, e) => DiagLog.UnhandledException("background task", e.Exception);

            SplashViewModel splash = new(new HostOptions("gui", DataRootFrom(desktop.Args ?? [])));
            SplashWindow splashWindow = new() { DataContext = splash };

            splash.QuitRequested += () => desktop.Shutdown();
            splash.Finished += (host, report, runner) => ShowMain(desktop, splashWindow, host, report, runner);

            desktop.MainWindow = splashWindow;
            splashWindow.Opened += async (_, _) => await splash.StartAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void ShowMain(IClassicDesktopStyleApplicationLifetime desktop, Window splash, UpdatrHost host, PreflightReport report, PreflightRunner runner)
    {
        _host = host;
        MainWindow main = new() { DataContext = new MainWindowViewModel(host, report, runner) };

        // Swap MainWindow before closing the splash, or closing it would end the app.
        desktop.MainWindow = main;
        main.Show();
        splash.Close();
    }

    /// <summary>The GUI's only option: <c>--data-dir &lt;path&gt;</c>, same as the CLI's.</summary>
    private static string? DataRootFrom(string[] args)
    {
        int index = Array.IndexOf(args, "--data-dir");
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
