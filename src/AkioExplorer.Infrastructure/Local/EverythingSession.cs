using System.Runtime.InteropServices;

namespace AkioExplorer.Infrastructure.Local;

/// <summary>One result row from Everything's index.</summary>
internal readonly record struct EverythingEntry(
    string Name, string Folder, bool IsFolder, long? Size, DateTime? ModifiedUtc, FileAttributes? Attributes)
{
    public string FullPath => Folder.EndsWith('\\') ? Folder + Name : Folder + "\\" + Name;
}

/// <summary>
/// Talks to a running voidtools Everything (1.4+) over its public IPC protocol: a WM_COPYDATA "query2" sent to the
/// EVERYTHING_TASKBAR_NOTIFICATION window, answered by a WM_COPYDATA back to our message-only window.
/// Everything keeps every NTFS file name in memory (read from the MFT, kept fresh by the USN journal), so a query over
/// the whole disk takes milliseconds — that index needs admin/a service, which is why we borrow it instead of building one.
/// A session owns a window, so create, use and dispose it on one thread.
/// </summary>
internal sealed unsafe class EverythingSession : IDisposable
{
    private const string TaskbarClass = "EVERYTHING_TASKBAR_NOTIFICATION";
    private const uint WM_COPYDATA = 0x004A;
    private const uint WM_USER = 0x0400;
    private const nint IpcIsDbLoaded = 401;
    private const nint CopyDataQuery2W = 18;
    private const nint HWND_MESSAGE = -3;
    private const uint SMTO_ABORTIFHUNG = 0x0002;
    private const uint QS_ALLINPUT = 0x04FF;
    private const uint MWMO_INPUTAVAILABLE = 0x0004;
    private const uint PM_REMOVE = 0x0001;

    internal const uint RequestName = 0x1;
    internal const uint RequestPath = 0x2;
    private const uint RequestFullPath = 0x4;
    private const uint RequestExtension = 0x8;
    internal const uint RequestSize = 0x10;
    private const uint RequestDateCreated = 0x20;
    internal const uint RequestDateModified = 0x40;
    private const uint RequestDateAccessed = 0x80;
    internal const uint RequestAttributes = 0x100;

    private const uint SortNameAscending = 1;
    private const uint ItemFolder = 0x1;
    private const uint ItemDrive = 0x2;
    private const uint ItemRoot = 0x4;

    private const uint BaseRequest = RequestName | RequestPath | RequestSize | RequestDateModified;
    private const nint IpcIsFileInfoIndexed = 411;
    private const nint FileInfoAttributes = 6;

    /// <summary>
    /// First page small so results show at once; later pages big, because Everything re-runs the search for
    /// every page (~60 ms on a full disk).
    /// </summary>
    private static readonly uint[] PageSizes = [256, 4096, 32768];
    private const int SendTimeoutMs = 3000;
    private const int ReplyTimeoutMs = 15000;

    private static readonly nint ReplyClassName = Marshal.StringToHGlobalUni("AkioExplorer.EverythingReply");
    private static readonly Lazy<bool> ClassRegistered = new(RegisterReplyClass);
    private static int s_nextReplyId = 0x41_4B_00_00;

    [ThreadStatic] private static uint t_expectedReply;
    [ThreadStatic] private static byte[]? t_reply;

    private readonly nint _everything;
    private readonly nint _window;
    private readonly uint _request;

    private EverythingSession(nint everything, nint window)
    {
        _everything = everything;
        _window = window;
        // Attributes that aren't indexed get read from disk by Everything, one file at a time (~60 µs each):
        // only ask when they're in the index, otherwise the caller reads the few it needs.
        bool attributesIndexed =
            SendMessageTimeoutW(everything, WM_USER, IpcIsFileInfoIndexed, FileInfoAttributes, SMTO_ABORTIFHUNG, 1000, out nint indexed) != 0 &&
            indexed != 0;
        _request = attributesIndexed ? BaseRequest | RequestAttributes : BaseRequest;
    }

    /// <summary>True when results carry attributes (Everything indexes them).</summary>
    public bool HasAttributes => (_request & RequestAttributes) != 0;

    /// <summary>True when Everything is running with its database loaded (cheap: one FindWindow + one message).</summary>
    public static bool IsAvailable() => FindLoadedEverything() != 0;

    /// <summary>Opens a session, or null when Everything isn't running / still loading.</summary>
    public static EverythingSession? TryOpen()
    {
        nint everything = FindLoadedEverything();
        if (everything == 0 || !ClassRegistered.Value) return null;

        nint window = CreateWindowExW(0, ReplyClassName, 0, 0, 0, 0, 0, 0, HWND_MESSAGE, 0, GetModuleHandleW(0), 0);
        return window == 0 ? null : new EverythingSession(everything, window);
    }

    private static nint FindLoadedEverything()
    {
        nint window = FindWindowW(TaskbarClass, null);
        if (window == 0) return 0;
        return SendMessageTimeoutW(window, WM_USER, IpcIsDbLoaded, 0, SMTO_ABORTIFHUNG, 1000, out nint loaded) != 0 && loaded != 0
            ? window
            : 0;
    }

    /// <summary>Number of matches for <paramref name="search"/>, or null when Everything didn't answer.</summary>
    public long? Count(string search, CancellationToken cancellationToken)
    {
        var reply = Send(search, 0, 0, RequestName, cancellationToken);
        return reply is null || reply.Length < 20 ? null : BitConverter.ToUInt32(reply, 0);
    }

    /// <summary>
    /// Every match for <paramref name="search"/>, fetched page by page. Returns false (and yields nothing) when
    /// Everything didn't answer the first page; a failure later just ends the list.
    /// </summary>
    public bool Query(string search, Func<EverythingEntry, bool> onEntry, CancellationToken cancellationToken)
    {
        uint offset = 0;
        for (int pageIndex = 0; ; pageIndex++)
        {
            uint pageSize = PageSizes[Math.Min(pageIndex, PageSizes.Length - 1)];
            var reply = Send(search, offset, pageSize, _request, cancellationToken);
            if (reply is null) return offset > 0;

            var page = Parse(reply, out uint total, out uint count);
            foreach (var entry in page)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!onEntry(entry)) return true;
            }

            offset += count;
            if (count == 0 || offset >= total) return true;
        }
    }

    // ------------------------------------------------------------------ protocol

    private byte[]? Send(string search, uint offset, uint maxResults, uint requestFlags, CancellationToken cancellationToken)
    {
        // EVERYTHING_IPC_QUERY2: 7 DWORDs, then the search text (UTF-16, null terminated).
        const int headerSize = 7 * sizeof(uint);
        int size = headerSize + (search.Length + 1) * sizeof(char);
        byte* buffer = (byte*)NativeMemory.Alloc((nuint)size);
        try
        {
            uint replyId = (uint)Interlocked.Increment(ref s_nextReplyId);
            uint* header = (uint*)buffer;
            header[0] = (uint)_window;      // reply_hwnd
            header[1] = replyId;            // reply_copydata_message
            header[2] = 0;                  // search_flags: no match case / whole word / path / regex
            header[3] = offset;
            header[4] = maxResults;
            header[5] = requestFlags;
            header[6] = SortNameAscending;
            char* text = (char*)(buffer + headerSize);
            search.AsSpan().CopyTo(new Span<char>(text, search.Length));
            text[search.Length] = '\0';

            var data = new COPYDATASTRUCT { dwData = CopyDataQuery2W, cbData = (uint)size, lpData = (nint)buffer };
            t_expectedReply = replyId;
            t_reply = null;

            // Not SMTO_BLOCK: Everything may answer while we wait, and that sent message must be able to reach us.
            if (SendMessageTimeoutW(_everything, WM_COPYDATA, _window, (nint)(&data), SMTO_ABORTIFHUNG, SendTimeoutMs, out nint accepted) == 0 ||
                accepted == 0)
                return null;

            long deadline = Environment.TickCount64 + ReplyTimeoutMs;
            while (t_reply is null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                long left = deadline - Environment.TickCount64;
                if (left <= 0) return null;

                MsgWaitForMultipleObjectsEx(0, 0, (uint)Math.Min(left, 50), QS_ALLINPUT, MWMO_INPUTAVAILABLE);
                while (PeekMessageW(out MSG msg, 0, 0, 0, PM_REMOVE))
                {
                    TranslateMessage(in msg);
                    DispatchMessageW(in msg);
                }
            }

            return t_reply;
        }
        finally
        {
            t_expectedReply = 0;
            NativeMemory.Free(buffer);
        }
    }

    /// <summary>Reads an EVERYTHING_IPC_LIST2 reply. Fields appear per item in request-flag bit order.</summary>
    internal static List<EverythingEntry> Parse(byte[] reply, out uint total, out uint count)
    {
        var entries = new List<EverythingEntry>();
        total = count = 0;
        if (reply.Length < 20) return entries;

        fixed (byte* start = reply)
        {
            byte* end = start + reply.Length;
            uint* header = (uint*)start;
            total = header[0];
            count = header[1];
            uint flags = header[3];

            uint* items = (uint*)(start + 20);
            if (20 + (long)count * 8 > reply.Length) return entries;

            for (uint i = 0; i < count; i++)
            {
                uint itemFlags = items[i * 2];
                uint dataOffset = items[i * 2 + 1];
                byte* p = start + dataOffset;
                if (p >= end) break;

                string name = string.Empty, folder = string.Empty;
                long? size = null;
                DateTime? modified = null;
                FileAttributes? attributes = null;

                if ((flags & RequestName) != 0 && !ReadString(ref p, end, out name)) break;
                if ((flags & RequestPath) != 0 && !ReadString(ref p, end, out folder)) break;
                if ((flags & RequestFullPath) != 0 && !ReadString(ref p, end, out _)) break;
                if ((flags & RequestExtension) != 0 && !ReadString(ref p, end, out _)) break;
                if ((flags & RequestSize) != 0)
                {
                    if (p + 8 > end) break;
                    long value = *(long*)p;
                    p += 8;
                    if (value >= 0) size = value;
                }
                if ((flags & RequestDateCreated) != 0) p += 8;
                if ((flags & RequestDateModified) != 0)
                {
                    if (p + 8 > end) break;
                    long fileTime = *(long*)p;
                    p += 8;
                    if (fileTime > 0 && fileTime != long.MaxValue)
                    {
                        try { modified = DateTime.FromFileTimeUtc(fileTime); } catch (ArgumentOutOfRangeException) { }
                    }
                }
                if ((flags & RequestDateAccessed) != 0) p += 8;
                if ((flags & RequestAttributes) != 0)
                {
                    if (p + 4 > end) break;
                    attributes = (FileAttributes)(*(uint*)p);
                    p += 4;
                }

                // Volume roots ("C:") come back as items too; they are not files.
                if ((itemFlags & (ItemDrive | ItemRoot)) != 0 || name.Length == 0 || folder.Length == 0) continue;
                bool isFolder = (itemFlags & ItemFolder) != 0;
                entries.Add(new EverythingEntry(name, folder, isFolder, isFolder ? null : size, modified, attributes));
            }
        }

        return entries;
    }

    private static bool ReadString(ref byte* p, byte* end, out string value)
    {
        value = string.Empty;
        if (p + 4 > end) return false;
        uint length = *(uint*)p;
        p += 4;
        // length chars + a null terminator.
        if (p + ((long)length + 1) * 2 > end) return false;
        value = new string((char*)p, 0, (int)length);
        p += ((long)length + 1) * 2;
        return true;
    }

    [UnmanagedCallersOnly]
    private static nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        if (msg == WM_COPYDATA)
        {
            var data = (COPYDATASTRUCT*)lParam;
            if (t_expectedReply != 0 && (uint)data->dwData == t_expectedReply)
            {
                t_reply = new ReadOnlySpan<byte>((void*)data->lpData, (int)data->cbData).ToArray();
                return 1;
            }
            return 0;
        }
        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private static bool RegisterReplyClass()
    {
        var wc = new WNDCLASSEXW
        {
            cbSize = (uint)sizeof(WNDCLASSEXW),
            lpfnWndProc = (nint)(delegate* unmanaged<nint, uint, nint, nint, nint>)&WndProc,
            hInstance = GetModuleHandleW(0),
            lpszClassName = ReplyClassName,
        };
        return RegisterClassExW(in wc) != 0 || Marshal.GetLastPInvokeError() == 1410; // ERROR_CLASS_ALREADY_EXISTS
    }

    public void Dispose()
    {
        DestroyWindow(_window);
        t_reply = null;
    }

    // ------------------------------------------------------------------ interop

    [StructLayout(LayoutKind.Sequential)]
    private struct COPYDATASTRUCT
    {
        public nint dwData;
        public uint cbData;
        public nint lpData;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public nint lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        public nint lpszMenuName;
        public nint lpszClassName;
        public nint hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public nint hwnd;
        public uint message;
        public nint wParam;
        public nint lParam;
        public uint time;
        public int ptX;
        public int ptY;
        public uint lPrivate;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint FindWindowW(string lpClassName, string? lpWindowName);

    [DllImport("user32.dll")]
    private static extern nint SendMessageTimeoutW(nint hWnd, uint msg, nint wParam, nint lParam, uint flags, uint timeout, out nint result);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern ushort RegisterClassExW(in WNDCLASSEXW wc);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint CreateWindowExW(uint exStyle, nint className, nint windowName, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern nint DefWindowProcW(nint hWnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool PeekMessageW(out MSG msg, nint hWnd, uint filterMin, uint filterMax, uint remove);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(in MSG msg);

    [DllImport("user32.dll")]
    private static extern nint DispatchMessageW(in MSG msg);

    [DllImport("user32.dll")]
    private static extern uint MsgWaitForMultipleObjectsEx(uint count, nint handles, uint milliseconds, uint wakeMask, uint flags);

    [DllImport("kernel32.dll")]
    private static extern nint GetModuleHandleW(nint moduleName);
}
