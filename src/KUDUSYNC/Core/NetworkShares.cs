using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace Kudusync.Core
{
    public sealed class SavedLogin
    {
        public string User { get; set; }
        public string Password { get; set; }
    }

    /// <summary>
    /// Logins for network shares, kept in Windows Credential Manager (never by KUDUSYNC itself), and connecting to
    /// shares with them so scheduled syncs work without the share being opened in File Explorer first.
    /// </summary>
    public static class NetworkShares
    {
        private static string Target(string share) => "KUDUSYNC:" + share.ToLowerInvariant();

        public static void SaveLogin(string share, string user, string password)
        {
            share = Paths.ShareRoot(share) ?? share;
            var blob = System.Text.Encoding.Unicode.GetBytes(password ?? string.Empty);
            var ptr = Marshal.AllocHGlobal(Math.Max(blob.Length, 1));
            try
            {
                Marshal.Copy(blob, 0, ptr, blob.Length);
                var credential = new Native.CREDENTIAL
                {
                    Type = Native.CRED_TYPE_GENERIC,
                    TargetName = Target(share),
                    Comment = "KUDUSYNC login for " + share,
                    CredentialBlob = ptr,
                    CredentialBlobSize = blob.Length,
                    Persist = Native.CRED_PERSIST_LOCAL_MACHINE,
                    UserName = user ?? string.Empty
                };
                if (!Native.CredWrite(ref credential, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            finally
            {
                for (var i = 0; i < blob.Length; i++) Marshal.WriteByte(ptr, i, 0);
                Marshal.FreeHGlobal(ptr);
                Array.Clear(blob, 0, blob.Length);
            }
        }

        public static SavedLogin LoadLogin(string share)
        {
            share = Paths.ShareRoot(share) ?? share;
            if (string.IsNullOrEmpty(share) || !Paths.IsWindows) return null;
            if (!Native.CredRead(Target(share), Native.CRED_TYPE_GENERIC, 0, out var ptr)) return null;
            try
            {
                var credential = Marshal.PtrToStructure<Native.CREDENTIAL>(ptr);
                var password = credential.CredentialBlobSize > 0
                    ? Marshal.PtrToStringUni(credential.CredentialBlob, credential.CredentialBlobSize / 2)
                    : string.Empty;
                return new SavedLogin { User = credential.UserName, Password = password };
            }
            finally
            {
                Native.CredFree(ptr);
            }
        }

        public static void DeleteLogin(string share)
        {
            share = Paths.ShareRoot(share) ?? share;
            if (!string.IsNullOrEmpty(share) && Paths.IsWindows) Native.CredDelete(Target(share), Native.CRED_TYPE_GENERIC, 0);
        }

        public static bool HasLogin(string share) => LoadLogin(share) != null;

        /// <summary>
        /// Makes sure the share can be opened, signing in with the saved login if needed. Tries a few times, since a
        /// share can take a moment to wake up (or Wi-Fi to connect). Returns null when it's reachable, or why not.
        /// </summary>
        public static string Connect(string path, int attempts = 3, int waitSeconds = 5, CancellationToken token = default)
        {
            var share = Paths.ShareRoot(path);
            if (share == null) return "This isn't a network path.";
            string problem = null;
            for (var attempt = 1; attempt <= Math.Max(1, attempts); attempt++)
            {
                if (Reachable(share)) return null;
                var login = LoadLogin(share);
                if (login != null)
                {
                    var code = Native.WNetAddConnection2(new Native.NETRESOURCE { lpRemoteName = share }, login.Password, login.User, 0);
                    if (code == 0 || code == 1219 /* already connected with other credentials */)
                    {
                        if (Reachable(share)) return null;
                    }
                    else if (code == 1326 || code == 86 || code == 1909 || code == 1330)
                    {
                        // Wrong user name or password, locked or expired account: trying again won't help.
                        return $"Couldn't sign in to {share}: {new Win32Exception(code).Message.TrimEnd('.', ' ')}. Edit the pair to update the login.";
                    }
                    else problem = $"Couldn't connect to {share}: {new Win32Exception(code).Message.TrimEnd('.', ' ')}.";
                }
                else problem = $"Couldn't reach {share}. Check that it's switched on and connected to the network" +
                               " (if it needs a login, edit the pair and sign in).";

                if (attempt < attempts && token.WaitHandle.WaitOne(TimeSpan.FromSeconds(waitSeconds))) break;
            }
            return problem ?? $"Couldn't reach {share}.";
        }

        /// <summary>Tests a user name and password against a share without keeping the connection.</summary>
        public static string TestLogin(string path, string user, string password)
        {
            var share = Paths.ShareRoot(path);
            if (share == null) return "Enter a network path like \\\\server\\share.";
            var code = Native.WNetAddConnection2(new Native.NETRESOURCE { lpRemoteName = share }, password, user, 0);
            if (code == 0 || code == 1219) return null;
            return new Win32Exception(code).Message;
        }

        private static bool Reachable(string share)
        {
            try { return Directory.Exists(Paths.Long(share + "\\")); }
            catch { return false; }
        }
    }
}
