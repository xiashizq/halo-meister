using Microsoft.UI.Xaml;

namespace HaloMeister.App;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();
    }

    /// <summary>
    /// Called when a second process redirects activation to this instance.
    /// </summary>
    internal void ActivateMainWindow()
    {
        if (_window is MainWindow main)
        {
            main.BringToForeground();
            return;
        }

        _window?.Activate();
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        LogCrash("XamlUnhandled", e.Exception);
        e.Handled = true;
        try
        {
            if (_window is MainWindow main)
                main.ReportCrash(e.Exception);
        }
        catch
        {
            // Avoid secondary failures while reporting.
        }
    }

    private static void OnDomainUnhandledException(object sender, System.UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
            LogCrash("DomainUnhandled", ex);
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        LogCrash("UnobservedTask", e.Exception);
        e.SetObserved();
    }

    internal static void LogCrash(string source, Exception ex)
    {
        try
        {
            string directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "HaloMeister");
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "crash.log");
            File.AppendAllText(
                path,
                $"[{DateTime.Now:O}] {source}{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // Best-effort logging only.
        }
    }
}
