using System;
using System.IO;
using System.Linq;
using System.Text;

namespace Kudusync.Core
{
    /// <summary>
    /// Path helpers. Paths inside a pair are relative and always use "\" (so the same record works on any machine);
    /// <see cref="Full"/> turns them into real paths. On Windows real paths get the "\\?\" prefix, so folders deeper
    /// than the old 260-character limit work too.
    /// </summary>
    public static class Paths
    {
        public static readonly bool IsWindows = Environment.OSVersion.Platform == PlatformID.Win32NT;

        public static readonly StringComparer Comparer = StringComparer.OrdinalIgnoreCase;
        public const StringComparison Comparison = StringComparison.OrdinalIgnoreCase;

        /// <summary>The folder at the top of each side that holds replaced and deleted files.</summary>
        public const string HistoryName = "_KUDUSYNC History";
        public const string TempSuffix = ".kudusync-tmp";

        public static bool IsUnc(string path) => !string.IsNullOrEmpty(path) && (path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal)) && !path.StartsWith(@"\\?\", StringComparison.Ordinal);

        /// <summary>"\\server\share" for "\\server\share\folder\file", or null.</summary>
        public static string ShareRoot(string path)
        {
            if (!IsUnc(path)) return null;
            var parts = path.Replace('/', '\\').TrimStart('\\').Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);
            return parts.Length >= 2 ? @"\\" + parts[0] + @"\" + parts[1] : null;
        }

        /// <summary>"E:" for "E:\Backup", or null for network paths.</summary>
        public static string DriveOf(string path)
        {
            if (string.IsNullOrEmpty(path) || path.Length < 2 || path[1] != ':' || !char.IsLetter(path[0])) return null;
            return char.ToUpperInvariant(path[0]) + ":";
        }

        /// <summary>A tidy absolute folder path without a trailing separator (except for "E:\").</summary>
        public static string Normalize(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return string.Empty;
            path = path.Trim().Trim('"');
            if (IsWindows) path = path.Replace('/', '\\');
            if (IsUnc(path)) return @"\\" + path.TrimStart('\\').TrimEnd('\\');
            // "E:" alone would mean the current folder on E:, but always means the whole drive here
            // (a drive's right-click menu passes "E:\", which arrives as E:" after Windows' quoting rules).
            if (path.Length == 2 && path[1] == ':') path += "\\";
            try { path = Path.GetFullPath(path); }
            catch { /* keep what was typed; it's checked later */ }
            if (path.Length > 3 || (!IsWindows && path.Length > 1)) path = path.TrimEnd('\\', '/');
            if (path.Length == 2 && path[1] == ':') path += "\\";
            return path;
        }

        /// <summary>True when the two folders are the same, or one is inside the other.</summary>
        public static bool Overlap(string a, string b)
        {
            a = WithSlash(Normalize(a));
            b = WithSlash(Normalize(b));
            return a.StartsWith(b, Comparison) || b.StartsWith(a, Comparison);
        }

        /// <summary>True when <paramref name="inner"/> is <paramref name="outer"/> or inside it.</summary>
        public static bool IsInside(string inner, string outer) =>
            WithSlash(Normalize(inner)).StartsWith(WithSlash(Normalize(outer)), Comparison);

        /// <summary>The relative ("\"-separated) path of <paramref name="full"/> inside <paramref name="root"/>, or null if it's not inside.</summary>
        public static string Relative(string root, string full)
        {
            var r = WithSlash(Normalize(root));
            var f = Normalize(full);
            if (!WithSlash(f).StartsWith(r, Comparison)) return null;
            return f.Length <= r.Length ? string.Empty : f.Substring(r.Length).Replace('/', '\\');
        }

        private static string WithSlash(string path) => path.EndsWith("\\") || path.EndsWith("/") ? path : path + (IsWindows || path.Contains('\\') ? "\\" : "/");

        /// <summary>The real path of a relative path inside a root, ready for System.IO (long-path safe on Windows).</summary>
        public static string Full(string root, string relative)
        {
            var path = string.IsNullOrEmpty(relative)
                ? root
                : Path.Combine(root, IsWindows ? relative : relative.Replace('\\', '/'));
            return Long(path);
        }

        /// <summary>Adds "\\?\" (or "\\?\UNC\") on Windows so paths of any length work.</summary>
        public static string Long(string path)
        {
            if (!IsWindows || string.IsNullOrEmpty(path) || path.StartsWith(@"\\?\", StringComparison.Ordinal)) return path;
            if (IsUnc(path)) return @"\\?\UNC\" + path.TrimStart('\\');
            if (path.Length >= 2 && path[1] == ':') return @"\\?\" + path;
            return path;
        }

        /// <summary>Removes the "\\?\" prefix for showing a path to people.</summary>
        public static string Display(string path)
        {
            if (string.IsNullOrEmpty(path)) return string.Empty;
            if (path.StartsWith(@"\\?\UNC\", StringComparison.Ordinal)) return @"\\" + path.Substring(8);
            if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) return path.Substring(4);
            return path;
        }

        public static string Parent(string relative)
        {
            var i = relative.LastIndexOf('\\');
            return i < 0 ? string.Empty : relative.Substring(0, i);
        }

        public static string Name(string relative)
        {
            var i = relative.LastIndexOf('\\');
            return i < 0 ? relative : relative.Substring(i + 1);
        }

        public static string Join(string parent, string name) => string.IsNullOrEmpty(parent) ? name : parent + "\\" + name;

        public static int Depth(string relative) => relative.Count(c => c == '\\');

        /// <summary>Makes text safe as a file or folder name.</summary>
        public static string SafeName(string text, string fallback = "Pair")
        {
            if (string.IsNullOrWhiteSpace(text)) return fallback;
            var invalid = Path.GetInvalidFileNameChars().Concat(new[] { '\\', '/', ':', '*', '?', '"', '<', '>', '|' }).ToArray();
            var sb = new StringBuilder();
            foreach (var c in text.Trim()) sb.Append(invalid.Contains(c) || c < 32 ? '_' : c);
            var s = sb.ToString().TrimEnd('.', ' ');
            if (s.Length > 80) s = s.Substring(0, 80).TrimEnd('.', ' ');
            return s.Length == 0 ? fallback : s;
        }
    }
}
