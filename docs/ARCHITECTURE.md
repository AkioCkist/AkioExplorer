# AkioExplorer – Architecture

Native Windows multi-cloud file manager. C# / .NET 9, WinUI 3 (Windows App SDK 2.4, unpackaged, self-contained), MVVM (CommunityToolkit.Mvvm), Microsoft.Extensions.DependencyInjection + Logging.

## Solution layout

```
AkioExplorer.sln
src/
  AkioExplorer.Core/            net9.0            Domain: models, interfaces, pure logic (no Windows, no UI)
    Models/                     StorageItem, StorageLocation, BreadcrumbSegment, SortOptions, AppSettings
    Abstractions/               IStorageProvider, IStorageProviderRegistry, ISettingsService, IListingCache
    Logic/                      NaturalStringComparer, StorageItemComparer, FileNameValidator, ListingCache
  AkioExplorer.Infrastructure/  net9.0-windows    Windows implementations
    Local/                      LocalStorageProvider (filesystem, drives, Recycle Bin via shell)
    Shell/                      ShellIconExtractor (system icons → BGRA pixels), KnownFolders, ShellActions
    Settings/                   JsonSettingsService (%LocalAppData%\AkioExplorer\settings.json)
    Logging/                    FileLoggerProvider (%LocalAppData%\AkioExplorer\logs)
  AkioExplorer.App/             WinUI 3 app
    Views/                      MainWindow, ExplorerView, SettingsPage
    Controls/                   FilePane (reusable pane: address bar + list + status)
    ViewModels/                 MainViewModel, FilePaneViewModel, FileItemViewModel, SettingsViewModel
    Services/                   IconService (cached ImageSource per file type), DialogService
    Converters/, Resources/
  AkioExplorer.CloudProviders/  net9.0          One folder per service + shared OAuth/HTTP
    Auth/                       OAuthClient (PKCE + loopback), OAuthTokenSource (refresh, rotation)
    Http/                       Shared pooled HttpClient + RetryHandler (429/5xx, Retry-After)
    GoogleDrive/ Dropbox/ OneDrive/   <Service>Connector (sign-in, factory) + <Service>Provider (IStorageProvider)
tests/
  AkioExplorer.Tests/           xUnit – Core logic + LocalStorageProvider against temp folders
```

Dependency direction: `App → Infrastructure → Core`, `CloudProviders → Core`. ViewModels never call a provider API directly except through `IStorageProvider`; no provider-specific code in UI.

## Storage abstraction

Local disk and every cloud account are the same thing to the UI: an `IStorageProvider` instance addressed by a `StorageLocation(ProviderId, Path)`.
This is what makes dual-pane, drag & drop and cloud-to-cloud transfers uniform.

```csharp
interface IStorageProvider {
  string Id; string DisplayName; StorageCapabilities Capabilities;
  string RootPath;
  IAsyncEnumerable<IReadOnlyList<StorageItem>> ListAsync(string path, CancellationToken ct); // pages
  Task<StorageItem?> GetItemAsync(string path, CancellationToken ct);
  Task<StorageItem> CreateFolderAsync(string parentPath, string name, CancellationToken ct);
  Task<StorageItem> RenameAsync(string path, string newName, CancellationToken ct);
  Task DeleteAsync(IReadOnlyList<string> paths, bool permanent, CancellationToken ct);
  IAsyncEnumerable<StorageItem> SearchAsync(string path, string query, CancellationToken ct);
  IReadOnlyList<BreadcrumbSegment> GetBreadcrumb(string path);
  string? GetParentPath(string path);
  // Phase 4: OpenReadAsync / OpenWriteAsync (streams), CopyAsync/MoveAsync (server side)
}
```

Cloud services plug in through two interfaces in `Core/Cloud`:

- `ICloudConnector` – one per service: `AuthenticateAsync` (interactive OAuth), `CreateProvider(account)`, `SignOutAsync`.
- `ICloudStorageProvider : IStorageProvider` – browsing/file operations for one account, plus quota.

`CloudAccountService` keeps account metadata in settings, secrets in `ICredentialStore` (Windows Credential Manager)
and registers one provider per account in the registry, so panes, search, drag & drop and the copy engine treat every
account like a drive. Adding a service = one connector + one provider class; nothing in the UI changes.

## File operations engine (Phase 2, reused by Phase 4)

`Core/Transfers/FileOperationService` is provider-agnostic: it plans (recursive listing through `IStorageProvider`),
resolves name conflicts (`IConflictResolver` → dialog, or the Settings default), and delegates byte movement to an
`ITransferExecutor` chosen per provider pair. Phase 2 ships `LocalTransferExecutor` (CopyFileEx + same-volume rename);
Phase 4 adds cloud executors (server-side copy when both ends are the same service, streaming otherwise) without
touching the engine or UI. Progress is polled by the UI every 250 ms – no per-chunk events cross threads.

## Performance design

- **Paged listing** – `ListAsync` yields pages; the pane shows the first page immediately (lazy loading for large cloud folders).
- **Listing cache** – LRU of the last ~64 folders per provider. Back/forward renders the cached listing instantly and revalidates in the background; the diff is applied in place so scroll position and selection survive.
- **Live updates** – `FileSystemWatcher` on the current local folder, debounced 250 ms, applied as an in-place diff.
- **Icons** – one shell lookup per extension (`SHGetFileInfo` + `SHGFI_USEFILEATTRIBUTES`, no disk access), done on a background thread and shared by every row; per-file lookup only for `.exe/.ico/.lnk/.url`.
- **Virtualised lists** – `ListView`/`GridView` with `ItemsStackPanel`/`ItemsWrapGrid`, x:Bind (compiled bindings), lightweight row templates.
- **Cancellation** – every navigation cancels the previous load/search.
- **Startup** – no I/O before first frame other than settings; ReadyToRun on release publish.

## Roadmap

| Phase | Scope | Status |
|---|---|---|
| 1 Foundation | Solution, DI, logging, settings, shell window (Mica, custom title bar), sidebar, toolbar, breadcrumb, local browsing, details/grid view, sorting, filter, dual-pane shell, keyboard model | **done** (also: inline rename, Recycle Bin delete, recursive search) |
| 2 File management | Copy/move/clipboard, drag & drop (in-app + Explorer), conflict dialog, undo, progress panel | **done** |
| 3 Cloud integration | Provider SDKs, OAuth 2.0 (loopback + PKCE), Credential Manager token storage, Google Drive / Dropbox / OneDrive, account management | **done** |
| 4 Transfer engine | Queue, bounded concurrency, progress/speed, pause/resume/cancel/retry with backoff, streaming cloud-to-cloud, server-side copy | planned |
| 5 Advanced | MEGA, Box, pCloud, WebDAV, SFTP, FTP, conflict dialog, history persistence, notifications | planned |
| 6 Release | Polish, profiling, MSIX packaging | planned |

## Build & run

```powershell
dotnet build src/AkioExplorer.App -c Debug -p:Platform=x64
dotnet run --project src/AkioExplorer.App -p:Platform=x64
dotnet test tests/AkioExplorer.Tests
```
