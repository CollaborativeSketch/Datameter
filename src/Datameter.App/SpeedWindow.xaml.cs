using System.Runtime.InteropServices;
using Datameter.Core;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using Windows.Graphics;

namespace Datameter.App;

/// <summary>
/// The floating speed meter: a small always-on-top chip showing what is moving right now.
///
/// It is deliberately a separate window rather than part of the page. The point of a meter is
/// that it stays visible while you are working in something else, which nothing inside the main
/// window can do.
/// </summary>
public sealed partial class SpeedWindow : Window
{
    /// <summary>
    /// Fixed size in DIPs. Sizing to content would make the chip breathe as the figures change
    /// width, which is very noticeable on something pinned above other windows, so the numbers
    /// are right-aligned inside a constant frame instead.
    /// </summary>
    /// <summary>
    /// 136 is not arbitrary: Windows will not make a top-level window narrower than roughly
    /// that at 100% scaling, so asking for less produced a chip whose proportions changed with
    /// the display. Asking for the floor keeps it identical at every scale factor.
    /// </summary>
    private const double WidthDips = 136;
    private const double HeightDips = 56;

    /// <summary>Gap from the working area's corner when no saved position applies.</summary>
    private const int DefaultMargin = 16;

    private readonly IntPtr _hwnd;
    private bool _dragging;
    private Point _grab;
    private bool _closingProgrammatically;
    private bool _closed;

    public SpeedWindow()
    {
        InitializeComponent();

        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);

        Title = "Datameter speed meter";

        var presenter = OverlappedPresenter.Create();
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsAlwaysOnTop = true;
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        AppWindow.SetPresenter(presenter);

        // A meter is not a place you alt-tab to.
        AppWindow.IsShownInSwitchers = false;


        ApplySize();

        Root.PointerPressed += OnPointerPressed;
        Root.PointerMoved += OnPointerMoved;
        Root.PointerReleased += OnPointerReleased;
        Root.PointerCaptureLost += (_, _) => EndDrag();
        Root.RightTapped += OnRightTapped;
        Root.DoubleTapped += (_, _) => ShowMainRequested?.Invoke(this, EventArgs.Empty);

        Root.Loaded += (_, _) => ApplySize();

        Closed += (_, _) =>
        {
            _closed = true;
            if (!_closingProgrammatically) HideRequested?.Invoke(this, EventArgs.Empty);
        };
    }

    /// <summary>Raised when the meter should be taken off screen and the preference updated.</summary>
    public event EventHandler? HideRequested;

    /// <summary>Raised when the user asks for the main window from the meter.</summary>
    public event EventHandler? ShowMainRequested;

    /// <summary>Raised after a drag, with the meter's new position in physical pixels.</summary>
    public event EventHandler<PointInt32>? PositionChanged;

    // ---- appearance ----------------------------------------------------------

    public void ApplyTheme(ElementTheme theme)
    {
        Root.RequestedTheme = theme;

        // ActualTheme is what Palette keys on, and it only settles once the requested theme has
        // been resolved against the tree above it.
        var resolved = theme == ElementTheme.Default ? Root.ActualTheme : theme;

        Root.Background = Palette.MeterBackground(resolved);
        Root.BorderBrush = Palette.MeterStroke(resolved);
        UpArrow.Foreground = Palette.Upload(resolved);
        DownArrow.Foreground = Palette.Download(resolved);
        UpValue.Foreground = Palette.TextPrimary(resolved);
        DownValue.Foreground = Palette.TextPrimary(resolved);
    }

    public void Show(SpeedSample sample)
    {
        UpValue.Text = ByteFormat.HumanizeRate(sample.SentPerSecond);
        DownValue.Text = ByteFormat.HumanizeRate(sample.ReceivedPerSecond);

        var adapter = string.IsNullOrWhiteSpace(sample.InterfaceName) ? "this PC" : sample.InterfaceName;
        ToolTipService.SetToolTip(Root, $"Live speed on {adapter}. Drag to move, double-click to open Datameter.");
    }

    // ---- placement -----------------------------------------------------------

    /// <summary>
    /// Puts the meter back where it was left, or in the bottom-right corner of the working area.
    /// A saved position that no longer lands on a connected display is discarded, so unplugging
    /// a monitor cannot strand the meter off screen.
    /// </summary>
    public void Place(int? savedX, int? savedY)
    {
        var size = AppWindow.Size;

        if (savedX is { } x && savedY is { } y && IsOnADisplay(x, y, size))
        {
            AppWindow.Move(new PointInt32(x, y));
            return;
        }

        var work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        AppWindow.Move(new PointInt32(
            work.X + work.Width - size.Width - DefaultMargin,
            work.Y + work.Height - size.Height - DefaultMargin));
    }

    private static bool IsOnADisplay(int x, int y, SizeInt32 size)
    {
        // Test the middle of the chip rather than its origin: a meter pushed slightly past an
        // edge is still reachable, one whose centre is off screen is not.
        var centre = new PointInt32(x + (size.Width / 2), y + (size.Height / 2));
        return DisplayArea.GetFromPoint(centre, DisplayAreaFallback.None) is not null;
    }

    private void ApplySize()
    {
        var scale = Root.XamlRoot?.RasterizationScale ?? (GetDpiForWindow(_hwnd) / 96.0);
        if (scale <= 0) scale = 1;

        var wanted = new SizeInt32((int)Math.Round(WidthDips * scale), (int)Math.Round(HeightDips * scale));
        if (AppWindow.Size.Width != wanted.Width || AppWindow.Size.Height != wanted.Height)
            AppWindow.Resize(wanted);

        RoundCorners(wanted, scale);
    }

    // ---- dragging ------------------------------------------------------------

    /// <summary>
    /// A borderless window has no caption to drag by, so the chip moves itself.
    ///
    /// The window moves by the pointer's delta within it, which is self-correcting: after the
    /// move the pointer sits at the same point relative to the window again, so the next delta
    /// starts from zero and the chip cannot run away from the cursor.
    /// </summary>
    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(Root);
        if (!point.Properties.IsLeftButtonPressed) return;

        _grab = point.Position;
        _dragging = Root.CapturePointer(e.Pointer);
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;

        var position = e.GetCurrentPoint(Root).Position;
        var scale = Root.XamlRoot?.RasterizationScale ?? 1;

        var dx = (int)Math.Round((position.X - _grab.X) * scale);
        var dy = (int)Math.Round((position.Y - _grab.Y) * scale);
        if (dx == 0 && dy == 0) return;

        var at = AppWindow.Position;
        AppWindow.Move(new PointInt32(at.X + dx, at.Y + dy));
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_dragging) Root.ReleasePointerCapture(e.Pointer);
        EndDrag();
    }

    private void EndDrag()
    {
        if (!_dragging) return;
        _dragging = false;

        // Moving between monitors can change the scale factor under us.
        ApplySize();
        PositionChanged?.Invoke(this, AppWindow.Position);
    }

    // ---- menu ----------------------------------------------------------------

    private void OnRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        var menu = new MenuFlyout { Placement = FlyoutPlacementMode.Bottom };

        var open = new MenuFlyoutItem { Text = "Open Datameter" };
        open.Click += (_, _) => ShowMainRequested?.Invoke(this, EventArgs.Empty);

        var hide = new MenuFlyoutItem { Text = "Hide speed meter" };
        hide.Click += (_, _) => HideRequested?.Invoke(this, EventArgs.Empty);

        menu.Items.Add(open);
        menu.Items.Add(hide);
        menu.ShowAt(Root, new FlyoutShowOptions { Position = e.GetPosition(Root) });
    }

    /// <summary>Closes without reporting it as the user dismissing the meter.</summary>
    public void CloseQuietly()
    {
        // Dismissing the meter from its own menu arrives here by way of Closed, so by the time
        // the preference has been saved the window may already be gone.
        if (_closed) return;

        _closingProgrammatically = true;
        Close();
    }

    // ---- native --------------------------------------------------------------

    private const int DwmBorderColor = 34;

    /// <summary>DWMWA_COLOR_NONE: draw no frame at all.</summary>
    private const int DwmColorNone = unchecked((int)0xFFFFFFFE);

    /// <summary>Corner radius in DIPs, matching the CornerRadius the chip is drawn with.</summary>
    private const double CornerRadiusDips = 7;

    /// <summary>
    /// Clips the window itself to the chip's rounded outline.
    ///
    /// DWM only rounds windows that have a frame, and this one has none, so the square window
    /// showed at each corner as a dark wedge outside the chip's own radius. A window region is
    /// the one thing that actually removes those pixels. Its edge is not antialiased, which at
    /// this radius costs less than the wedges did.
    /// </summary>
    private void RoundCorners(SizeInt32 size, double scale)
    {
        try
        {
            // CreateRoundRectRgn takes the width and height of the corner ellipse, so twice the
            // radius, and its right and bottom edges are exclusive.
            var diameter = (int)Math.Round(CornerRadiusDips * 2 * scale);
            var region = CreateRoundRectRgn(0, 0, size.Width + 1, size.Height + 1, diameter, diameter);
            if (region == IntPtr.Zero) return;

            // The window takes ownership of the region on success; on failure we own it still.
            if (SetWindowRgn(_hwnd, region, true) == 0) DeleteObject(region);

            // Nothing draws the frame now, but a themed border would still be composited over
            // the clipped edge on Windows 11.
            var border = DwmColorNone;
            DwmSetWindowAttribute(_hwnd, DwmBorderColor, ref border, sizeof(int));
        }
        catch
        {
        }
    }

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int width, int height);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    private static extern bool DeleteObject(IntPtr handle);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int SetWindowRgn(IntPtr hwnd, IntPtr region, bool redraw);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
}
