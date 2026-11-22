using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace AkioExplorer.Infrastructure.Shell;

/// <summary>
/// STA thread with a Win32 message loop. Shell context-menu extensions may create windows on the thread that
/// invokes them (progress UI, hidden helper windows); without a pump those would hang.
/// </summary>
internal sealed class PumpingStaWorker : IDisposable
{
    private readonly ConcurrentQueue<Action> _queue = new();
    private readonly AutoResetEvent _signal = new(false);
    private readonly Thread _thread;
    private volatile bool _stopping;

    public PumpingStaWorker(string name)
    {
        _thread = new Thread(Run) { IsBackground = true, Name = name };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    public Task<T> InvokeAsync<T>(Func<T> work)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Enqueue(() =>
        {
            try { tcs.SetResult(work()); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        _signal.Set();
        return tcs.Task;
    }

    public Task InvokeAsync(Action work) => InvokeAsync(() => { work(); return true; });

    private void Run()
    {
        OleInitialize(IntPtr.Zero); // clipboard / drag-drop aware extensions expect OLE, not just COM
        var handles = new[] { _signal.SafeWaitHandle.DangerousGetHandle() };
        while (!_stopping)
        {
            while (_queue.TryDequeue(out var action)) action();
            MsgWaitForMultipleObjectsEx(1, handles, 0xFFFFFFFF, QS_ALLINPUT, MWMO_INPUTAVAILABLE);
            while (PeekMessage(out var msg, IntPtr.Zero, 0, 0, PM_REMOVE))
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
        }
    }

    public void Dispose()
    {
        _stopping = true;
        _signal.Set();
    }

    private const uint QS_ALLINPUT = 0x04FF;
    private const uint MWMO_INPUTAVAILABLE = 0x0004;
    private const uint PM_REMOVE = 0x0001;

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int x;
        public int y;
    }

    [DllImport("ole32.dll")]
    private static extern int OleInitialize(IntPtr pvReserved);

    [DllImport("user32.dll")]
    private static extern uint MsgWaitForMultipleObjectsEx(uint nCount, IntPtr[] pHandles, uint dwMilliseconds, uint dwWakeMask, uint dwFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG lpMsg);
}
