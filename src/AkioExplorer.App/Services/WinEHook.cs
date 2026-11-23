using System.Runtime.InteropServices;

namespace AkioExplorer.App.Services;

/// <summary>
/// Takes Win+E from File Explorer while AkioExplorer runs. Recent Windows 11 builds open File Explorer straight
/// from Explorer's own hotkey and ignore the "File Explorer" opennewwindow command override, and Explorer holds
/// the key so RegisterHotKey fails; a low-level keyboard hook (as PowerToys uses) is the only way to get it.
/// Runs inside <see cref="WinEAgent"/>; the installing thread must pump messages (the callback runs there).
/// </summary>
public sealed class WinEHook : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;
    private const uint VK_E = 0x45;
    private const uint VK_LWIN = 0x5B;
    private const uint VK_RWIN = 0x5C;
    private const int VK_SHIFT = 0x10;
    private const int VK_CONTROL = 0x11;
    private const int VK_MENU = 0x12;
    private const ushort VK_DUMMY = 0xFF; // unassigned: tells the shell "Win was used" so releasing it doesn't open Start
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private static readonly UIntPtr OwnInput = new(0x414B4945); // "AKIE", marks the dummy key we inject

    private readonly HookProc _proc; // kept alive: native code calls it
    private IntPtr _hook;
    private bool _lwin, _rwin;
    private bool _swallowingE;

    public WinEHook() => _proc = Callback;

    /// <summary>Raised inside the hook callback when Win+E is pressed.</summary>
    public event EventHandler? Pressed;

    /// <summary>Asked on each Win+E; false lets File Explorer have the key (e.g. another app is now the default).</summary>
    public Func<bool>? ShouldTake { get; set; }

    /// <summary>Installs or removes the hook (on the calling thread).</summary>
    public bool Enabled
    {
        get => _hook != IntPtr.Zero;
        set
        {
            if (value == Enabled) return;
            if (value)
            {
                _lwin = _rwin = _swallowingE = false;
                _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
            }
            else
            {
                UnhookWindowsHookEx(_hook);
                _hook = IntPtr.Zero;
            }
        }
    }

    private IntPtr Callback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            int msg = wParam.ToInt32();
            bool down = msg is WM_KEYDOWN or WM_SYSKEYDOWN;
            bool up = msg is WM_KEYUP or WM_SYSKEYUP;

            if (info.vkCode == VK_LWIN) _lwin = down || (!up && _lwin);
            else if (info.vkCode == VK_RWIN) _rwin = down || (!up && _rwin);
            else if (info.vkCode == VK_E && info.dwExtraInfo != OwnInput)
            {
                if (down && (_lwin || _rwin) && !Held(VK_SHIFT) && !Held(VK_CONTROL) && !Held(VK_MENU)
                    && (_swallowingE || ShouldTake?.Invoke() != false))
                {
                    if (!_swallowingE) // first press only, not auto-repeat
                    {
                        _swallowingE = true;
                        SendDummyKey();
                        Pressed?.Invoke(this, EventArgs.Empty); // handlers must return at once (Windows drops hooks that stall)
                    }
                    return 1; // File Explorer never sees it
                }
                if (_swallowingE)
                {
                    if (up) _swallowingE = false;
                    return 1;
                }
            }
        }
        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    private static bool Held(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    /// <summary>
    /// A key between Win down and Win up keeps the Start menu shut, and input sent by this process lets the
    /// window that opens next take the foreground.
    /// </summary>
    private static void SendDummyKey()
    {
        var inputs = new INPUT[2];
        inputs[0].type = 1; // INPUT_KEYBOARD
        inputs[0].ki = new KEYBDINPUT { wVk = VK_DUMMY, dwExtraInfo = OwnInput };
        inputs[1].type = 1;
        inputs[1].ki = new KEYBDINPUT { wVk = VK_DUMMY, dwFlags = KEYEVENTF_KEYUP, dwExtraInfo = OwnInput };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    public void Dispose() => Enabled = false;

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    // INPUT is a union; MOUSEINPUT is its largest member, so pad KEYBDINPUT up to that size.
    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public KEYBDINPUT ki;
        private readonly long _pad;
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int idHook, HookProc proc, IntPtr hMod, uint threadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, INPUT[] inputs, int size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
}
