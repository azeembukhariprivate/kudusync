using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Kudusync.Core
{
    /// <summary>Command-line quoting that matches how Windows programs (including robocopy.exe) split arguments.</summary>
    public static class Args
    {
        public static string Join(IEnumerable<string> args) => string.Join(" ", args.Select(Quote));

        public static string Quote(string arg)
        {
            if (string.IsNullOrEmpty(arg)) return "\"\"";
            if (arg.IndexOfAny(new[] { ' ', '\t', '\n', '\v', '"' }) < 0) return arg;

            var sb = new StringBuilder("\"");
            var backslashes = 0;
            foreach (var c in arg)
            {
                if (c == '\\')
                {
                    backslashes++;
                    continue;
                }
                if (c == '"')
                {
                    sb.Append('\\', backslashes * 2 + 1);
                    sb.Append('"');
                }
                else
                {
                    sb.Append('\\', backslashes);
                    sb.Append(c);
                }
                backslashes = 0;
            }
            sb.Append('\\', backslashes * 2);
            sb.Append('"');
            return sb.ToString();
        }

        /// <summary>Splits text typed by the user ("/XF *.tmp "my file.txt"") into arguments.</summary>
        public static List<string> Split(string text)
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(text)) return result;
            var current = new StringBuilder();
            var quoted = false;
            var has = false;
            foreach (var c in text)
            {
                if (c == '"')
                {
                    quoted = !quoted;
                    has = true;
                    continue;
                }
                if (!quoted && char.IsWhiteSpace(c))
                {
                    if (has) result.Add(current.ToString());
                    current.Clear();
                    has = false;
                    continue;
                }
                current.Append(c);
                has = true;
            }
            if (has) result.Add(current.ToString());
            return result;
        }
    }

    public static class Format
    {
        public static string Size(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double size = bytes < 0 ? 0 : bytes;
            var unit = 0;
            while (size >= 1024 && unit < units.Length - 1)
            {
                size /= 1024;
                unit++;
            }
            if (unit == 0) return bytes + " B";
            return (size >= 100 ? size.ToString("0") : size.ToString("0.#")) + " " + units[unit];
        }

        public static string Speed(double bytesPerSecond) => bytesPerSecond <= 0 ? "–" : Size((long)bytesPerSecond) + "/s";

        public static string Duration(System.TimeSpan t)
        {
            if (t.TotalSeconds < 1) return "less than a second";
            if (t.TotalHours >= 1) return $"{(int)t.TotalHours} h {t.Minutes} min";
            if (t.TotalMinutes >= 1) return $"{(int)t.TotalMinutes} min {t.Seconds} s";
            return $"{(int)t.TotalSeconds} s";
        }

        public static string Count(int n, string one, string many = null) =>
            n.ToString("N0") + " " + (n == 1 ? one : many ?? one + "s");
    }
}
