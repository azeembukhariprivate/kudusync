// Shared KUDU code: this file comes from kudu-common (https://github.com/azeembukhariprivate/kudu-common).
// Change it there and run its sync script; the build fails when this copy differs from kudu-common.
// Checks GitHub for a newer version of the app (only its version number and page are read; nothing about the person is
// sent).
using System;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Kudusync.Core
{
    public sealed class UpdateInfo
    {
        /// <summary>The newest version on GitHub.</summary>
        public Version Latest { get; set; }
        /// <summary>True when that version is newer than the one running.</summary>
        public bool IsNewer { get; set; }
        /// <summary>The page of that release, to open in the browser.</summary>
        public string Url { get; set; }
    }

    /// <summary>Asks GitHub for the latest published release. Nothing is downloaded automatically.</summary>
    public static class UpdateChecker
    {
        /// <summary>
        /// Asks GitHub for the latest release. Returns null when it couldn't be reached (no internet is not an error).
        /// </summary>
        public static async Task<UpdateInfo> CheckAsync()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;

            using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) })
            {
                http.DefaultRequestHeaders.UserAgent.ParseAdd(AppInfo.Name + "/" + AppInfo.VersionText);
                http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

                var url = $"https://api.github.com/repos/{AppInfo.RepoOwner}/{AppInfo.RepoName}/releases/latest";
                var json = await http.GetStringAsync(url).ConfigureAwait(false);

                var match = Regex.Match(json, "\"tag_name\"\\s*:\\s*\"[vV]?(?<v>\\d+(\\.\\d+){0,3})");
                if (!match.Success || !Version.TryParse(Pad(match.Groups["v"].Value), out var parsed)) return null;

                var latest = new Version(parsed.Major, parsed.Minor, Math.Max(parsed.Build, 0));
                return new UpdateInfo
                {
                    Latest = latest,
                    IsNewer = latest > AppInfo.Version,
                    Url = AppInfo.ReleasesUrl + "/latest"
                };
            }
        }

        /// <summary>Makes a version like "2" into "2.0", which .NET can read.</summary>
        private static string Pad(string v) => v.Contains(".") ? v : v + ".0";
    }
}
