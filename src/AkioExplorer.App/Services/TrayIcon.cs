using System.Runtime.InteropServices;

namespace AkioExplorer.App.Services;

/// <summary>
/// Notification-area icon for "keep running in the background": click opens the window, right-click offers Exit.
/// Plain Shell_NotifyIcon on a message-only window owned by the UI thread (WinUI has no tray API).
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private const int WM_APP_TRAY = 0x8000 + 17;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_CONTEXTMENU = 0x007B;
    private const int NIN_SELECT = 0x0400;
    private const int NIN_KEYSELECT = 0x0401;
    private const uint NIM_ADD = 0, NIM_DELETE = 2, NIM_SETVERSION = 4;
    private const uint NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4;
    private const uint TPM_RETURNCMD = 0x0100, TPM_RIGHTBUTTON = 0x0002;
    private const int CmdOpen = 1, CmdExit = 2, CmdQuickSearch = 3;
    private static readonly IntPtr HWND_MESSAGE = new(-3);

    private readonly Action _open;
    private readonly Action _exit;
    private readonly Action? _quickSearch;
    private readonly Func<string?>? _quickSearchKeys;
    private readonly WndProc _proc; // kept alive: native code calls it
    private readonly uint _taskbarCreated;
    private readonly string _className = "AkioExplorerTray" + Environment.ProcessId;
    private IntPtr _hwnd;
    private IntPtr _icon;
    private bool _added;

    /// <param name="quickSearch">Opens the quick-search popup (menu item shown when given).</param>
    /// <param name="quickSearchKeys">Its current hotkey for the menu label, or null when none works.</param>
    public TrayIcon(Action open, Action exit, Action? quickSearch = null, Func<string?>? quickSearchKeys = null)
    {
        _open = open;
        _exit = exit;
        _quickSearch = quickSearch;
        _quickSearchKeys = quickSearchKeys;
        _proc = WindowProc;
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");

        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
            hInstance = GetModuleHandle(null),
            lpszClassName = _className,
        };
        RegisterClassEx(ref wc);
        _hwnd = CreateWindowEx(0, _className, "AkioExplorer tray", 0, 0, 0, 0, 0, HWND_MESSAGE, IntPtr.Zero, wc.hInstance, IntPtr.Zero);

        var stock = new SHSTOCKICONINFO { cbSize = (uint)Marshal.SizeOf<SHSTOCKICONINFO>() };
        if (SHGetStockIconInfo(3 /* SIID_FOLDER */, 0x100 | 0x1 /* SHGSI_ICON | SHGSI_SMALLICON */, ref stock) == 0) _icon = stock.hIcon;
    }

    public bool IsVisible => _added;

    public void Show()
    {
        if (_added || _hwnd == IntPtr.Zero) return;
        var data = NewData();
        data.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
        data.uCallbackMessage = WM_APP_TRAY;
        data.hIcon = _icon;
        data.szTip = "AkioExplorer";
        _added = Shell_NotifyIcon(NIM_ADD, ref data);
        data.uVersion = 4; // NOTIFYICON_VERSION_4: NIN_SELECT / WM_CONTEXTMENU
        Shell_NotifyIcon(NIM_SETVERSION, ref data);
    }

    public void Hide()
    {
        if (!_added) return;
        var data = NewData();
        Shell_NotifyIcon(NIM_DELETE, ref data);
        _added = false;
    }

    private NOTIFYICONDATA NewData() => new()
    {
        cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = _hwnd,
        uID = 1,
        szTip = string.Empty,
        szInfo = string.Empty,
        szInfoTitle = string.Empty,
    };

    private IntPtr WindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_APP_TRAY)
        {
            int ev = (int)(lParam.ToInt64() & 0xFFFF);
            switch (ev)
            {
                case NIN_SELECT or NIN_KEYSELECT or WM_LBUTTONUP or WM_LBUTTONDBLCLK:
                    _open();
                    break;
                case WM_CONTEXTMENU or WM_RBUTTONUP:
                    ShowMenu();
                    break;
            }
            return IntPtr.Zero;
        }
        if (msg == _taskbarCreated && _added)
        {
            // Explorer restarted: the icon is gone, add it again.
            _added = false;
            Show();
        }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private void ShowMenu()
    {
        IntPtr menu = CreatePopupMenu();
        AppendMenu(menu, 0, CmdOpen, "Open AkioExplorer");
        if (_quickSearch is not null)
            AppendMenu(menu, 0, CmdQuickSearch, _quickSearchKeys?.Invoke() is { Length: > 0 } keys ? "Quick search	" + keys : "Quick search");
        AppendMenu(menu, 0x800 /* MF_SEPARATOR */, 0, null);
        AppendMenu(menu, 0, CmdExit, "Exit");
        SetMenuDefaultItem(menu, CmdOpen, false);
        GetCursorPos(out var pt);
        SetForegroundWindow(_hwnd); // required so the menu closes when clicking elsewhere
        int cmd = TrackPopupMenuEx(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON, pt.X, pt.Y, _hwnd, IntPtr.Zero);
        DestroyMenu(menu);
        if (cmd == CmdOpen) _open();
        else if (cmd == CmdExit) _exit();
        else if (cmd == CmdQuickSearch) _quickSearch?.Invoke();
    }

    public void Dispose()
    {
        Hide();
        if (_hwnd != IntPtr.Zero) DestroyWindow(_hwnd);
        _hwnd = IntPtr.Zero;
        if (_icon != IntPtr.Zero) DestroyIcon(_icon);
        _icon = IntPtr.Zero;
    }

    private delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHSTOCKICONINFO
    {
        public uint cbSize;
        public IntPtr hIcon;
        public int iSysImageIndex;
        public int iIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szPath;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateWindowEx(uint exStyle, string className, string windowName, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string message);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIcon(uint message, ref NOTIFYICONDATA data);
    [DllImport("shell32.dll")] private static extern int SHGetStockIconInfo(uint siid, uint flags, ref SHSTOCKICONINFO info);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(IntPtr menu, uint flags, int id, string? text);
    [DllImport("user32.dll")] private static extern bool SetMenuDefaultItem(IntPtr menu, int item, bool byPosition);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] private static extern int TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr hwnd, IntPtr tpm);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT pt);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
}
