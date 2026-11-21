using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using AkioExplorer.Core.Cloud;

namespace AkioExplorer.Infrastructure.Security;

/// <summary>
/// Secrets (OAuth refresh tokens) in Windows Credential Manager: encrypted per user by Windows, never written to
/// AkioExplorer's own files. Entries are visible in Control Panel → Credential Manager → Windows Credentials as
/// "AkioExplorer/…".
/// </summary>
public sealed class WindowsCredentialStore : ICredentialStore
{
    private const int CRED_TYPE_GENERIC = 1;
    private const int CRED_PERSIST_LOCAL_MACHINE = 2;
    private const int ERROR_NOT_FOUND = 1168;
    private const int MaxBlobBytes = 5 * 512; // CRED_MAX_CREDENTIAL_BLOB_SIZE

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public int Flags;
        public int Type;
        public string TargetName;
        public string? Comment;
        public long LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string? UserName;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CredWriteW")]
    private static extern bool CredWrite(ref CREDENTIAL credential, int flags);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CredReadW")]
    private static extern bool CredRead(string target, int type, int flags, out IntPtr credential);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CredDeleteW")]
    private static extern bool CredDelete(string target, int type, int flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);

    public void Save(string key, string secret)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(secret);
        if (bytes.Length > MaxBlobBytes)
            throw new InvalidOperationException("Secret is too large for Windows Credential Manager.");

        IntPtr blob = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var credential = new CREDENTIAL
            {
                Type = CRED_TYPE_GENERIC,
                TargetName = key,
                Comment = "AkioExplorer cloud account sign-in",
                CredentialBlobSize = bytes.Length,
                CredentialBlob = blob,
                Persist = CRED_PERSIST_LOCAL_MACHINE,
                UserName = "AkioExplorer",
            };
            if (!CredWrite(ref credential, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally
        {
            // Wipe the plaintext copy before freeing.
            Marshal.Copy(new byte[bytes.Length], 0, blob, bytes.Length);
            Marshal.FreeHGlobal(blob);
            Array.Clear(bytes);
        }
    }

    public string? Load(string key)
    {
        if (!CredRead(key, CRED_TYPE_GENERIC, 0, out IntPtr pointer))
        {
            int error = Marshal.GetLastWin32Error();
            if (error == ERROR_NOT_FOUND) return null;
            throw new Win32Exception(error);
        }

        try
        {
            var credential = Marshal.PtrToStructure<CREDENTIAL>(pointer);
            if (credential.CredentialBlobSize == 0 || credential.CredentialBlob == IntPtr.Zero) return string.Empty;
            var bytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            return Encoding.UTF8.GetString(bytes);
        }
        finally
        {
            CredFree(pointer);
        }
    }

    public void Delete(string key)
    {
        if (!CredDelete(key, CRED_TYPE_GENERIC, 0))
        {
            int error = Marshal.GetLastWin32Error();
            if (error != ERROR_NOT_FOUND) throw new Win32Exception(error);
        }
    }
}
