using System.Runtime.InteropServices;
using AkioExplorer.Core.Models;

namespace AkioExplorer.App.Services;

/// <summary>
/// The quick-search hotkey (RegisterHotKey on a message-only window owned by the UI thread). Works while every
/// window is hidden, as long as the process runs. Settings can suspend it while the user records a new key.
/// </summary>
public sealed class GlobalHotkey : IDisposable
{
    private const uint WM_HOTKEY = 0x0312;
    private const uint MOD_NOREPEAT = 0x4000;
    private const uint WM_TIMER = 0x0113;
    private const int HotkeyId = 0x4151; // "AQ"
    private const int RetryTimerId = 1;
    private const uint RetryMs = 3000;
    private static readonly IntPtr HWND_MESSAGE = new(-3);

    private readonly WndProc _proc; // kept alive: native code calls it
    private readonly string _className = "AkioExplorerHotkey" + Environment.ProcessId;
    private IntPtr _hwnd;
    private HotkeyGesture? _registered;
    private HotkeyGesture? _wanted;
    private bool _suspended;
    private bool _paused;

    public GlobalHotkey()
    {
        _proc = WindowProc;
        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
            hInstance = GetModuleHandle(null),
            lpszClassName = _className,
        };
        RegisterClassEx(ref wc);
        _hwnd = CreateWindowEx(0, _className, "AkioExplorer hotkey", 0, 0, 0, 0, 0, HWND_MESSAGE, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
    }

    /// <summary>Raised on the UI thread when the hotkey is pressed anywhere in Windows.</summary>
    public event EventHandler? Pressed;

    /// <summary>Raised when <see cref="Status"/> changes (registration worked / failed).</summary>
    public event EventHandler? StatusChanged;

    /// <summary>The hotkey in effect, or null when off or taken by another app.</summary>
    public HotkeyGesture? Active => _registered;

    /// <summary>Why the wanted hotkey isn't working, or empty when it is (shown under the setting).</summary>
    public string Status { get; private set; } = string.Empty;

    /// <summary>True when <see cref="Status"/> asks the user to act (key taken), false for a chosen state (paused).</summary>
    public bool StatusNeedsAction => Status.Length > 0 && !_paused;

    /// <summary>Registers <paramref name="gesture"/> (null turns the hotkey off). Returns false when another app owns it.</summary>
    public bool Set(HotkeyGesture? gesture)
    {
        _wanted = gesture;
        return Apply();
    }

    /// <summary>Lets the key reach the app (the settings key recorder) until <see cref="Resume"/>.</summary>
    public void Suspend()
    {
        _suspended = true;
        Unregister();
    }

    public void Resume()
    {
        _suspended = false;
        Apply();
    }

    /// <summary>
    /// Tray › Pause hotkey ("game mode"): hands the key back to other apps until unpaused or AkioExplorer restarts.
    /// </summary>
    public bool Paused
    {
        get => _paused;
        set
        {
            if (_paused == value) return;
            _paused = value;
            Apply();
        }
    }

    private bool Apply()
    {
        Unregister();
        KillTimer(_hwnd, RetryTimerId);
        if (_suspended || _paused || _wanted is not { IsValid: true } wanted || _hwnd == IntPtr.Zero)
        {
            SetStatus(_paused && _wanted is not null ? "Paused from the notification-area icon (Pause hotkey)" : string.Empty);
            return _wanted is null || _suspended || _paused;
        }

        if (TryRegister(wanted)) return true;

        // Another app holds the key. Keep trying, so quitting that app (Flow Launcher, PowerToys Run…) frees it for
        // us without restarting AkioExplorer.
        SetStatus($"{wanted} is already used by another app (a launcher such as Flow Launcher or PowerToys Run?). " +
                  "Close it or pick another key; AkioExplorer takes the key as soon as it is free.");
        SetTimer(_hwnd, RetryTimerId, RetryMs, IntPtr.Zero);
        return false;
    }

    private bool TryRegister(HotkeyGesture wanted)
    {
        if (!RegisterHotKey(_hwnd, HotkeyId, (uint)wanted.Modifiers | MOD_NOREPEAT, (uint)wanted.Key)) return false;
        _registered = wanted;
        KillTimer(_hwnd, RetryTimerId);
        SetStatus(string.Empty);
        return true;
    }

    private void Unregister()
    {
        if (_registered is null) return;
        UnregisterHotKey(_hwnd, HotkeyId);
        _registered = null;
    }

    private void SetStatus(string status)
    {
        if (status == Status) return;
        Status = status;
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private IntPtr WindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            Pressed?.Invoke(this, EventArgs.Empty);
            return IntPtr.Zero;
        }
        if (msg == WM_TIMER && wParam.ToInt32() == RetryTimerId)
        {
            if (_registered is null && !_suspended && !_paused && _wanted is { IsValid: true } wanted) TryRegister(wanted);
            else KillTimer(_hwnd, RetryTimerId);
            return IntPtr.Zero;
        }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        Unregister();
        KillTimer(_hwnd, RetryTimerId);
        if (_hwnd != IntPtr.Zero) DestroyWindow(_hwnd);
        _hwnd = IntPtr.Zero;
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

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateWindowEx(uint exStyle, string className, string windowName, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    [DllImport("user32.dll")] private static extern IntPtr SetTimer(IntPtr hwnd, IntPtr id, uint ms, IntPtr callback);
    [DllImport("user32.dll")] private static extern bool KillTimer(IntPtr hwnd, IntPtr id);
}
