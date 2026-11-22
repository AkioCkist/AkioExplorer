# Cloud setup

Supported services, by how they sign in:

| Sign-in | Services |
|---|---|
| Browser (OAuth, needs an app registration below) | Google Drive, Dropbox, OneDrive, OneDrive for Business, SharePoint Online, Box |
| Form in the "Add cloud account" dialog (nothing to register) | MEGA, pCloud, Yandex Disk, Nextcloud, ownCloud, WebDAV, FTP/FTPS, SFTP, Amazon S3, S3-compatible (MinIO, R2, Spaces…), Wasabi, Backblaze B2, Azure Blob Storage |
| Local folder | iCloud Drive – Apple has no file API; install iCloud for Windows and `%USERPROFILE%\iCloudDrive` appears in the sidebar |

Form-based services are tested with the typed details before anything is saved. Passwords, secret keys and
sessions go to Windows Credential Manager; server, user name, bucket etc. go to `settings.json`. On reconnect the
form comes back filled in; leaving a password empty keeps the saved one. MEGA keeps only a session (never the
password), SFTP pins the server's host key on first connect and refuses a changed key until you reconnect.

AkioExplorer talks to each service with **your own app registration** (a free "client id"). Desktop apps are
*public clients*: the client id is not a secret, sign-in uses OAuth 2.0 with PKCE in your normal browser, and
AkioExplorer never sees your password.

Sign-in always happens in the **default Windows browser** (OAuth 2.0 authorization code + PKCE, loopback redirect).
Where the client id comes from, later wins:

1. **Built into the build** – `src/AkioExplorer.CloudProviders/cloud-apps.builtin.json` (git-ignored, embedded at
   compile time). Register the apps once as the developer, put the ids here, rebuild: users then just click a
   service in "Add cloud account" and the browser opens – no setup step.
2. **Per user** – `%LocalAppData%\AkioExplorer\cloud-apps.json`. When the build has no id for a service, the
   "Add cloud account" dialog asks for it once (with a link to the developer console), saves it here and signs in.
3. **Environment variables** (table below).

All three use the same format:

```jsonc
{
  "GoogleDrive": { "ClientId": "xxxx.apps.googleusercontent.com", "ClientSecret": "GOCSPX-…" },
  "Dropbox":     { "ClientId": "your-app-key" },
  "OneDrive":    { "ClientId": "00000000-0000-0000-0000-000000000000" },
  "Box":         { "ClientId": "…", "ClientSecret": "…" }
}
```

| Service | Environment variables |
|---|---|
| Google Drive | `AKIO_GOOGLE_CLIENT_ID`, `AKIO_GOOGLE_CLIENT_SECRET` |
| Dropbox | `AKIO_DROPBOX_APP_KEY` |
| OneDrive, OneDrive for Business, SharePoint | `AKIO_ONEDRIVE_CLIENT_ID` |
| Box | `AKIO_BOX_CLIENT_ID`, `AKIO_BOX_CLIENT_SECRET` |

## Google Drive

All of this is in [Google Cloud Console](https://console.cloud.google.com/) → **Google Auth Platform** (left
menu: Overview, Branding, Audience, Clients, Data Access). Pick the right project in the project picker at the top
first – every step below applies to that project.

1. **Project** – create one (e.g. `AkioExplorer`) or select an existing one.
2. **Enable the API** – [APIs & Services → Library → Google Drive API](https://console.cloud.google.com/apis/library/drive.googleapis.com)
   → **Enable**. Without it sign-in works but every listing fails with `accessNotConfigured`.
3. **Branding** – [Google Auth Platform → Branding](https://console.cloud.google.com/auth/branding): app name
   (shown on the consent screen), user support e-mail, developer contact e-mail. Logo/domains are optional (a logo
   triggers brand verification – leave it empty for personal use).
4. **Audience** – [Google Auth Platform → Audience](https://console.cloud.google.com/auth/audience):
   - User type **External** (any Google account) – *Internal* only exists for Workspace organisations.
   - Publishing status starts as **Testing** → under **Test users** click **+ Add users** and add **every Google
     account that will sign in** (up to 100). Anyone else gets *Error 403: access_denied*.
5. **Data Access** – [Google Auth Platform → Data Access](https://console.cloud.google.com/auth/scopes) → **Add or
   remove scopes**, add `.../auth/drive`, `openid`, `.../auth/userinfo.email` → **Update** → **Save**. (Optional
   while testing – the app requests them anyway – but needed before publishing.)
6. **Client** – [Google Auth Platform → Clients](https://console.cloud.google.com/auth/clients) → **+ Create client**
   → Application type **Desktop app** → name it → **Create**. No redirect URI to enter: desktop clients accept
   `http://127.0.0.1:<any port>/`.
7. Copy the **Client ID** (`….apps.googleusercontent.com`) and **Client secret** (`GOCSPX-…`) into `GoogleDrive`
   (built-in json, the "Add cloud account" form, or the environment variables). The secret is shown in full only
   once – download the JSON if you need it later.

Testing vs. In production (Audience → **Publish app**)

| | Testing | In production, unverified |
|---|---|---|
| Who can sign in | Listed test users only (≤ 100) | Any Google account (≤ 100 users until verified) |
| Consent screen | "Google hasn't verified this app" → **Continue** | Same warning |
| Refresh token lifetime | **7 days** for the restricted `drive` scope – then **Reconnect** | No forced expiry |

For personal use, publishing without verification is fine. Distributing to the public requires Google's
verification of the restricted `drive` scope (a security assessment) – not needed for a handful of users.

Troubleshooting

| What you see | Cause | Fix |
|---|---|---|
| `Error 403: access_denied` on Google's page | Account isn't a test user while the app is in Testing | Audience → Test users → add the account (or publish the app) |
| `Error 400: redirect_uri_mismatch` | Client type isn't **Desktop app** (e.g. "Web application") | Create a new Desktop app client and use its id |
| `Error 401: invalid_client` / `deleted_client` | Wrong/old id, or id and secret from different clients | Copy both again from the same client in Clients |
| Sign-in OK, listing says API not enabled / `accessNotConfigured` | Drive API not enabled in **this** project | Step 2 – make sure the project picker shows the client's project |
| Asked to reconnect every week | App is in Testing (7-day refresh tokens) | Publish the app |
| `admin_policy_enforced` | Workspace admin blocks third-party apps | Ask the admin to trust the client id |

Notes
- Google requires the secret for desktop clients even though it isn't confidential (Google documents this); PKCE
  is what actually protects the code exchange.
- Scopes requested: `https://www.googleapis.com/auth/drive` (full Drive access, needed for a file manager),
  `openid`, `email` (to name the account "Google Drive you@gmail.com").
- Redirect: `http://127.0.0.1:<random port>/` – nothing to configure for desktop clients.
- Removing access: <https://myaccount.google.com/connections> or **Disconnect** in AkioExplorer (revokes the token).

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

**OneDrive for Business** and **SharePoint Online** use this same registration. SharePoint additionally asks for
`Sites.Read.All` (add it under API permissions) to turn the site address into a document library.

## Box

1. <https://app.box.com/developers/console> → **Create Platform App** → **User Authentication (OAuth 2.0)**.
2. Configuration → Redirect URI: `http://localhost:53683/` (exact, fixed port).
3. Application scopes: **Read and write all files and folders stored in Box**.
4. Copy **Client ID** and **Client Secret** into `Box.ClientId` / `Box.ClientSecret`. Box requires the secret on
   token requests; for a desktop app it is not really confidential, treat it like Google's.

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
- Box: REST v2.0, id-based with a path→id cache; uploads over 50 MB use chunked upload sessions (SHA-1 per part).
- WebDAV (also Nextcloud, ownCloud, pCloud, Yandex): PROPFIND parsed as a stream, Basic auth sent up front,
  Digest/NTLM servers switch to challenge auth, Range downloads for the segmented downloader.
- S3 family: hand-written SigV4 (checked against the AWS documentation vectors), multipart uploads with 4 parts in
  flight, pre-signed URLs for segmented downloads, AWS buckets in other regions followed automatically.
- Azure Blob: Shared Key auth, parallel block uploads, SAS URLs for segmented downloads.
- FTP/FTPS: FluentFTP with a pool of 4 control connections. SFTP: SSH.NET, pipelined transfers.
- MEGA: MegaApiClient (end-to-end encryption on this PC); the node tree is cached for a few seconds.
- All HTTP calls share one pooled connection handler with retries for 429/5xx honouring `Retry-After`.
