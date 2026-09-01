using Microsoft.UI.Xaml;

namespace Datameter.App;

public partial class App : Application
{
    private Window? _window;

    /// <summary>
    /// Crash log. A WinUI 3 app that faults during startup dies without a dialog and without
    /// an event-log entry, so anything fatal gets written here before the process goes.
    /// </summary>
    public static string LogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Datameter", "error.log");

    public App()
    {
        InitializeComponent();

        UnhandledException += (_, e) =>
        {
            Log("UnhandledException", e.Exception);
            e.Handled = true;   // keep the window alive so the failure is visible, not silent
        };

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log("AppDomain", e.ExceptionObject as Exception);

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log("UnobservedTask", e.Exception);
            e.SetObserved();
        };
    }

    public static void Log(string source, Exception? ex)
    {
        try
        {
            var dir = Path.GetDirectoryName(LogPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.AppendAllText(LogPath, $"[{DateTimeOffset.Now:u}] {source}: {ex}\n\n");
        }
        catch
        {
            // Logging must never be the thing that takes the app down.
        }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();
    }
}
