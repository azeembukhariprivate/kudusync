using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Kudusync.Core
{
    /// <summary>A folder pair found in SyncToy's settings.</summary>
    public sealed class SyncToyPair
    {
        public string Name { get; set; }
        public string Left { get; set; }
        public string Right { get; set; }
        public string ModeName { get; set; }
        public string Include { get; set; }
        public string Exclude { get; set; }
        public int ExcludedAttributes { get; set; }
        public List<string> ExcludedFolders { get; set; } = new List<string>();
        public bool CheckContents { get; set; }
        public bool Active { get; set; } = true;
        public bool UsedRecycleBin { get; set; } = true;

        public SyncMode Mode
        {
            get
            {
                var m = (ModeName ?? string.Empty).ToLowerInvariant();
                if (m.Contains("echo")) return SyncMode.Mirror;
                if (m.Contains("contribute")) return SyncMode.Update;
                return SyncMode.TwoWay;
            }
        }

        public string SyncToyModeName
        {
            get
            {
                switch (Mode)
                {
                    case SyncMode.Mirror: return "Echo";
                    case SyncMode.Update: return "Contribute";
                    default: return "Synchronize";
                }
            }
        }
    }

    /// <summary>
    /// Reads the folder pairs Microsoft SyncToy 2.x saved in %LOCALAPPDATA%\Microsoft\SyncToy\2.0\SyncToyDirPairs.bin.
    /// The file's layout isn't documented, so this is best-effort: it looks for SyncToy's settings objects by their
    /// field names, without loading any SyncToy code.
    /// </summary>
    public static class SyncToyImport
    {
        public static string DefaultFile =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "SyncToy", "2.0", "SyncToyDirPairs.bin");

        public static bool Available => File.Exists(DefaultFile);

        public static List<SyncToyPair> Read(string file)
        {
            using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                return Read(stream);
        }

        public static List<SyncToyPair> Read(Stream stream)
        {
            var found = new List<SyncToyPair>();
            foreach (var root in NrbfReader.ReadAll(stream))
                foreach (var obj in Objects(root, new HashSet<object>()))
                {
                    var pair = FromObject(obj);
                    if (pair != null) found.Add(pair);
                }
            return found;
        }

        /// <summary>Every object in a graph (the file may hold one settings object per pair, or a list of them).</summary>
        private static IEnumerable<NrbfObject> Objects(object value, HashSet<object> seen)
        {
            if (value == null || value is string || !seen.Add(value)) yield break;
            if (value is NrbfObject obj)
            {
                yield return obj;
                foreach (var member in obj.Members.Values)
                    foreach (var inner in Objects(member, seen)) yield return inner;
            }
            else if (value is object[] array)
            {
                foreach (var item in array)
                    foreach (var inner in Objects(item, seen)) yield return inner;
            }
        }

        private static SyncToyPair FromObject(NrbfObject obj)
        {
            var left = obj.Get("leftDir") as string;
            var right = obj.Get("rightDir") as string;
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return null;

            var pair = new SyncToyPair
            {
                Name = obj.Get("name") as string ?? Path.GetFileName(left.TrimEnd('\\')),
                Left = left,
                Right = right,
                ModeName = obj.Get("syncModeName") as string ?? EnumText(obj.Get("syncMode")),
                Include = obj.Get("searchPattern") as string,
                Exclude = obj.Get("excludedPattern") as string,
                ExcludedAttributes = EnumValue(obj.Get("excludedFileAttributes")),
                CheckContents = obj.Get("computeHash") as bool? ?? false,
                Active = obj.Get("active") as bool? ?? true,
                UsedRecycleBin = obj.Get("backupOlderFile") as bool? ?? true
            };

            if (obj.Get("exceptionalDirs") is NrbfObject dirs)
            {
                var leftDirs = dirs.Members.FirstOrDefault(m => NrbfObject.Plain(m.Key).StartsWith("left", StringComparison.OrdinalIgnoreCase)).Value;
                pair.ExcludedFolders.AddRange(Strings(leftDirs, new HashSet<object>()));
            }
            return pair;
        }

        private static IEnumerable<string> Strings(object value, HashSet<object> seen)
        {
            if (value == null) yield break;
            if (value is string s)
            {
                yield return s;
                yield break;
            }
            if (!seen.Add(value)) yield break;
            if (value is object[] array)
                foreach (var item in array)
                    foreach (var inner in Strings(item, seen)) yield return inner;
            else if (value is NrbfObject obj)
                foreach (var member in obj.Members.Values)
                    foreach (var inner in Strings(member, seen)) yield return inner;
        }

        private static int EnumValue(object value)
        {
            if (value is int i) return i;
            if (value is NrbfObject obj && obj.Get("value__") is int v) return v;
            return 0;
        }

        private static string EnumText(object value)
        {
            switch (EnumValue(value))
            {
                case 1: return "Echo";
                case 3: return "Contribute";
                default: return "Synchronize";
            }
        }

        /// <summary>Turns a SyncToy pair into a KUDUSYNC pair (left = source in the one-way modes, as in SyncToy).</summary>
        public static FolderPair ToPair(SyncToyPair p)
        {
            var pair = new FolderPair
            {
                Name = string.IsNullOrWhiteSpace(p.Name) ? "SyncToy pair" : p.Name.Trim(),
                Left = Locations.FromPath(p.Left),
                Right = Locations.FromPath(p.Right),
                Mode = p.Mode,
                Verify = p.CheckContents,
                InRunAll = p.Active,
                KeepDays = p.UsedRecycleBin ? 30 : -1
            };
            if (!string.IsNullOrWhiteSpace(p.Include)) pair.Filters.Include = Normalize(p.Include);
            if (!string.IsNullOrWhiteSpace(p.Exclude))
            {
                var extra = FileFilter.Split(Normalize(p.Exclude));
                var existing = FileFilter.Split(pair.Filters.Exclude);
                pair.Filters.Exclude = string.Join("; ", existing.Concat(extra.Where(e => !existing.Contains(e, StringComparer.OrdinalIgnoreCase))));
            }
            pair.Filters.SkipHidden = (p.ExcludedAttributes & (int)FileAttributes.Hidden) != 0;
            pair.Filters.SkipSystem = (p.ExcludedAttributes & (int)FileAttributes.System) != 0 || pair.Filters.SkipSystem;

            var folders = new List<string>();
            foreach (var dir in p.ExcludedFolders)
            {
                var full = Locations.LongName(dir);
                var relative = Paths.Relative(p.Left, full);
                if (!string.IsNullOrEmpty(relative)) folders.Add(relative);
            }
            pair.Filters.ExcludeFolders = string.Join("; ", folders.Distinct(StringComparer.OrdinalIgnoreCase));
            return pair;
        }

        // SyncToy separated patterns with spaces, commas or semicolons; KUDUSYNC uses semicolons.
        private static string Normalize(string patterns) =>
            string.Join("; ", patterns.Split(new[] { ';', ',', ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()));
    }
}
