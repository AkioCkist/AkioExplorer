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

## Status – Phase 3 (Cloud integration) done

- Google Drive (official Drive v3 SDK), Dropbox (official SDK), OneDrive personal + work/school (Microsoft Graph REST)
- Multiple accounts per service; add, rename, disconnect from the sidebar (right-click) or Settings, with storage usage
- Sign-in in your own browser with OAuth 2.0 + PKCE (loopback redirect); refresh tokens in Windows Credential Manager
- Browse, paged listing of large folders, breadcrumbs, create folder, rename, delete (to the service's trash), search
- Expired or revoked sign-ins show a **Reconnect** button and a warning badge on the account
- **Needs OAuth client ids** – embed them in the build (`cloud-apps.builtin.json`, one-click sign-in for users) or enter one per service in the "Add cloud account" dialog; see [docs/CLOUD-SETUP.md](docs/CLOUD-SETUP.md).
- New accounts are named service + e-mail, e.g. `Google Drive you@gmail.com` (rename any time)

### Google Drive – Google Cloud Console checklist

Full steps and troubleshooting: [docs/CLOUD-SETUP.md#google-drive](docs/CLOUD-SETUP.md#google-drive).

1. [console.cloud.google.com](https://console.cloud.google.com/) → select/create the project (top picker).
2. **APIs & Services → Library → Google Drive API → Enable**.
3. **Google Auth Platform → Branding**: app name + support/developer e-mail (skip the logo).
4. **Audience**: External; while *Testing*, add every account that signs in under **Test users** – otherwise Google shows `Error 403: access_denied`.
5. **Clients → Create client → Desktop app** (no redirect URI needed) → copy Client ID + Client secret.
6. Put them in `src/AkioExplorer.CloudProviders/cloud-apps.builtin.json` (git-ignored) and rebuild, or paste them in "Add cloud account":

   ```json
   { "GoogleDrive": { "ClientId": "….apps.googleusercontent.com", "ClientSecret": "GOCSPX-…" } }
   ```

7. In *Testing*, Google expires the `drive` refresh token after 7 days (app shows **Reconnect**). **Audience → Publish app** removes that (unverified apps: ≤ 100 users, consent screen shows a warning – fine for personal use).

## Status – Phase 4 (Transfer engine) – implemented, partly verified

- Upload / Download toolbar buttons, drag & drop and paste between This PC and clouds, cloud → cloud (streaming) and same-account server-side copy/move
- Resumable/chunked uploads (Drive resumable, Dropbox sessions, Graph upload sessions) with per-chunk retries
- Speed: cloud → PC downloads use up to 8 connections per file with dynamic segmentation (IDM-style engine, `Core/Transfers/Segmented`); partial data sits in `<name>.akiodl` + `.akiodl.json` and Retry resumes it. Uploads use adaptive chunk sizes (bigger chunks for big files = fewer idle round trips), cloud → cloud reads ahead while uploading, and cloud transfers run 4 files at once (`DownloadConnections`, `ParallelFileTransfers` in settings.json)
- Preview pane (Alt+P, resizable): images, text/code, PDF pages, audio/video player, shell thumbnails, OneCommander-style details (sizes, age, attributes, media/photo/document properties); cloud files use the service's rendition. Grid view shows real thumbnails (local thumbnail cache, Drive/OneDrive/Dropbox renditions)
- Transfer queue page (pause / resume / cancel), Recent transfers (persisted, retry skips files that already arrived; jobs running at exit are kept as Interrupted)
- Double-click a cloud file: downloaded to a private cache and opened (read-only working copy)
- Settings: download folder
- Debug builds only: AKIO_DEV_CLOUD=<folder> adds a folder-backed fake cloud for testing transfer UI without OAuth

## Status – Phase 5a (Power tools) – implemented, UI not yet hand-tested

Plan and gap analysis vs OneCommander / Google Drive: [docs/FEATURE-PLAN.md](docs/FEATURE-PLAN.md).

- Tabs per pane (Ctrl+T / Ctrl+W / Ctrl+Tab / Ctrl+Shift+T, middle-click a folder, drag to reorder, restored on start)
- Command palette (Ctrl+Shift+P): every command with its shortcut, plus "Go to" favorites, folders, drives, accounts
- Batch rename (Ctrl+M, or F2 with several items): masks `[N] [N2-5] [E] [C] [P] [YMD] [hms]`, counter, find/replace (regex), case, live preview, one undo
- Selection: none, invert, by pattern (Num + / Num -), same type; compare panes (select differences)
- New text document, duplicate, copy name, Open in Terminal, Hidden / Read-only attributes
- ZIP compress / extract, checksums (MD5, SHA-1, SHA-256, SHA-512), folder sizes (on demand or automatic)
- View › Hidden items (Ctrl+H), File name extensions, Folder sizes
- Faster local copy (8 small files at a time, big files one at a time, no per-file lookups in new folders): ~2.7 s → ~1.0 s for 3,000 small files + 256 MB in the benchmark (`$env:AKIO_BENCH=1; dotnet test --filter CopyBenchmark`); deleted rows disappear instantly, permanent delete of big trees in parallel

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
| Command palette | Ctrl+Shift+P |
| New tab / close / reopen closed | Ctrl+T / Ctrl+W / Ctrl+Shift+T |
| Next / previous tab | Ctrl+Tab / Ctrl+Shift+Tab |
| Batch rename | Ctrl+M (F2 with several items) |
| Select none / invert | Ctrl+Shift+A / Ctrl+I (Num *) |
| Select / deselect by pattern | Num + / Num - |
| New text document | Ctrl+Alt+N |
| Folder sizes | Ctrl+Alt+S |
| Open in Terminal | Ctrl+Alt+T |
| Show hidden items | Ctrl+H |

