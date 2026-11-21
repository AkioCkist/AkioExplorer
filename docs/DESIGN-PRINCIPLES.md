# AkioExplorer – HCI & Design Principles

Rules distilled from the sources below and how each one maps to concrete decisions in the app.
Every new feature must be checked against this list.

Sources read:
- Nielsen – *10 Usability Heuristics* (nngroup.com)
- Nielsen – *Response Times: The 3 Important Limits* (nngroup.com)
- Laws of UX (lawsofux.com) – Doherty, Fitts, Hick, Jakob, Miller, Tesler, Pareto, Postel
- Microsoft – *Keyboard accelerators* and *Content layout and spacing* (learn.microsoft.com)

## 1. Speed budget (Doherty threshold < 400 ms, Nielsen 0.1 / 1 / 10 s)

| Interaction | Budget | How |
|---|---|---|
| Keystroke / click feedback (selection, hover, filter) | < 100 ms | All UI work on dispatcher is O(visible items); filter runs in-memory |
| Folder open (local, < 10 k items) | < 100 ms perceived | Enumeration off UI thread, one `Reset` notification, virtualized lists |
| Back / Forward | instant | Listing cache (stale-while-revalidate): show cached list immediately, refresh in background |
| 0.1 – 1 s operations | no extra UI | Avoid spinners that flash (display inertia) – loading indicator appears only after 250 ms |
| 1 – 10 s | indeterminate progress | Progress bar in pane header + status-bar text |
| > 10 s (transfers) | percent-done + ETA | Transfer queue with % / speed / remaining; user can keep working |

Never block the UI thread with I/O. Every async operation takes a `CancellationToken`; navigating away cancels the previous load.

## 2. Keyboard first (Heuristic 7 – flexibility & efficiency; Jakob's law)

Follow Windows File Explorer conventions so existing muscle memory works.

| Action | Shortcut |
|---|---|
| Open item | Enter / double-click |
| Back / Forward | Alt+Left, Backspace / Alt+Right, mouse XButton1/2 |
| Up one level | Alt+Up |
| Refresh | F5 |
| Address bar edit | Ctrl+L, Alt+D |
| Search / filter | Ctrl+F, Ctrl+E |
| Rename | F2 |
| Delete (Recycle Bin) | Del |
| Delete permanently | Shift+Del (confirmation) |
| New folder | Ctrl+Shift+N |
| Select all | Ctrl+A |
| Switch active pane | F6 (Windows standard "cycle panes") |
| Toggle dual pane | Ctrl+Shift+D |
| Details / Grid view | Ctrl+Shift+6 / Ctrl+Shift+2 (Explorer view shortcuts) |
| Copy path | Ctrl+Shift+C |
| Type-ahead | Type letters in list to jump to matching item |

Accelerators are shown in tooltips (“Rename (F2)”) and menus so they are discoverable (recognition over recall).

## 3. Fitts & Hick – targets and choices

- Frequent commands (navigation, new folder, delete, view) live in the toolbar directly above the list, close to the cursor.
- Context menu shows only commands valid for the current selection (fewer choices = faster decision). Rare commands go under “More”.
- Minimum hit target 32 × 32 epx for toolbar buttons (compact density, desktop mouse), list rows 32 epx.
- Sidebar is a persistent, always-visible target on the left edge.

## 4. Visibility of system status (Heuristic 1)

- Status bar: item count, selection count + total size, loading/error state.
- Active pane has an accent indicator in dual-pane mode – the user always knows where commands apply.
- Breadcrumb always shows current location (“you are here”).

## 5. Error prevention & recovery (Heuristics 3, 5, 9)

- Delete goes to Recycle Bin by default (undoable) – no confirmation dialog for a recoverable action, confirmation only for permanent delete.
- Invalid file names are rejected inline before calling the filesystem.
- Errors are shown in plain language in an `InfoBar` with the item name and a suggestion (e.g. “Access denied – you don't have permission to open ‘System Volume Information’.”), never as raw exception codes. Esc closes dialogs; every dialog has Cancel.

## 6. Consistency & minimalism (Heuristics 4, 8)

- Native WinUI 3 controls, Mica backdrop, Segoe UI Variable, Segoe Fluent Icons, system accent colour.
- Spacing grid from Microsoft: 8 epx between buttons, 12 epx between content areas, 16 epx surface-to-edge.
- No decorative cards, gradients or dashboard tiles: content (the file list) gets the space.
- Theme follows system by default, Light/Dark overrideable.

## 7. Recognition over recall (Heuristic 6, Miller)

- Real Windows shell icons for file types (users recognise them instantly).
- Favorites and drives visible in sidebar; recent locations kept in back/forward history.
- Sort indicator arrow in the column header that is active.

## 8. Tesler / Pareto

- Complexity (retry, conflict handling, resumable uploads, OAuth refresh) is absorbed by the app, not pushed to the user.
- Optimise the 20 % of actions used 80 % of the time first: open, back, copy/move, rename, delete, search.
