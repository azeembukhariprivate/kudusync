using System;
using System.Diagnostics;
using System.Reflection;
using System.Text.RegularExpressions;

namespace Kudusync.Core
{
    public static class AppInfo
    {
        public const string Name = "KUDUSYNC";
        public const string Author = "Azeem Bukhari";
        public const string RepoOwner = "azeembukhariprivate";
        public const string RepoName = "kudusync";

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
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch { /* no browser available */ }
        }

        public static void OpenFolder(string folder, string select = null)
        {
            try
            {
                var args = select != null ? "/select,\"" + select + "\"" : "\"" + folder + "\"";
                Process.Start(new ProcessStartInfo("explorer.exe", args) { UseShellExecute = true });
            }
            catch { /* ignore */ }
        }

        public static void OpenFile(string path)
        {
            try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
            catch { /* no program for this file type */ }
        }
    }
}
