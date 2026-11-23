using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;

namespace AkioExplorer.App.Services;

/// <summary>What a launch asks for: a folder or file to show, and whether to start hidden (sign-in autostart).</summary>
public sealed record LaunchRequest(string? Path, bool Background)
{
    /// <summary>
    /// Parses the command line. Paths come from Explorer verbs ("%1", "%V"), Win+E (none) or a terminal ("AkioExplorer .").
    /// <c>"C:\"</c> reaches us as <c>C:"</c> (the backslash escapes the quote), so drive paths are repaired.
    /// </summary>
    public static LaunchRequest Parse(IReadOnlyList<string> args, string currentDirectory)
    {
        bool background = false;
        string? path = null;
        foreach (var raw in args)
        {
            if (raw.Equals("--background", StringComparison.OrdinalIgnoreCase))
            {
                background = true;
                continue;
            }
            if (path is not null || raw.StartsWith("--", StringComparison.Ordinal)) continue;

            string candidate = raw.Trim().Trim('"').Trim();
            if (candidate.Length == 0) continue;
            if (candidate.Length == 2 && candidate[1] == ':') candidate += "\\";
            try
            {
                path = System.IO.Path.GetFullPath(candidate, currentDirectory);
            }
            catch (Exception)
            {
                // Not a path (e.g. "shell:::{…}"): just show the window.
            }
        }
        return new LaunchRequest(path, background);
    }

    public string Serialize() => (Background ? "1" : "0") + "\n" + (Path ?? string.Empty);

    public static LaunchRequest Deserialize(string text)
    {
        int newline = text.IndexOf('\n');
        if (newline < 0) return new LaunchRequest(null, false);
        string path = text[(newline + 1)..].Trim();
        return new LaunchRequest(path.Length == 0 ? null : path, text[0] == '1');
    }
}

/// <summary>
/// One AkioExplorer per user session. A second launch hands its request to the running instance over a named pipe
/// (and lets it take the foreground) and exits — that is what makes Win+E and double-clicked folders instant.
/// </summary>
public static class SingleInstance
{
    // Stable per user and Windows session (string.GetHashCode is randomized per process, so it can't be used here).
    internal static readonly string Id = "AkioExplorer-" + new string(Environment.UserName.Where(char.IsLetterOrDigit).ToArray())
                                         + "-" + System.Diagnostics.Process.GetCurrentProcess().SessionId;
    private static readonly string MutexName = @"Local\" + Id;
    private static readonly string PipeName = Id + "-activate";
    private static Mutex? _mutex;
    private static readonly CancellationTokenSource Stop = new();
    private static readonly object Gate = new();
    private static readonly List<LaunchRequest> Early = [];
    private static Action<LaunchRequest>? _onRequest;

    /// <summary>
    /// True when another instance took the request (this process should exit). The first instance starts listening
    /// here, before XAML loads: launches during its start-up (Win+E pressed twice, several folders opened at once)
    /// are queued instead of timing out and starting second copies of the app.
    /// </summary>
    public static bool TryHandOff(LaunchRequest request)
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out bool created);
        if (created) return BecomePrimary();

        long deadline = Environment.TickCount64 + 10_000;
        while (true)
        {
            if (Send(request, 1500)) return true;
            // The running instance quit (or crashed) meanwhile: this launch becomes the instance.
            if (TryAcquire(_mutex)) return BecomePrimary();
            // Unreachable for good (e.g. it runs elevated, so its pipe refuses us): run normally rather than lose the request.
            if (Environment.TickCount64 > deadline) return false;
            Thread.Sleep(100);
        }
    }

    private static bool BecomePrimary()
    {
        StartListening();
        return false;
    }

    private static bool TryAcquire(Mutex mutex)
    {
        try
        {
            return mutex.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            return true; // the owner died without releasing it: ours now
        }
    }

    /// <summary>Hands <paramref name="request"/> to the running instance, if there is one (used by the Win+E agent).</summary>
    public static bool TrySend(LaunchRequest request)
    {
        if (!Mutex.TryOpenExisting(MutexName, out var existing)) return false;
        existing.Dispose();
        return Send(request, 5000);
    }

    private static bool Send(LaunchRequest request, int timeout)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(timeout);
            if (GetNamedPipeServerProcessId(client.SafePipeHandle.DangerousGetHandle(), out uint pid)) AllowSetForegroundWindow(pid);
            var bytes = Encoding.UTF8.GetBytes(request.Serialize());
            client.Write(bytes, 0, bytes.Length);
            client.Flush();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Delivers hand-offs to <paramref name="onRequest"/> (on a background thread), starting with any that arrived
    /// while the app was still loading.
    /// </summary>
    public static void Listen(Action<LaunchRequest> onRequest)
    {
        LaunchRequest[] early;
        lock (Gate)
        {
            _onRequest = onRequest;
            early = [.. Early];
            Early.Clear();
        }
        foreach (var request in early) onRequest(request);
    }

    /// <summary>The app is quitting: refuse hand-offs, so a new launch waits for the mutex and starts fresh.</summary>
    public static void StopListening() => Stop.Cancel();

    private static void Deliver(LaunchRequest request)
    {
        Action<LaunchRequest>? handler;
        lock (Gate)
        {
            handler = _onRequest;
            if (handler is null)
            {
                Early.Add(request);
                return;
            }
        }
        handler(request);
    }

    private static void StartListening()
    {
        var thread = new Thread(() =>
        {
            while (!Stop.IsCancellationRequested)
            {
                try
                {
                    using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.CurrentUserOnly | PipeOptions.Asynchronous);
                    server.WaitForConnectionAsync(Stop.Token).GetAwaiter().GetResult();
                    using var reader = new StreamReader(server, Encoding.UTF8);
                    Deliver(LaunchRequest.Deserialize(reader.ReadToEnd()));
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception)
                {
                    Thread.Sleep(200);
                }
            }
        })
        { IsBackground = true, Name = "Single instance listener" };
        thread.Start();
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(IntPtr pipe, out uint serverProcessId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(uint processId);
}
