# AkioExplorer

Native Windows multi-cloud file manager (WinUI 3, .NET 9, MVVM). Inspired by Air Explorer, built keyboard-first and fast.

- Design rules: [docs/DESIGN-PRINCIPLES.md](docs/DESIGN-PRINCIPLES.md)
- Architecture & roadmap: [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)

## Status – Phase 1 (Foundation) done

Working today (local disks only):

- Windows 11 shell: Mica, custom title bar, sidebar (Home / Favorites / Local Computer / drives), Light/Dark/System theme
- Browsing: breadcrumb + typed address bar with folder autocomplete (Ctrl+L), back/forward/up history, mouse back/forward buttons
- Details and Grid views, real shell icons, natural sort by name/date/type/size, folders first
- Instant filter while typing (Ctrl+F); Enter runs a recursive search with streamed results
- Dual pane (Ctrl+Shift+D), F6 to switch pane, "Open in other pane"
- New folder + inline rename, F2 rename, Del → Recycle Bin, Shift+Del permanent delete (confirmation, Cancel default)
- Type-ahead selection, context menus, copy path, pin to Favorites, Properties, Show in File Explorer
- Live refresh via FileSystemWatcher, listing cache for instant back/forward, adaptive columns for narrow panes
- Settings persisted to `%LocalAppData%\AkioExplorer\settings.json`; logs in `%LocalAppData%\AkioExplorer\logs`

## Status – Phase 2 (File management) done

- Copy / Cut / Paste (Ctrl+C / X / V), interoperable with File Explorer both ways; cut items appear dimmed
- Copy to / Move to: other pane or any folder (picker); context menu "Copy/Move to other pane"
- Drag & drop between panes, onto folder rows, from and to File Explorer (Explorer rules: same drive = move, Ctrl = copy, Shift = move), with a "Move to X" caption and drop highlight
- Copy engine: Windows copy engine (CopyFileEx) with progress, unbuffered I/O for big files, instant same-drive moves, retries for locked files, bounded concurrency, cancel; a move only removes the source when everything inside it arrived
- Conflict dialog (Replace / Keep both / Skip, compare size & date, apply to all), default behavior configurable in Settings
- Progress panel: percent, speed, time left; operations under ~0.6 s never flash on screen
- Undo (Ctrl+Z) for rename, new folder, copy and move (moves restore the original names)

Not implemented yet (no UI is shown for these): cloud providers, transfer queue page/history — see roadmap.

## Requirements

- Windows 10 1809+ / Windows 11
- .NET 9 SDK (the app is self-contained: Windows App SDK 2.4 is bundled, no runtime install needed)

## Build, run, test

```powershell
dotnet build src/AkioExplorer.App -p:Platform=x64
```

```powershell
.\src\AkioExplorer.App\bin\x64\Debug\net9.0-windows10.0.19041.0\win-x64\AkioExplorer.exe
```

```powershell
dotnet test tests/AkioExplorer.Tests
```

Set `AKIO_DEBUG_LOG=1` to write debug-level logs.

## Keyboard shortcuts

| Action | Keys |
|---|---|
| Open | Enter / double-click |
| Back / Forward / Up | Alt+Left (Backspace) / Alt+Right / Alt+Up |
| Refresh | F5, Ctrl+R |
| Type a path | Ctrl+L, Alt+D, F4 |
| Filter / search | Ctrl+F, Ctrl+E, F3 (Enter = search subfolders, Esc = clear) |
| New folder | Ctrl+Shift+N |
| Rename | F2 |
| Copy / Cut / Paste | Ctrl+C / Ctrl+X / Ctrl+V |
| Undo | Ctrl+Z |
| Delete / permanently | Del / Shift+Del |
| Details / Grid | Ctrl+Shift+6 / Ctrl+Shift+2 |
| Dual pane / switch pane | Ctrl+Shift+D / F6 |
| Copy path | Ctrl+Shift+C |
| Properties | Alt+Enter |
| Focus sidebar | Ctrl+1 |
| Jump to item | type its first letters |
