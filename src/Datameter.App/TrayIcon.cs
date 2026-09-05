using System.Runtime.InteropServices;

namespace Datameter.App;

/// <summary>One line of the notification-area menu.</summary>
public sealed record TrayMenuItem(string Text, Action Invoke)
{
    /// <summary>A separator. Its text is ignored and it does nothing when chosen.</summary>
    public static TrayMenuItem Separator { get; } = new("-", () => { });

    public bool IsSeparator => ReferenceEquals(this, Separator);
}

/// <summary>
/// The notification-area icon, and the only way back to a window that has been closed to the
/// background.
///
/// WinUI 3 has no tray API, so this is Shell_NotifyIcon driven from a message-only window of its
/// own. A message-only window is the standard host: it never renders, but it has a window
/// procedure, which is what the shell needs somewhere to send the icon's mouse messages.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private const int WmDestroy = 0x0002;
    private const int WmNull = 0x0000;
    private const int WmTrayCallback = 0x0400 + 1;   // WM_APP + 1

    private const int WmLButtonUp = 0x0202;
    private const int WmLButtonDblClk = 0x0203;
    private const int WmRButtonUp = 0x0205;
    private const int WmContextMenu = 0x007B;

    private const int NimAdd = 0x0000;
    private const int NimModify = 0x0001;
    private const int NimDelete = 0x0002;

    private const int NifMessage = 0x0001;
    private const int NifIcon = 0x0002;
    private const int NifTip = 0x0004;

    private const int TpmRightButton = 0x0002;
    private const int TpmReturnCmd = 0x0100;

    private const int MfString = 0x0000;
    private const int MfSeparator = 0x0800;

    /// <summary>Message-only parent. Windows under it are never shown and never painted.</summary>
    private static readonly IntPtr HwndMessage = new(-3);

    /// <summary>Menu command ids start above zero, because TrackPopupMenu returns 0 for "nothing".</summary>
    private const int FirstCommandId = 1;

    private readonly WndProc _wndProc;   // held so the delegate is not collected under native code
    private readonly IntPtr _hwnd;
    private readonly string _className;

    /// <summary>
    /// Held for the icon's lifetime, not just for the handle. Icon owns its HICON and destroys
    /// it when finalised, so letting this go out of scope would leave the shell drawing from a
    /// handle that has been freed — a blank tray icon, appearing at a garbage collection rather
    /// than at anything to do with the code.
    /// </summary>
    private readonly System.Drawing.Icon? _iconSource;
    private readonly IntPtr _icon;

    private List<TrayMenuItem> _menu = new();
    private string _tooltip = "";
    private bool _added;
    private bool _disposed;

    public TrayIcon(string tooltip)
    {
        _wndProc = HandleMessage;
        _className = "DatameterTray_" + Guid.NewGuid().ToString("N");

        var wc = new WindowClass
        {
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = GetModuleHandle(null),
            lpszClassName = _className
        };

        if (RegisterClass(ref wc) == 0) return;

        _hwnd = CreateWindowEx(
            0, _className, "Datameter", 0, 0, 0, 0, 0,
            HwndMessage, IntPtr.Zero, wc.hInstance, IntPtr.Zero);

        if (_hwnd == IntPtr.Zero) return;

        _iconSource = LoadAppIcon();
        _icon = _iconSource?.Handle ?? IntPtr.Zero;
        _tooltip = Trim(tooltip);

        var data = Describe(NifMessage | NifIcon | NifTip);
        _added = Shell_NotifyIcon(NimAdd, ref data);
    }

    /// <summary>True when the icon is actually in the notification area.</summary>
    public bool IsVisible => _added;

    /// <summary>Raised when the icon is clicked or double-clicked.</summary>
    public event EventHandler? Opened;

    /// <summary>
    /// Supplies the menu at the moment it is opened, so it can describe the current state
    /// rather than whatever was true when the icon was created.
    /// </summary>
    public Func<IReadOnlyList<TrayMenuItem>>? BuildMenu { get; set; }

    /// <summary>Replaces the hover text. Windows truncates it at 127 characters.</summary>
    public void SetTooltip(string tooltip)
    {
        if (!_added) return;

        var trimmed = Trim(tooltip);
        if (trimmed == _tooltip) return;

        _tooltip = trimmed;
        var data = Describe(NifTip);
        Shell_NotifyIcon(NimModify, ref data);
    }

    private static string Trim(string text) =>
        text.Length <= 127 ? text : text[..127];

    private NotifyIconData Describe(int flags) => new()
    {
        cbSize = Marshal.SizeOf<NotifyIconData>(),
        hWnd = _hwnd,
        uID = 1,
        uFlags = flags,
        uCallbackMessage = WmTrayCallback,
        hIcon = _icon,
        szTip = _tooltip
    };

    private IntPtr HandleMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam)
    {
        switch (message)
        {
            case WmTrayCallback:
                switch ((int)lParam)
                {
                    case WmLButtonUp:
                    case WmLButtonDblClk:
                        Opened?.Invoke(this, EventArgs.Empty);
                        return IntPtr.Zero;

                    case WmRButtonUp:
                    case WmContextMenu:
                        ShowMenu();
                        return IntPtr.Zero;
                }
                break;

            // No WM_COMMAND case: the menu is tracked with TPM_RETURNCMD, so the chosen item
            // comes back as a return value instead. Handling both would invoke it twice.
            case WmDestroy:
                return IntPtr.Zero;
        }

        return DefWindowProc(hwnd, message, wParam, lParam);
    }

    private void ShowMenu()
    {
        var items = BuildMenu?.Invoke();
        if (items is null || items.Count == 0) return;

        _menu = items.ToList();

        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero) return;

        try
        {
            for (var i = 0; i < _menu.Count; i++)
            {
                var item = _menu[i];
                if (item.IsSeparator) AppendMenu(menu, MfSeparator, IntPtr.Zero, null);
                else AppendMenu(menu, MfString, new IntPtr(FirstCommandId + i), item.Text);
            }

            if (!GetCursorPos(out var cursor)) return;

            // Without this the menu does not close when you click elsewhere, and the WM_NULL
            // afterwards is what lets it close the first time rather than the second.
            SetForegroundWindow(_hwnd);

            var chosen = TrackPopupMenuEx(
                menu, TpmRightButton | TpmReturnCmd, cursor.X, cursor.Y, _hwnd, IntPtr.Zero);

            PostMessage(_hwnd, WmNull, IntPtr.Zero, IntPtr.Zero);

            var index = chosen - FirstCommandId;
            if (index >= 0 && index < _menu.Count) _menu[index].Invoke();
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    /// <summary>
    /// The app's own icon, taken from the running executable so the tray matches the taskbar
    /// and the installer without shipping a second copy of the artwork.
    /// </summary>
    private static System.Drawing.Icon? LoadAppIcon()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe)) return System.Drawing.Icon.ExtractAssociatedIcon(exe);
        }
        catch
        {
        }

        return null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            if (_added)
            {
                var data = Describe(0);
                Shell_NotifyIcon(NimDelete, ref data);
                _added = false;
            }

            if (_hwnd != IntPtr.Zero) DestroyWindow(_hwnd);
            if (!string.IsNullOrEmpty(_className)) UnregisterClass(_className, GetModuleHandle(null));

            _iconSource?.Dispose();
        }
        catch
        {
            // A tray icon that will not tidy itself up must not stop the app closing.
        }
    }

    // ---- native --------------------------------------------------------------

    private delegate IntPtr WndProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass
    {
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public int dwState;
        public int dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public int uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X, Y;
    }

    [DllImport("user32.dll", EntryPoint = "RegisterClassW", CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClass(ref WindowClass windowClass);

    [DllImport("user32.dll", EntryPoint = "UnregisterClassW", CharSet = CharSet.Unicode)]
    private static extern bool UnregisterClass(string className, IntPtr instance);

    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(
        int exStyle, string className, string windowName, int style,
        int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll", EntryPoint = "DefWindowProcW", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);

    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(int message, ref NotifyIconData data);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", EntryPoint = "AppendMenuW", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr menu, int flags, IntPtr id, string? item);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenuEx(
        IntPtr menu, int flags, int x, int y, IntPtr hwnd, IntPtr parameters);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll", EntryPoint = "PostMessageW", CharSet = CharSet.Unicode)]
    private static extern bool PostMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);
}
