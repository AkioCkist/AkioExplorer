# Cloud setup (Google Drive, Dropbox, OneDrive)

AkioExplorer talks to each service with **your own app registration** (a free "client id"). Desktop apps are
*public clients*: the client id is not a secret, sign-in uses OAuth 2.0 with PKCE in your normal browser, and
AkioExplorer never sees your password.

Put the ids in `%LocalAppData%\AkioExplorer\cloud-apps.json` (the "Add cloud account" dialog can create and open
this file), or in environment variables. Save the file and choose the service again — no restart needed.

```jsonc
{
  "GoogleDrive": { "ClientId": "xxxx.apps.googleusercontent.com", "ClientSecret": "GOCSPX-…" },
  "Dropbox":     { "ClientId": "your-app-key" },
  "OneDrive":    { "ClientId": "00000000-0000-0000-0000-000000000000" }
}
```

| Service | Environment variables |
|---|---|
| Google Drive | `AKIO_GOOGLE_CLIENT_ID`, `AKIO_GOOGLE_CLIENT_SECRET` |
| Dropbox | `AKIO_DROPBOX_APP_KEY` |
| OneDrive | `AKIO_ONEDRIVE_CLIENT_ID` |

## Google Drive

1. <https://console.cloud.google.com/> → create a project.
2. **APIs & Services → Library** → enable **Google Drive API**.
3. **OAuth consent screen** → External (or Internal for Workspace) → add your Google account under *Test users*.
4. **Credentials → Create credentials → OAuth client ID → Application type: Desktop app**.
5. Copy the **Client ID** and **Client secret** into `GoogleDrive`.

Notes
- Google requires the secret for desktop clients even though it isn't confidential (Google documents this).
- Scope used: `https://www.googleapis.com/auth/drive` (full Drive access, needed for a file manager). It is a
  *restricted* scope: while the consent screen is in "Testing", only listed test users can sign in and tokens expire
  after 7 days (the app then shows **Reconnect**). Publishing to many users requires Google's verification.
- Redirect: `http://127.0.0.1:<random port>/` – nothing to configure for desktop clients.

## Dropbox

1. <https://www.dropbox.com/developers/apps> → **Create app** → *Scoped access* → *Full Dropbox*.
2. **Permissions** tab: enable `account_info.read`, `files.metadata.read`, `files.metadata.write`,
   `files.content.read`, `files.content.write` → **Submit**.
3. **Settings** tab → **OAuth 2 → Redirect URIs** → add exactly `http://localhost:53682/`.
4. Copy the **App key** into `Dropbox.ClientId` (the app secret is *not* needed – PKCE is used).

Notes: in "Development" status up to 500 users can connect. Dropbox keeps deleted files restorable; permanent
delete is only available for Dropbox Business, otherwise AkioExplorer deletes normally.

## OneDrive (personal and work/school)

1. <https://entra.microsoft.com/> → **Identity → Applications → App registrations → New registration**.
2. Supported account types: **Accounts in any organizational directory and personal Microsoft accounts**.
3. Redirect URI: platform **Public client/native (mobile & desktop)** → `http://localhost`.
4. **Authentication** → *Allow public client flows* → **Yes**.
5. **API permissions** → Microsoft Graph → Delegated: `Files.ReadWrite.All`, `User.Read`, `offline_access`.
6. Copy the **Application (client) ID** into `OneDrive.ClientId`.

Work/school tenants may require an administrator to consent to `Files.ReadWrite.All`.

## Where credentials are stored

| What | Where |
|---|---|
| Account list (name, e-mail, service) | `%LocalAppData%\AkioExplorer\settings.json` |
| Refresh tokens | **Windows Credential Manager** → Windows Credentials → `AkioExplorer/<account id>` (encrypted by Windows) |
| Access tokens | Memory only, refreshed automatically before they expire |

**Disconnect** removes the account, revokes the token where the service supports it (Google, Dropbox) and
deletes the Credential Manager entry.

## Implementation notes

- Google Drive: official `Google.Apis.Drive.v3` SDK. Drive is id-based; AkioExplorer caches names/parents learned
  while browsing for breadcrumbs. Drive search covers the whole drive (the API can't limit a search to a folder).
- Dropbox: official `Dropbox.Api` SDK (it refreshes access tokens itself).
- OneDrive: Microsoft Graph v1.0 **REST API** called directly with `HttpClient` (the official API). The Graph SDK
  was not used because it adds ~40 MB of assemblies and its own HTTP pipeline for a handful of endpoints.
- All HTTP calls share one pooled connection handler with retries for 429/5xx honouring `Retry-After`.
