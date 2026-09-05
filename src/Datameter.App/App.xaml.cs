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

    /// <summary>
    /// The live speed reading, shared by the page and the floating meter so the two can never
    /// disagree. It is a difference between counter readings, so it has to be sampled once.
    /// </summary>
    public static SpeedService Speed { get; } = new();

    /// <summary>The main window, so the meter and the tray icon can bring it forward.</summary>
    public static Window? PrimaryWindow { get; private set; }

    /// <summary>
    /// Set when the user has actually asked to quit, from the notification area. Closing the
    /// window otherwise only hides it, so this is what tells the window the difference.
    /// </summary>
    public static bool IsExiting { get; private set; }

    /// <summary>Brings the window back, whether it is behind something or hidden entirely.</summary>
    public static void ShowPrimaryWindow()
    {
        if (PrimaryWindow is not { } window) return;

        window.AppWindow.Show();
        window.Activate();
    }

    /// <summary>
    /// Quits for real, rather than closing the window to the notification area. Named Quit
    /// rather than Exit because Application.Exit already means something slightly different.
    /// </summary>
    public static void Quit()
    {
        IsExiting = true;
        PrimaryWindow?.Close();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Speed.Start();

        _window = new MainWindow();
        PrimaryWindow = _window;
        _window.Activate();
    }
}
