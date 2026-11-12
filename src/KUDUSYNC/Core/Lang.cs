// Shared KUDU code: this file comes from kudu-common (https://github.com/azeembukhariprivate/kudu-common).
// Change it there and run its sync script; the build fails when this copy differs from kudu-common.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace Kudusync.Core
{
    /// <summary>
    /// The app in other languages. The app's own code is written in English and stays that way: a text is put into the
    /// chosen language only at the moment it's shown (see Views/LangUi.cs), so nothing the app works with changes.
    /// Each language is a text file inside the app (Lang/ml.txt and so on): a line of English, a tab, and the same in
    /// that language. A line whose English has {0}, {1}... stands for texts with something filled in ("{0} files").
    /// </summary>
    public static class Lang
    {
        /// <summary>The languages on offer: the code, and the name in the language itself.</summary>
        public static readonly (string Code, string Name)[] All =
        {
            ("en", "English"), ("ml", "മലയാളം"), ("hi", "हिन्दी"), ("es", "Español"), ("fr", "Français"), ("ar", "العربية")
        };

        private sealed class Pattern
        {
            public Regex Match;
            public string To;
            public bool ValuesOnly; // little fixed text: only when what's filled in is a number, so a name isn't mistaken for it
            public int Weight;
        }

        private static Dictionary<string, string> _exact = new Dictionary<string, string>(StringComparer.Ordinal);
        private static List<Pattern> _patterns = new List<Pattern>();
        private static readonly Dictionary<string, string> _seen = new Dictionary<string, string>(StringComparer.Ordinal);
        private static readonly object _sync = new object();

        /// <summary>The language in use ("en" when none was loaded).</summary>
        public static string Current { get; private set; } = "en";

        public static bool IsEnglish => Current == "en";

        /// <summary>Arabic is written from right to left: windows are mirrored.</summary>
        public static bool RightToLeft => Current == "ar";

        public static bool Has(string code) => All.Any(l => l.Code == code);

        /// <summary>The language to use: the one chosen, else Windows' own when it's on offer, else English.</summary>
        public static string Choose(string chosen)
        {
            if (!string.IsNullOrEmpty(chosen) && Has(chosen)) return chosen;
            var windows = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
            return Has(windows) ? windows : "en";
        }

        /// <summary>Loads a language from the text files kept inside the app. English needs none.</summary>
        public static void Use(string code, Assembly app = null)
        {
            var texts = new List<string>();
            if (code != "en" && Has(code))
            {
                app = app ?? typeof(Lang).Assembly;
                var ending = ".Lang." + code + ".txt";
                foreach (var name in app.GetManifestResourceNames().Where(n => n.EndsWith(ending, StringComparison.OrdinalIgnoreCase)).OrderBy(n => n, StringComparer.Ordinal))
                    using (var stream = app.GetManifestResourceStream(name))
                    using (var reader = new StreamReader(stream, Encoding.UTF8))
                        texts.Add(reader.ReadToEnd());
            }
            Load(code, texts);
        }

        /// <summary>Sets the language from the lines of its files (also for tests).</summary>
        public static void Load(string code, IEnumerable<string> files)
        {
            var exact = new Dictionary<string, string>(StringComparer.Ordinal);
            var patterns = new List<Pattern>();
            foreach (var pair in LangCommon.For(code)) Add(pair.Key, pair.Value, exact, patterns);
            foreach (var file in files ?? Enumerable.Empty<string>())
                foreach (var raw in file.Split('\n'))
                {
                    var line = raw.TrimEnd('\r');
                    if (line.Length == 0 || line[0] == '#') continue;
                    var tab = line.IndexOf('\t');
                    if (tab <= 0 || tab == line.Length - 1) continue;
                    Add(Unescape(line.Substring(0, tab)), Unescape(line.Substring(tab + 1)), exact, patterns);
                }
            lock (_sync)
            {
                _exact = exact;
                _patterns = patterns.OrderByDescending(p => p.Weight).ToList();
                _seen.Clear();
                Current = exact.Count == 0 && patterns.Count == 0 ? "en" : code;
            }
        }

        private static void Add(string english, string other, Dictionary<string, string> exact, List<Pattern> patterns)
        {
            if (english.Length == 0 || other.Length == 0) return;
            if (english.IndexOf('{') < 0 || !Regex.IsMatch(english, @"\{\d\}"))
            {
                exact[english] = other;
                return;
            }
            exact[english] = other; // for code that fills it in itself: Lang.T("{0} files", n)
            // "{0} of {1} files" becomes a search for that text with anything in the two places.
            var fixedText = Regex.Replace(english, @"\{\d\}", string.Empty);
            var letters = fixedText.Count(char.IsLetter);
            if (letters < 2) return; // "{0} {1}" would fit everything
            var parts = Regex.Split(english, @"(\{\d\})");
            var sb = new StringBuilder("^");
            var seenHoles = new HashSet<string>();
            foreach (var part in parts)
            {
                if (Regex.IsMatch(part, @"^\{\d\}$"))
                {
                    var name = "h" + part[1];
                    sb.Append(seenHoles.Add(name) ? "(?<" + name + ">.+?)" : "\\k<" + name + ">");
                }
                else sb.Append(Regex.Escape(part));
            }
            sb.Append('$');
            var words = fixedText.Split(new[] { ' ', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries).Count(w => w.Any(char.IsLetter));
            patterns.Add(new Pattern
            {
                Match = new Regex(sb.ToString(), RegexOptions.Singleline | RegexOptions.CultureInvariant),
                To = other,
                ValuesOnly = letters < 6 || words < 2 && fixedText.Trim().Length < 9,
                Weight = fixedText.Length
            });
        }

        public static string Unescape(string s)
        {
            if (s.IndexOf('\\') < 0) return s;
            var sb = new StringBuilder(s.Length);
            for (var i = 0; i < s.Length; i++)
            {
                if (s[i] != '\\' || i + 1 == s.Length) { sb.Append(s[i]); continue; }
                var next = s[++i];
                sb.Append(next == 'n' ? '\n' : next == 't' ? '\t' : next == 'r' ? '\r' : next);
            }
            return sb.ToString();
        }

        private static bool IsValue(string s)
        {
            foreach (var c in s)
                if (!(char.IsDigit(c) || c == '.' || c == ',' || c == ':' || c == ' ' || c == '%' || c == '-' || c == '+' || c == '/' || c == ' ' || c == ' '))
                    return false;
            return s.Length > 0;
        }

        /// <summary>A text in the language in use; the text itself when there's nothing for it.</summary>
        public static string T(string english)
        {
            if (string.IsNullOrEmpty(english) || Current == "en") return english;
            Dictionary<string, string> exact;
            List<Pattern> patterns;
            lock (_sync)
            {
                exact = _exact;
                patterns = _patterns;
                if (_seen.TryGetValue(english, out var before)) return before;
            }
            var result = Find(english, exact, patterns);
            lock (_sync)
            {
                if (_seen.Count > 4000) _seen.Clear();
                _seen[english] = result;
            }
            return result;
        }

        private static string Find(string english, Dictionary<string, string> exact, List<Pattern> patterns)
        {
            if (exact.TryGetValue(english, out var direct)) return direct;
            // The same with spaces or a colon around it ("Name:" where "Name" is known).
            var trimmed = english.Trim();
            if (trimmed.Length != english.Length && exact.TryGetValue(trimmed, out direct))
                return english.Replace(trimmed, direct);
            if (english.Length > 600 || !english.Any(char.IsLetter)) return english;
            foreach (var p in patterns)
            {
                var m = p.Match.Match(english);
                if (!m.Success) continue;
                var values = new string[10];
                var fits = true;
                for (var i = 0; i < 10 && fits; i++)
                {
                    var g = m.Groups["h" + i];
                    if (!g.Success) { values[i] = string.Empty; continue; }
                    if (p.ValuesOnly && !IsValue(g.Value)) fits = false;
                    // What's filled in may be one of the app's own words ("emails" in "{0} emails saved").
                    values[i] = exact.TryGetValue(g.Value, out var inner) ? inner : g.Value;
                }
                if (!fits) continue;
                try { return string.Format(CultureInfo.CurrentCulture, p.To.Replace("{{", "{").Replace("}}", "}").Replace("{", "{{").Replace("}", "}}").Replace("{{0}}", "{0}").Replace("{{1}}", "{1}").Replace("{{2}}", "{2}").Replace("{{3}}", "{3}").Replace("{{4}}", "{4}").Replace("{{5}}", "{5}").Replace("{{6}}", "{6}").Replace("{{7}}", "{7}").Replace("{{8}}", "{8}").Replace("{{9}}", "{9}"), values); }
                catch (FormatException) { return english; }
            }
            return english;
        }

        /// <summary>A text with values filled in, for code that wants to say it directly.</summary>
        public static string T(string english, params object[] values)
        {
            var text = T(english);
            try { return string.Format(CultureInfo.CurrentCulture, text, values); }
            catch (FormatException) { return string.Format(CultureInfo.CurrentCulture, english, values); }
        }
    }
}
