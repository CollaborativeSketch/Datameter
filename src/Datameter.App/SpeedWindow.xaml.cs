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
/// The floating speed meter: a small always-on-top card showing what is moving right now.
///
/// It is deliberately a separate window rather than part of the page. The point of a meter is
/// that it stays visible while you are working in something else, which nothing inside the main
/// window can do.
///
/// The window and the card are not the same rectangle, and most of the work here is keeping that
/// straight. Hiding the border through the presenter hides it without removing the frame: the
/// window keeps a few pixels of non-client edge on every side, which renders as a pale rim, and
/// Windows will not make it narrower than about 136 pixels however small the card is meant to
/// be. Clearing the style bits does not hold, because the presenter puts them back. So the
/// window is left to be whatever Windows insists on and clipped down to the card inside it, and
/// everything to do with position works in card coordinates, converting at the boundary.
/// </summary>
public sealed partial class SpeedWindow : Window
{
    /// <summary>
    /// Everything one size setting controls. Font and glyph move with the card, so a larger
    /// meter is genuinely more legible rather than the same text in more space.
    /// </summary>
    private sealed record Metrics(
        double Width, double Height, double FontSize, double GlyphSize,
        Thickness Padding, double ColumnGap);

    private static Metrics MetricsFor(MeterSizeOption size) => size switch
    {
        // The reading sits immediately after its glyph rather than across the slab, so the gap
        // is the column gap and nothing more. Widths are the longest reading the meter can
        // show, "999.9 KB/s", plus the glyph, that gap and the padding.
        MeterSizeOption.Small => new(86, 40, 10.5, 11, new Thickness(7, 2, 8, 2), 3),
        MeterSizeOption.Large => new(126, 62, 15, 16, new Thickness(10, 5, 11, 5), 4),
        _ => new(100, 48, 12, 13, new Thickness(8, 3, 9, 3), 3),
    };

    /// <summary>Gap from the working area's corner when no saved position applies.</summary>
    private const int DefaultMargin = 16;

    private readonly IntPtr _hwnd;
    private MeterSizeOption _size = MeterSizeOption.Medium;

    /// <summary>The card's size in physical pixels.</summary>
    private SizeInt32 _card;

    /// <summary>Where the client area begins inside the window, in physical pixels.</summary>
    private int _insetX;
    private int _insetY;

    /// <summary>
    /// Whether the meter has been put where it belongs yet. Until it has, its position is
    /// wherever the window happened to open, and reporting that as a change would overwrite
    /// the saved position with a meaningless one before it has been read back.
    /// </summary>
    private bool _placed;

    private bool _dragging;
    private Point _grab;
    private bool _closingProgrammatically;
    private bool _closed;

    public SpeedWindow()
    {
        InitializeComponent();

        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);

        Title = AppInfo.MeterWindowTitle;

        var presenter = OverlappedPresenter.Create();
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsAlwaysOnTop = true;
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        AppWindow.SetPresenter(presenter);

        // A meter is not a place you alt-tab to.
        AppWindow.IsShownInSwitchers = false;

        ApplyMetrics();

        Root.PointerPressed += OnPointerPressed;
        Root.PointerMoved += OnPointerMoved;
        Root.PointerReleased += OnPointerReleased;
        Root.PointerCaptureLost += (_, _) => EndDrag();
        Root.RightTapped += OnRightTapped;
        Root.DoubleTapped += (_, _) => ShowMainRequested?.Invoke(this, EventArgs.Empty);

        // The frame is only measurable once the window has been realised, and the scale factor
        // only known once there is a XamlRoot, so the layout is settled a second time here.
        Root.Loaded += (_, _) => ApplyMetrics();

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

    /// <summary>Raised after a drag, with the card's resting position in physical pixels.</summary>
    public event EventHandler<PointInt32>? PositionChanged;

    // ---- appearance ----------------------------------------------------------

    public void ApplyTheme(ElementTheme theme)
    {
        Root.RequestedTheme = theme;

        // The slab's ground is dark whatever the app is showing, so its contents are coloured
        // for a dark ground rather than for the current theme.
        Root.Background = Palette.MeterBackground();

        UpArrow.Foreground = Palette.Upload(ElementTheme.Dark);
        DownArrow.Foreground = Palette.Download(ElementTheme.Dark);
        UpValue.Foreground = Palette.MeterText();
        DownValue.Foreground = Palette.MeterText();
    }

    /// <summary>Resizes the card, its text and its glyphs together.</summary>
    public void SetSize(MeterSizeOption size)
    {
        if (_size == size) return;

        _size = size;

        var before = CardPosition;
        ApplyMetrics();

        // Resizing must not shift the slab: it keeps the corner it was left at.
        MoveCardTo(before, announce: _placed);
    }

    public void Show(SpeedSample sample)
    {
        UpValue.Text = ByteFormat.HumanizeRate(sample.SentPerSecond);
        DownValue.Text = ByteFormat.HumanizeRate(sample.ReceivedPerSecond);

        var adapter = string.IsNullOrWhiteSpace(sample.InterfaceName) ? "this PC" : sample.InterfaceName;
        ToolTipService.SetToolTip(Root, $"Live speed on {adapter}. Drag to move, double-click to open Datameter.");
    }

    // ---- placement -----------------------------------------------------------

    /// <summary>Where the card sits on screen, which is inside the window by the frame inset.</summary>
    private PointInt32 CardPosition
    {
        get
        {
            var at = AppWindow.Position;
            return new PointInt32(at.X + _insetX, at.Y + _insetY);
        }
    }

    private void MoveCardTo(PointInt32 card, bool announce)
    {
        var target = new PointInt32(card.X - _insetX, card.Y - _insetY);
        if (target.X != AppWindow.Position.X || target.Y != AppWindow.Position.Y)
            AppWindow.Move(target);

        if (announce) PositionChanged?.Invoke(this, card);
    }

    /// <summary>
    /// Puts the meter back where it was left, or in the bottom-right corner of the working area.
    /// A saved position that no longer lands on a connected display is discarded, so unplugging
    /// a monitor cannot strand the meter off screen.
    /// </summary>
    public void Place(int? savedX, int? savedY)
    {
        if (savedX is { } x && savedY is { } y && IsOnADisplay(x, y))
        {
            MoveCardTo(new PointInt32(x, y), announce: false);
        }
        else
        {
            var work = WorkAreaFor(CardPosition);
            MoveCardTo(
                new PointInt32(
                    work.X + work.Width - _card.Width - DefaultMargin,
                    work.Y + work.Height - _card.Height - DefaultMargin),
                announce: false);
        }

        _placed = true;
    }

    private bool IsOnADisplay(int x, int y)
    {
        // Test the middle of the card rather than its origin: a meter pushed slightly past an
        // edge is still reachable, one whose centre is off screen is not.
        var centre = new PointInt32(x + (_card.Width / 2), y + (_card.Height / 2));
        return DisplayArea.GetFromPoint(centre, DisplayAreaFallback.None) is not null;
    }

    /// <summary>The working area of the display the card's centre is on.</summary>
    private RectInt32 WorkAreaFor(PointInt32 card)
    {
        var centre = new PointInt32(card.X + (_card.Width / 2), card.Y + (_card.Height / 2));
        var area = DisplayArea.GetFromPoint(centre, DisplayAreaFallback.None)
                   ?? DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);

        return area.WorkArea;
    }

    /// <summary>
    /// Lays the card out at the chosen size, sizes the window to hold it, and clips the window
    /// down to the card so that neither the frame nor any width Windows insisted on is visible.
    /// </summary>
    private void ApplyMetrics()
    {
        var metrics = MetricsFor(_size);
        var scale = Root.XamlRoot?.RasterizationScale ?? (GetDpiForWindow(_hwnd) / 96.0);
        if (scale <= 0) scale = 1;

        Root.Width = metrics.Width;
        Root.Height = metrics.Height;
        Root.Padding = metrics.Padding;
        Layout.ColumnSpacing = metrics.ColumnGap;

        UpValue.FontSize = metrics.FontSize;
        DownValue.FontSize = metrics.FontSize;
        UpArrow.FontSize = metrics.GlyphSize;
        DownArrow.FontSize = metrics.GlyphSize;

        _card = new SizeInt32(
            (int)Math.Round(metrics.Width * scale),
            (int)Math.Round(metrics.Height * scale));

        // Ask for a window whose client area is exactly the card. Windows may hand back a wider
        // one; the region trims whatever it decided to add.
        var frame = MeasureFrame();
        var wanted = new SizeInt32(_card.Width + frame.Width, _card.Height + frame.Height);
        if (AppWindow.Size.Width != wanted.Width || AppWindow.Size.Height != wanted.Height)
            AppWindow.Resize(wanted);

        // Re-measured after the resize: the inset is what the region and every position
        // conversion are pinned to, so it has to describe the window as it now is.
        frame = MeasureFrame();
        _insetX = frame.Left;
        _insetY = frame.Top;

        ClipToCard();
    }

    /// <summary>
    /// The window's frame in physical pixels: where the client area begins inside the window,
    /// and how much larger the window is than its client area.
    /// </summary>
    private (int Left, int Top, int Width, int Height) MeasureFrame()
    {
        try
        {
            if (!GetWindowRect(_hwnd, out var window)) return default;
            if (!GetClientRect(_hwnd, out var client)) return default;

            var origin = default(NativePoint);
            if (!ClientToScreen(_hwnd, ref origin)) return default;

            return (
                origin.X - window.Left,
                origin.Y - window.Top,
                (window.Right - window.Left) - client.Right,
                (window.Bottom - window.Top) - client.Bottom);
        }
        catch
        {
            return default;
        }
    }

    // ---- dragging ------------------------------------------------------------

    /// <summary>
    /// A borderless window has no caption to drag by, so the card moves itself.
    ///
    /// The window moves by the pointer's delta within it, which is self-correcting: after the
    /// move the pointer sits at the same point relative to the window again, so the next delta
    /// starts from zero and the card cannot run away from the cursor.
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
        ApplyMetrics();

        // Wherever it was let go is where it stays.
        PositionChanged?.Invoke(this, CardPosition);
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

    /// <summary>
    /// Clips the window to the slab, at the slab's place inside it.
    ///
    /// This is what takes the window frame out of view and what makes the small size actually
    /// small. The slab has square corners, so a plain rectangle is all that is wanted here.
    /// </summary>
    private void ClipToCard()
    {
        try
        {
            // The right and bottom edges are exclusive, so the far corner is the slab's extent
            // exactly: a pixel beyond would leave a hairline of unpainted window showing down
            // the right edge and along the bottom.
            var region = CreateRectRgn(
                _insetX, _insetY,
                _insetX + _card.Width, _insetY + _card.Height);

            if (region == IntPtr.Zero) return;

            // The window takes ownership of the region on success; on failure we own it still.
            if (SetWindowRgn(_hwnd, region, true) == 0) DeleteObject(region);
        }
        catch
        {
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X, Y;
    }

    [DllImport("gdi32.dll", ExactSpelling = true)]
    private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    private static extern bool DeleteObject(IntPtr handle);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int SetWindowRgn(IntPtr hwnd, IntPtr region, bool redraw);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool GetClientRect(IntPtr hwnd, out NativeRect rect);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool ClientToScreen(IntPtr hwnd, ref NativePoint point);
}
