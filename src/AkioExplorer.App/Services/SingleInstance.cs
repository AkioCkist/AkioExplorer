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
    private static readonly string Id = "AkioExplorer-" + new string(Environment.UserName.Where(char.IsLetterOrDigit).ToArray())
                                        + "-" + System.Diagnostics.Process.GetCurrentProcess().SessionId;
    private static readonly string MutexName = @"Local\" + Id;
    private static readonly string PipeName = Id + "-activate";
    private static Mutex? _mutex;

    /// <summary>True when another instance took the request (this process should exit).</summary>
    public static bool TryHandOff(LaunchRequest request)
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out bool created);
        if (created) return false;

        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(3000);
            if (GetNamedPipeServerProcessId(client.SafePipeHandle.DangerousGetHandle(), out uint pid)) AllowSetForegroundWindow(pid);
            var bytes = Encoding.UTF8.GetBytes(request.Serialize());
            client.Write(bytes, 0, bytes.Length);
            client.Flush();
            return true;
        }
        catch (Exception)
        {
            // The other instance is starting or stuck: run normally rather than lose the request.
            return false;
        }
    }

    /// <summary>Listens for hand-offs; <paramref name="onRequest"/> runs on a background thread.</summary>
    public static void Listen(Action<LaunchRequest> onRequest)
    {
        var thread = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.CurrentUserOnly);
                    server.WaitForConnection();
                    using var reader = new StreamReader(server, Encoding.UTF8);
                    onRequest(LaunchRequest.Deserialize(reader.ReadToEnd()));
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
