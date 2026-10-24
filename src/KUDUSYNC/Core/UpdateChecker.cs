using System;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Kudusync.Core
{
    public sealed class UpdateInfo
    {
        public Version Latest { get; set; }
        public bool IsNewer { get; set; }
        public string Url { get; set; }
    }

    /// <summary>Asks GitHub for the latest published release. Nothing is downloaded automatically.</summary>
    public static class UpdateChecker
    {
        public static async Task<UpdateInfo> CheckAsync()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;

            using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) })
            {
                http.DefaultRequestHeaders.UserAgent.ParseAdd("KUDUSYNC/" + AppInfo.VersionText);
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

        private static string Pad(string v) => v.Contains(".") ? v : v + ".0";
    }
}
