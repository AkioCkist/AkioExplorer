using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using AkioExplorer.Core.Abstractions;
using Microsoft.Extensions.Logging;
using static AkioExplorer.Infrastructure.Shell.NativeMethods;
using static AkioExplorer.Infrastructure.Shell.ShellMenuNative;

namespace AkioExplorer.Infrastructure.Shell;

/// <summary>
/// Reads the real Windows context menu (IContextMenu) so shell extensions — 7-Zip, Git, VLC, "Send to", Defender —
/// show up inside the app's own menu, plus the "Open with" app list and installed code editors.
/// All COM work runs on one pumping STA thread; a menu stays alive until the next one replaces it.
/// </summary>
public sealed class ShellMenuService : IShellMenuService, IDisposable
{
    private const uint FirstCommandId = 1;
    private const uint LastCommandId = 0x7FFF;

    /// <summary>Verbs the app already shows as its own commands.</summary>
    private static readonly HashSet<string> HiddenVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "open", "opennewwindow", "opennewprocess", "opennewtab", "explore", "find", "openas", "openaswith",
        "cut", "copy", "paste", "pastelink", "delete", "rename", "properties", "copyaspath",
        "AkioExplorer", "OpenInExplorer", "pintohome", "pintohomefile", "unpinfromhome", "undo", "redo", "refresh", "Windows.ModernShare.Pinned",
    };

    /// <summary>English captions of built-in items that carry no verb (the app has its own versions).</summary>
    private static readonly HashSet<string> HiddenCaptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "Open with", "View", "Sort by", "Group by", "Refresh", "Paste", "Paste shortcut", "Undo", "Redo",
        "Properties", "Copy as path", "Pin to Quick access", "Show more options",
    };

    private readonly PumpingStaWorker _worker = new("Shell menu worker");
    private readonly ConcurrentDictionary<string, IReadOnlyList<OpenWithApp>> _openWith = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<ShellMenuService> _logger;
    private Task<IReadOnlyList<CodeEditor>>? _editors;
    private LiveMenu? _current;

    public ShellMenuService(ILogger<ShellMenuService> logger)
    {
        _logger = logger;
    }

    /// <summary>Window that owns dialogs shown by shell extensions.</summary>
    public IntPtr OwnerWindow { get; set; }

    // ------------------------------------------------------------------ context menu

    public Task<IShellMenu?> GetItemMenuAsync(IReadOnlyList<string> paths, bool extended) =>
        paths.Count == 0 ? Task.FromResult<IShellMenu?>(null) : _worker.InvokeAsync(() => Build(() => CreateItemMenu(paths), extended, Path.GetDirectoryName(paths[0]), background: false));

    public Task<IShellMenu?> GetBackgroundMenuAsync(string folder, bool extended) =>
        _worker.InvokeAsync(() => Build(() => CreateBackgroundMenu(folder), extended, folder, background: true));

    private IShellMenu? Build(Func<IContextMenu?> create, bool extended, string? directory, bool background)
    {
        _current?.Release();
        _current = null;

        IContextMenu? menu = null;
        IntPtr hmenu = IntPtr.Zero;
        try
        {
            menu = create();
            if (menu is null) return null;

            hmenu = CreatePopupMenu();
            uint flags = CMF_NORMAL | CMF_EXPLORE | (extended ? CMF_EXTENDEDVERBS : 0);
            int hr = menu.QueryContextMenu(hmenu, 0, FirstCommandId, LastCommandId, flags);
            if (hr < 0) throw Marshal.GetExceptionForHR(hr)!;

            var entries = ReadMenu(menu, hmenu, background, depth: 0);
            var live = new LiveMenu(this, menu, hmenu, Tidy(entries), directory);
            _current = live;
            return live;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Shell context menu unavailable");
            if (hmenu != IntPtr.Zero) DestroyMenu(hmenu);
            if (menu is not null) Marshal.ReleaseComObject(menu);
            return null;
        }
    }

    private IContextMenu? CreateItemMenu(IReadOnlyList<string> paths)
    {
        var absolute = new List<IntPtr>();
        try
        {
            foreach (var path in paths)
            {
                if (SHParseDisplayName(path, IntPtr.Zero, out var pidl, 0, out _) >= 0 && pidl != IntPtr.Zero) absolute.Add(pidl);
            }
            if (absolute.Count == 0) return null;

            var iid = IID_IShellFolder;
            if (SHBindToParent(absolute[0], ref iid, out var folderPtr, out _) < 0 || folderPtr == IntPtr.Zero) return null;
            var parent = (IShellFolder)Marshal.GetObjectForIUnknown(folderPtr);
            Marshal.Release(folderPtr);
            try
            {
                // Children must share the first item's parent (always true inside one folder listing).
                var children = absolute.Select(ILFindLastID).ToArray();
                var iidMenu = IID_IContextMenu;
                int hr = parent.GetUIObjectOf(OwnerWindow, (uint)children.Length, children, ref iidMenu, IntPtr.Zero, out var menuPtr);
                if (hr < 0 || menuPtr == IntPtr.Zero) return null;
                var menu = (IContextMenu)Marshal.GetObjectForIUnknown(menuPtr);
                Marshal.Release(menuPtr);
                return menu;
            }
            finally
            {
                Marshal.ReleaseComObject(parent);
            }
        }
        finally
        {
            foreach (var pidl in absolute) CoTaskMemFree(pidl);
        }
    }

    private IContextMenu? CreateBackgroundMenu(string folder)
    {
        if (SHParseDisplayName(folder, IntPtr.Zero, out var pidl, 0, out _) < 0 || pidl == IntPtr.Zero) return null;
        try
        {
            if (SHGetDesktopFolder(out var desktop) < 0) return null;
            try
            {
                var iid = IID_IShellFolder;
                if (desktop.BindToObject(pidl, IntPtr.Zero, ref iid, out var folderPtr) < 0 || folderPtr == IntPtr.Zero) return null;
                var shellFolder = (IShellFolder)Marshal.GetObjectForIUnknown(folderPtr);
                Marshal.Release(folderPtr);
                try
                {
                    var iidMenu = IID_IContextMenu;
                    if (shellFolder.CreateViewObject(OwnerWindow, ref iidMenu, out var menuPtr) < 0 || menuPtr == IntPtr.Zero) return null;
                    var menu = (IContextMenu)Marshal.GetObjectForIUnknown(menuPtr);
                    Marshal.Release(menuPtr);
                    return menu;
                }
                finally
                {
                    Marshal.ReleaseComObject(shellFolder);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(desktop);
            }
        }
        finally
        {
            CoTaskMemFree(pidl);
        }
    }

    private List<ShellMenuEntry> ReadMenu(IContextMenu menu, IntPtr hmenu, bool background, int depth)
    {
        var result = new List<ShellMenuEntry>();
        int count = GetMenuItemCount(hmenu);
        for (int i = 0; i < count; i++)
        {
            var info = new MENUITEMINFOW
            {
                cbSize = (uint)Marshal.SizeOf<MENUITEMINFOW>(),
                fMask = MIIM_FTYPE | MIIM_STATE | MIIM_ID | MIIM_SUBMENU | MIIM_STRING | MIIM_BITMAP,
            };
            if (!GetMenuItemInfoW(hmenu, (uint)i, true, ref info)) continue;

            if ((info.fType & MFT_SEPARATOR) != 0)
            {
                result.Add(ShellMenuEntry.Separator);
                continue;
            }
            if ((info.fType & MFT_OWNERDRAW) != 0 || info.cch == 0) continue; // no readable caption

            string text = ReadCaption(hmenu, i, info.cch);
            if (string.IsNullOrWhiteSpace(text)) continue;

            int offset = (int)info.wID - (int)FirstCommandId;
            string? verb = info.hSubMenu == IntPtr.Zero && offset >= 0 ? ReadVerb(menu, offset) : null;
            if (verb is not null && HiddenVerbs.Contains(verb)) continue;
            if (HiddenCaptions.Contains(text) && !(background && text.Equals("New", StringComparison.OrdinalIgnoreCase))) continue;

            IReadOnlyList<ShellMenuEntry>? children = null;
            if (info.hSubMenu != IntPtr.Zero)
            {
                if (depth > 3) continue;
                // Dynamic submenus ("Send to", "New") fill themselves on WM_INITMENUPOPUP.
                try
                {
                    if (menu is IContextMenu3 menu3) menu3.HandleMenuMsg2(WM_INITMENUPOPUP, info.hSubMenu, (IntPtr)i, out _);
                    else if (menu is IContextMenu2 menu2) menu2.HandleMenuMsg(WM_INITMENUPOPUP, info.hSubMenu, (IntPtr)i);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Submenu init failed for {Text}", text);
                }
                children = Tidy(ReadMenu(menu, info.hSubMenu, background, depth + 1));
                if (children.Count == 0) continue;
            }

            bool enabled = (info.fState & MFS_DISABLED) == 0;
            result.Add(new ShellMenuEntry(offset, text, verb, ReadBitmap(info.hbmpItem), enabled, false, children));
        }
        return result;
    }

    private static string ReadCaption(IntPtr hmenu, int index, uint cch)
    {
        var buffer = Marshal.AllocHGlobal((int)(cch + 1) * 2);
        try
        {
            var info = new MENUITEMINFOW
            {
                cbSize = (uint)Marshal.SizeOf<MENUITEMINFOW>(),
                fMask = MIIM_STRING,
                dwTypeData = buffer,
                cch = cch + 1,
            };
            if (!GetMenuItemInfoW(hmenu, (uint)index, true, ref info)) return string.Empty;
            string raw = Marshal.PtrToStringUni(buffer) ?? string.Empty;
            int tab = raw.IndexOf('\t');
            if (tab >= 0) raw = raw[..tab];
            // "&Open" → "Open", "Tom && Jerry" → "Tom & Jerry"
            var sb = new StringBuilder(raw.Length);
            for (int i = 0; i < raw.Length; i++)
            {
                if (raw[i] == '&')
                {
                    if (i + 1 < raw.Length && raw[i + 1] == '&') { sb.Append('&'); i++; }
                    continue;
                }
                sb.Append(raw[i]);
            }
            return sb.ToString().Trim();
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static string? ReadVerb(IContextMenu menu, int offset)
    {
        var buffer = Marshal.AllocHGlobal(512);
        try
        {
            Marshal.WriteInt16(buffer, 0);
            if (menu.GetCommandString((UIntPtr)(uint)offset, GCS_VERBW, IntPtr.Zero, buffer, 256) < 0) return null;
            string verb = Marshal.PtrToStringUni(buffer) ?? string.Empty;
            return verb.Length == 0 ? null : verb;
        }
        catch
        {
            return null; // some extensions throw for ids they don't know
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static unsafe IconBitmap? ReadBitmap(IntPtr hbitmap)
    {
        long value = hbitmap.ToInt64();
        if (value == 0 || value == -1 || (value > 0 && value <= 11)) return null; // HBMMENU_* system glyphs / callback
        if (GetObject(hbitmap, Marshal.SizeOf<BITMAP>(), out var bmp) == 0 || bmp.bmWidth <= 0 || bmp.bmHeight <= 0 || bmp.bmWidth > 256) return null;

        int width = bmp.bmWidth, height = bmp.bmHeight;
        var header = new BITMAPINFOHEADER
        {
            biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = width,
            biHeight = -height,
            biPlanes = 1,
            biBitCount = 32,
        };
        var pixels = new byte[width * height * 4];
        IntPtr hdc = GetDC(IntPtr.Zero);
        try
        {
            fixed (byte* p = pixels)
            {
                if (GetDIBits(hdc, hbitmap, 0, (uint)height, p, ref header, 0) == 0) return null;
            }
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, hdc);
        }

        bool hasAlpha = false;
        for (int i = 3; i < pixels.Length; i += 4)
        {
            if (pixels[i] != 0) { hasAlpha = true; break; }
        }
        if (!hasAlpha)
        {
            for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
        }
        // Menu bitmaps with alpha are already premultiplied (AlphaBlend format), which is what WriteableBitmap wants.
        return new IconBitmap(width, height, pixels);
    }

    /// <summary>Drops leading, trailing and doubled separators.</summary>
    private static List<ShellMenuEntry> Tidy(List<ShellMenuEntry> entries)
    {
        var result = new List<ShellMenuEntry>(entries.Count);
        foreach (var entry in entries)
        {
            if (entry.IsSeparator && (result.Count == 0 || result[^1].IsSeparator)) continue;
            result.Add(entry);
        }
        while (result.Count > 0 && result[^1].IsSeparator) result.RemoveAt(result.Count - 1);
        return result;
    }

    private void Invoke(LiveMenu live, ShellMenuEntry entry)
    {
        if (live.Released) return;
        GetCursorPos(out var point);
        bool shift = (GetKeyState(0x10) & 0x8000) != 0;
        bool ctrl = (GetKeyState(0x11) & 0x8000) != 0;
        var info = new CMINVOKECOMMANDINFOEX
        {
            cbSize = (uint)Marshal.SizeOf<CMINVOKECOMMANDINFOEX>(),
            fMask = CMIC_MASK_UNICODE | CMIC_MASK_PTINVOKE | (shift ? CMIC_MASK_SHIFT_DOWN : 0) | (ctrl ? CMIC_MASK_CONTROL_DOWN : 0),
            hwnd = OwnerWindow,
            lpVerb = (IntPtr)entry.Id,
            lpVerbW = (IntPtr)entry.Id,
            lpDirectory = live.Directory,
            lpDirectoryW = live.Directory,
            nShow = SW_SHOWNORMAL,
            ptInvoke = point,
        };
        int hr = live.Menu.InvokeCommand(ref info);
        if (hr < 0 && hr != unchecked((int)0x800704C7)) // ERROR_CANCELLED
            _logger.LogInformation("Shell command {Text} failed: 0x{Hr:X8}", entry.Text, hr);
    }

    private sealed class LiveMenu(ShellMenuService owner, IContextMenu menu, IntPtr hmenu, IReadOnlyList<ShellMenuEntry> entries, string? directory) : IShellMenu
    {
        public IContextMenu Menu { get; } = menu;
        public string? Directory { get; } = directory;
        public IReadOnlyList<ShellMenuEntry> Entries { get; } = entries;
        public bool Released { get; private set; }

        public Task InvokeAsync(ShellMenuEntry entry) => owner._worker.InvokeAsync(() => owner.Invoke(this, entry));

        /// <summary>Worker thread only.</summary>
        public void Release()
        {
            if (Released) return;
            Released = true;
            DestroyMenu(hmenu);
            Marshal.ReleaseComObject(Menu);
        }

        public void Dispose() => owner._worker.InvokeAsync(() =>
        {
            Release();
            if (owner._current == this) owner._current = null;
        });
    }

    // ------------------------------------------------------------------ open with

    public Task<IReadOnlyList<OpenWithApp>> GetOpenWithAppsAsync(string path)
    {
        string ext = Path.GetExtension(path);
        if (string.IsNullOrEmpty(ext)) return Task.FromResult<IReadOnlyList<OpenWithApp>>([]);
        if (_openWith.TryGetValue(ext, out var cached)) return Task.FromResult(cached);
        return _worker.InvokeAsync(() =>
        {
            var apps = new List<OpenWithApp>();
            try
            {
                foreach (var (handler, index) in EnumerateHandlers(ext))
                {
                    try
                    {
                        if (handler.GetUIName(out var namePtr) < 0) continue;
                        string name = Marshal.PtrToStringUni(namePtr) ?? string.Empty;
                        CoTaskMemFree(namePtr);
                        if (name.Length == 0) continue;
                        var (icon, iconPath) = ReadHandlerIcon(handler);
                        apps.Add(new OpenWithApp(name, icon, iconPath, index, true));
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(handler);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Open with list failed for {Ext}", ext);
            }
            IReadOnlyList<OpenWithApp> list = apps;
            _openWith[ext] = list;
            return list;
        });
    }

    private static IEnumerable<(IAssocHandler Handler, int Index)> EnumerateHandlers(string ext)
    {
        if (SHAssocEnumHandlers(ext, ASSOC_FILTER_RECOMMENDED, out var enumerator) < 0 || enumerator is null) yield break;
        try
        {
            var buffer = new IAssocHandler[1];
            int index = 0;
            while (enumerator.Next(1, buffer, out uint fetched) == 0 && fetched == 1)
            {
                yield return (buffer[0], index++);
                if (index > 40) yield break;
            }
        }
        finally
        {
            Marshal.ReleaseComObject(enumerator);
        }
    }

    private static (IconBitmap? Icon, string? ImagePath) ReadHandlerIcon(IAssocHandler handler)
    {
        if (handler.GetIconLocation(out var locationPtr, out int index) < 0 || locationPtr == IntPtr.Zero) return (null, null);
        string location = Marshal.PtrToStringUni(locationPtr) ?? string.Empty;
        CoTaskMemFree(locationPtr);
        if (location.Length == 0) return (null, null);

        if (location.StartsWith('@'))
        {
            // Packaged app: "@{Package?ms-resource://…}" resolves to a PNG on disk.
            var sb = new StringBuilder(1024);
            if (SHLoadIndirectString(location, sb, sb.Capacity, IntPtr.Zero) >= 0 && File.Exists(sb.ToString()))
                return (null, sb.ToString());
            return (null, null);
        }
        return (ExtractIcon(Environment.ExpandEnvironmentVariables(location), index), null);
    }

    private static IconBitmap? ExtractIcon(string file, int index)
    {
        if (SHDefExtractIconW(file, index, 0, out var large, IntPtr.Zero, 32) != 0 || large == IntPtr.Zero) return null;
        try
        {
            return ShellIconExtractor.ToBitmap(large);
        }
        finally
        {
            DestroyIcon(large);
        }
    }

    public Task OpenWithAsync(OpenWithApp app, IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return Task.CompletedTask;
        string ext = Path.GetExtension(paths[0]);
        return _worker.InvokeAsync(() =>
        {
            IAssocHandler? match = null;
            foreach (var (handler, index) in EnumerateHandlers(ext))
            {
                if (match is null && index == app.Index && handler.GetUIName(out var namePtr) >= 0)
                {
                    string name = Marshal.PtrToStringUni(namePtr) ?? string.Empty;
                    CoTaskMemFree(namePtr);
                    if (name == app.Name) { match = handler; continue; }
                }
                Marshal.ReleaseComObject(handler);
            }
            if (match is null) throw new InvalidOperationException($"\"{app.Name}\" is no longer registered for {ext} files.");

            IntPtr dataObject = IntPtr.Zero;
            try
            {
                dataObject = CreateDataObject(paths);
                if (dataObject == IntPtr.Zero) throw new InvalidOperationException("Windows couldn't prepare these files for the app.");
                int hr = match.Invoke(dataObject);
                if (hr < 0) throw Marshal.GetExceptionForHR(hr)!;
            }
            finally
            {
                if (dataObject != IntPtr.Zero) Marshal.Release(dataObject);
                Marshal.ReleaseComObject(match);
            }
        });
    }

    private static IntPtr CreateDataObject(IReadOnlyList<string> paths)
    {
        var pidls = new List<IntPtr>();
        try
        {
            foreach (var path in paths)
            {
                if (SHParseDisplayName(path, IntPtr.Zero, out var pidl, 0, out _) >= 0 && pidl != IntPtr.Zero) pidls.Add(pidl);
            }
            if (pidls.Count == 0) return IntPtr.Zero;
            if (SHCreateShellItemArrayFromIDLists((uint)pidls.Count, pidls.ToArray(), out var array) < 0 || array is null) return IntPtr.Zero;
            try
            {
                var bhid = BHID_DataObject;
                var iid = IID_IDataObject;
                return array.BindToHandler(IntPtr.Zero, ref bhid, ref iid, out var data) >= 0 ? data : IntPtr.Zero;
            }
            finally
            {
                Marshal.ReleaseComObject(array);
            }
        }
        finally
        {
            foreach (var pidl in pidls) CoTaskMemFree(pidl);
        }
    }

    // ------------------------------------------------------------------ code editors

    public Task<IReadOnlyList<CodeEditor>> GetCodeEditorsAsync() =>
        _editors ??= _worker.InvokeAsync(() => (IReadOnlyList<CodeEditor>)DetectEditors());

    private List<CodeEditor> DetectEditors()
    {
        string local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs");
        string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var candidates = new (string Id, string Name, string[] Paths)[]
        {
            ("vscode", "VS Code", [Path.Combine(local, "Microsoft VS Code", "Code.exe"), Path.Combine(pf, "Microsoft VS Code", "Code.exe"), FromPath("code.cmd", @"..\Code.exe")]),
            ("vscode-insiders", "VS Code Insiders", [Path.Combine(local, "Microsoft VS Code Insiders", "Code - Insiders.exe"), Path.Combine(pf, "Microsoft VS Code Insiders", "Code - Insiders.exe")]),
            ("cursor", "Cursor", [Path.Combine(local, "cursor", "Cursor.exe")]),
            ("windsurf", "Windsurf", [Path.Combine(local, "Windsurf", "Windsurf.exe")]),
            ("antigravity", "Antigravity", [Path.Combine(local, "Antigravity", "Antigravity.exe")]),
            ("antigravity-ide", "Antigravity IDE", [Path.Combine(local, "Antigravity IDE", "Antigravity IDE.exe")]),
            ("zed", "Zed", [Path.Combine(local, "Zed", "Zed.exe")]),
            ("sublime", "Sublime Text", [Path.Combine(pf, "Sublime Text", "sublime_text.exe"), Path.Combine(pf, "Sublime Text 3", "sublime_text.exe")]),
        };

        var found = new List<CodeEditor>();
        foreach (var (id, name, paths) in candidates)
        {
            var exe = paths.FirstOrDefault(p => p.Length > 0 && File.Exists(p));
            if (exe is null) continue;
            found.Add(new CodeEditor(id, name, Path.GetFullPath(exe), ExtractIcon(exe, 0)));
        }
        return found;
    }

    private static string FromPath(string fileName, string relative)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                if (File.Exists(Path.Combine(dir, fileName))) return Path.GetFullPath(Path.Combine(dir, relative));
            }
            catch (ArgumentException)
            {
            }
        }
        return string.Empty;
    }

    public void OpenInEditor(CodeEditor editor, string path)
    {
        var start = new ProcessStartInfo(editor.ExecutablePath) { UseShellExecute = false };
        start.ArgumentList.Add(path);
        // Electron editors must not inherit this app's ELECTRON_* variables (none expected, but be safe).
        start.Environment.Remove("ELECTRON_RUN_AS_NODE");
        Process.Start(start)?.Dispose();
    }

    public void Dispose()
    {
        _worker.InvokeAsync(() => _current?.Release());
        _worker.Dispose();
    }
}
