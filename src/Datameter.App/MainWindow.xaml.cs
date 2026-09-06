using System.Runtime.InteropServices;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace Datameter.App;

public sealed partial class MainWindow : Window
{
    /// <summary>The size the page is designed for, in device-independent pixels.</summary>
    private const double PreferredWidthDips = 1000;
    private const double PreferredHeightDips = 900;

    /// <summary>Below this the page stops being readable, so the window will not go smaller.</summary>
    private const double MinimumWidthDips = 640;
    private const double MinimumHeightDips = 520;

    /// <summary>Kept clear of the working area's edges so the window never opens flush.</summary>
    private const int EdgeMargin = 24;

    public MainWindow()
    {
        InitializeComponent();

        Title = AppInfo.DisplayName;
        AppTitleText.Text = AppInfo.DisplayName;
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        SizeToDisplay();
        SetWindowIcon();

        if (MicaController.IsSupported())
            SystemBackdrop = new MicaBackdrop();

        // Closing the window normally leaves Datameter running in the notification area: the
        // history it accumulates past Windows' own retention is only collected while it runs.
        // Quitting from the tray sets App.IsExiting, and that is what makes a close a close.
        AppWindow.Closing += (_, args) =>
        {
            if (App.IsExiting || !Page.RunInBackground) return;

            args.Cancel = true;
            AppWindow.Hide();
        };

        // The floating meter and the tray icon are separate windows, and either would keep the
        // process alive after this one has really closed.
        Closed += (_, _) => Page.Shutdown();
    }

    /// <summary>
    /// Opens at the intended size for this display, rather than at a fixed number of pixels.
    ///
    /// AppWindow.Resize takes physical pixels, so a literal 1000 by 900 opens at two thirds of
    /// the intended size at 150% scaling and half at 200%. Unscaled it is also taller than a
    /// 1366 by 768 laptop screen, which put the chart below the bottom edge with no way to
    /// reach it. So the size is scaled for the display and then clamped to what will fit.
    /// </summary>
    private void SizeToDisplay()
    {
        var scale = 1.0;
        try
        {
            var dpi = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
            if (dpi > 0) scale = dpi / 96.0;
        }
        catch
        {
        }

        var work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;

        var width = Clamp(PreferredWidthDips, MinimumWidthDips, scale, work.Width);
        var height = Clamp(PreferredHeightDips, MinimumHeightDips, scale, work.Height);

        AppWindow.Resize(new SizeInt32(width, height));

        // A window that cannot be shrunk into uselessness, in the same scaled units.
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = (int)Math.Round(MinimumWidthDips * scale);
            presenter.PreferredMinimumHeight = (int)Math.Round(MinimumHeightDips * scale);
        }
    }

    /// <summary>
    /// The preferred size in physical pixels, cut down to the working area but never below the
    /// minimum — a screen too small for the minimum gets the minimum and a scroll bar, which is
    /// better than a window sized to nothing.
    /// </summary>
    private static int Clamp(double preferredDips, double minimumDips, double scale, int available)
    {
        var preferred = (int)Math.Round(preferredDips * scale);
        var minimum = (int)Math.Round(minimumDips * scale);

        return Math.Max(minimum, Math.Min(preferred, available - (EdgeMargin * 2)));
    }

    /// <summary>
    /// Gives the window the icon compiled into the executable.
    ///
    /// A WinUI 3 window is not given one automatically, and a window with no icon is drawn as
    /// a blank tile in the taskbar, in Alt+Tab and above the taskbar's thumbnail preview — the
    /// mark shown in the title bar is the page's own drawing and does not reach any of those.
    ///
    /// Both sizes are taken, rather than one and left to Windows: the icon carries an entry
    /// drawn for 16 pixels, and asking for it by name is the whole reason it is in there.
    /// </summary>
    private void SetWindowIcon()
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrEmpty(executable)) return;

        try
        {
            if (ExtractIconEx(executable, 0, out var large, out var small, 1) == 0) return;

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);

            // The handles stay alive for as long as the window does, which is the process.
            if (large != IntPtr.Zero) SendMessage(hwnd, WmSetIcon, IconBig, large);
            if (small != IntPtr.Zero) SendMessage(hwnd, WmSetIcon, IconSmall, small);
        }
        catch
        {
            // An icon is worth having, not worth failing to open the window over.
        }
    }

    private const uint WmSetIcon = 0x0080;
    private static readonly IntPtr IconSmall = IntPtr.Zero;
    private static readonly IntPtr IconBig = new(1);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string file, int index, out IntPtr large, out IntPtr small, uint count);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
}
