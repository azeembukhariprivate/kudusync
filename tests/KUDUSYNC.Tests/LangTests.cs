using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Kudusync.Core;

namespace Kudusync.Tests
{
    /// <summary>The app in other languages: every language file is whole, and texts are looked up as they should be.</summary>
    public static class LangTests
    {
        private static string LangDir
        {
            get
            {
                var dir = AppContext.BaseDirectory;
                while (dir != null && !Directory.Exists(Path.Combine(dir, "src", "KUDUSYNC", "Lang"))) dir = Path.GetDirectoryName(dir);
                if (dir == null) throw new Exception("src/KUDUSYNC/Lang wasn't found");
                return Path.Combine(dir, "src", "KUDUSYNC", "Lang");
            }
        }

        private static void Check(bool ok, string what)
        {
            if (!ok) throw new Exception("Check failed: " + what);
        }

        private static string Holes(string s) => string.Join(",", Regex.Matches(s, @"\{\d\}").Cast<Match>().Select(m => m.Value).Distinct().OrderBy(x => x));

        public static void Run()
        {
            var counts = Lang.All.Where(l => l.Code != "en").Select(l => File.ReadAllLines(Path.Combine(LangDir, l.Code + ".txt")).Length).ToList();
            Check(counts.All(c => c == counts[0] && c > 50), "every language has every text: " + string.Join(", ", counts));
            foreach (var (code, _) in Lang.All.Where(l => l.Code != "en"))
                foreach (var line in File.ReadAllLines(Path.Combine(LangDir, code + ".txt")))
                {
                    var tab = line.IndexOf('\t');
                    Check(tab > 0 && tab < line.Length - 1 && line.IndexOf('\t', tab + 1) < 0, code + ": one English text and one translation: " + line);
                    Check(Holes(line.Substring(0, tab)) == Holes(line.Substring(tab + 1)), code + ": the same places to fill in: " + line);
                }
            try
            {
                foreach (var (code, settings) in new[] { ("ml", "ക്രമീകരണങ്ങൾ"), ("hi", "सेटिंग्स"), ("es", "Ajustes"), ("fr", "Paramètres"), ("ar", "الإعدادات") })
                {
                    Lang.Load(code, new[] { File.ReadAllText(Path.Combine(LangDir, code + ".txt")) });
                    Check(Lang.Current == code, code + " loaded");
                    Check(Lang.T("Settings") == settings, code + ": Settings is " + Lang.T("Settings"));
                    Check(Lang.T("Something nobody wrote down") == "Something nobody wrote down", code + ": unknown text stays as it is");
                    Check(Format.Count(12, "file") != "12 files" || code == "en" || !File.ReadAllText(Path.Combine(LangDir, code + ".txt")).Contains("{0} files\t"), code + ": counts in the language");
                }
                Check(Lang.RightToLeft, "Arabic is right to left");
            }
            finally
            {
                Lang.Load("en", null);
            }
            Check(Lang.T("Settings") == "Settings" && Format.Count(2, "file") == "2 files", "English unchanged");
        }
    }
}
