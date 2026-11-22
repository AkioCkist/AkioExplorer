# AkioExplorer – Feature gap plan (vs OneCommander, Google Drive)

Goal: give power users full control without hurting speed or the HCI rules in [DESIGN-PRINCIPLES.md](DESIGN-PRINCIPLES.md).
Rare commands go to the toolbar overflow (⋯), the context menu and the command palette, so the main toolbar stays short (Hick).

## What the app already has

Dual pane, preview pane, details/grid, breadcrumb + typed path, instant filter + recursive Google-Drive-style search,
copy/cut/paste/drag & drop with conflict dialog, undo, Recycle Bin, favorites, cloud accounts (Drive / Dropbox / OneDrive),
transfer queue with segmented downloads, thumbnails, settings.

## Gaps

| Area | OneCommander / Total Commander | Google Drive | AkioExplorer before |
|---|---|---|---|
| Tabs per pane | yes | – | no |
| Command palette / every command searchable | yes | – | no |
| Batch rename (mask, counter, find/replace, regex, case) | yes | – | no |
| Selection tools (none, invert, by pattern) | yes | – | Ctrl+A only |
| New file (text / empty) | yes | "New" menu | folders only |
| ZIP compress / extract | yes | download as ZIP | no |
| Folder sizes | yes (auto) | storage view | no |
| Checksums (MD5 / SHA) | plugin | – | no |
| Compare panes (select differences) | yes | – | no |
| Duplicate, copy name, open terminal here | yes | "Make a copy" | no |
| File attributes (hidden, read-only) | yes | – | no |
| Show / hide extensions, hidden items toggle in view | yes | – | settings only |
| Extra columns (created, extension, attributes), column chooser | yes | yes | no |
| Color tags / labels | yes | – (colors on folders) | no |
| Miller columns view | yes | – | no |
| Workspaces (saved tab sets) | yes | – | no |
| Custom keyboard shortcuts | yes | – | no |
| History dropdown on Back/Forward | yes | – | no |
| Folder sync / mirror | TC | desktop app | no |
| Share link, permissions | – | yes | no |
| Trash view + restore | – | yes | delete only |
| Starred, Recent, Shared with me views | – | yes | search filter only |
| File versions | – | yes | no |
| Storage usage per account | – | yes | settings only |

## Phases

### Phase 5a – Power tools (this batch, all local-testable)

1. **Tabs** in each pane: Ctrl+T new, Ctrl+W close, Ctrl+Tab / Ctrl+Shift+Tab switch, Ctrl+Shift+T reopen closed,
   middle-click a folder or "Open in new tab"; drag to reorder; each tab keeps its own back/forward; restored on start.
   Tab strip shows once a pane has two tabs (minimalism).
2. **Command palette** (Ctrl+Shift+P): every command with its shortcut, fuzzy filter, plus "Go to" favorites / drives.
3. **Batch rename** (Ctrl+M, or F2 with several items): name mask `[N]` `[E]` `[C]` `[P]` `[YMD]`, counter start/step/digits,
   find & replace (plain or regex, match case), case change, live preview with conflicts flagged, one Ctrl+Z undoes all.
4. **Selection tools**: Select none (Ctrl+Shift+A), Invert (Ctrl+I), Select by pattern (Numpad +, e.g. `*.jpg;*.png`),
   Select same type.
5. **New file** (Ctrl+Alt+N): "New Text Document.txt", inline rename.
6. **ZIP**: Compress to ZIP, Extract here, Extract to folder (local).
7. **Folder sizes**: calculate on demand (Ctrl+Alt+S) or automatically (View › Folder sizes); sort by size includes folders.
8. **Checksums**: MD5 / SHA-1 / SHA-256 / SHA-512 of a file, copy, compare with a pasted hash.
9. **Compare panes**: selects items that are missing or different (size / date) in the other pane.
10. **Misc**: Duplicate, Copy name, Open terminal here (Windows Terminal, else PowerShell), Hidden / Read-only toggles,
    View › Hidden items (Ctrl+H) and File name extensions.

### Phase 5b – View & customisation

Extra columns + column chooser, color tags, history dropdown on Back/Forward, workspaces, Miller columns view,
custom shortcuts (settings.json first, UI later).

### Phase 5c – Google Drive parity (needs live accounts to verify)

Get share link, Trash view + restore, Starred / Recent / Shared with me views, versions, storage usage panel.
Each needs provider API work (Drive v3, Dropbox, Graph) and is only shown for services that support it.

### MEGA parity (2026-11-22)

Done through `MegaRawApi` (MEGA's JSON API with the stored session; MegaApiClient keeps login/upload/listing):
Rubbish bin browser (restore to the original folder via the `rr` attribute, delete, empty), thumbnails and previews
(shown, and generated + uploaded with every picture/video upload), public links (copy / turn off; also Google Drive),
favourite / colour label / hide (`fav`, `lbl`, `sen`; star also on Google Drive), versions (list, restore with `ov`,
delete), server-side copy inside an account, parallel downloads (4 × 4 MB ranges, AES-CTR per part, MAC checked),
parallel uploads, Recent files and Photos & videos views (search presets), open/import links other people shared,
MEGA S4 preset, transfer quota on paid plans.
Not done: sharing folders with contacts and incoming shares (RSA share keys), file requests, Device centre (→ Phase 5d), chat.

### Google Drive parity (2026-11-22)

"Shared with me" and "Shared drives" as folders at the top of My Drive (create / rename / delete shared drives),
Google Docs open in the browser (also "Open in browser" for anything), New ▸ Docs / Sheets / Slides / Forms / Drawings,
Download as ▸ every export format, "Make a Google copy" (convert Office files), Share dialog (people + roles,
ownership transfer, general access), comments (reply, resolve), activity (Drive Activity API: needs the new
`drive.activity.readonly` scope, existing accounts reconnect once), versions (download, keep forever, delete,
restore = re-upload), folder colours (labels), shortcuts, description, lock editing, Starred and Largest files views.
Not possible through Google's public APIs: approvals, Workspace labels, spam, request access, Computers.

### Phase 5d – Sync

Compare & sync two folders (local ↔ cloud), preview of the plan, one-way mirror and two-way.
