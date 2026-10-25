// Shared KUDU code: this file comes from kudu-common (https://github.com/azeembukhariprivate/kudu-common).
// Change it there and run its sync script; the build fails when this copy differs from kudu-common.
using System;
using System.Diagnostics;
using System.Reflection;
using System.Text.RegularExpressions;

namespace Kudusync.Core
{
    /// <summary>
    /// About the app. Its name and repository are in AppDetails.cs, which each app keeps for itself (with anything else
    /// only it needs).
    /// </summary>
    public static partial class AppInfo
    {
        public const string Author = "Azeem Bukhari";
        public const string RepoOwner = "azeembukhariprivate";

        public static string RepoUrl => $"https://github.com/{RepoOwner}/{RepoName}";
        public static string ReleasesUrl => RepoUrl + "/releases";

        public static Version Version
        {
            get
            {
                var v = Assembly.GetExecutingAssembly().GetName().Version;
                return new Version(v.Major, v.Minor, Math.Max(v.Build, 0));
            }
        }

        public static string VersionText => Version.ToString(3);

        public static string ExePath
        {
            get
            {
                try { return Process.GetCurrentProcess().MainModule.FileName; }
                catch { return Assembly.GetExecutingAssembly().Location; }
            }
        }

        /// <summary>Opens a web page in the default browser. Only http and https links are opened.</summary>
        public static void OpenUrl(string url)
        {
            if (string.IsNullOrEmpty(url) || !Regex.IsMatch(url, @"^https?://", RegexOptions.IgnoreCase)) return;
            if (Admin.IsAdmin && Unelevated.Open(url)) return;
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch { /* no browser available */ }
        }

        public static void OpenFolder(string folder, string select = null)
        {
            var args = select != null ? "/select,\"" + select + "\"" : "\"" + folder + "\"";
            // Running as administrator: File Explorer's desktop opens it, so nothing opened from there gets admin rights.
            if (Admin.IsAdmin && Unelevated.Open("explorer.exe", args)) return;
            try { Process.Start(new ProcessStartInfo("explorer.exe", args) { UseShellExecute = true }); }
            catch { /* ignore */ }
        }

        public static void OpenFile(string path)
        {
            if (Admin.IsAdmin && Unelevated.Open(path)) return;
            try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
            catch { /* no program for this file type */ }
        }
    }
}
