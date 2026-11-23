// Brings in the folder pairs of SyncToy, which kept them in its own binary file.
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
        /// <summary>The pair's name.</summary>
        public string Name { get; set; }
        /// <summary>Its first folder.</summary>
        public string Left { get; set; }
        /// <summary>Its second folder.</summary>
        public string Right { get; set; }
        /// <summary>SyncToy's way of syncing (Synchronize, Echo, Contribute).</summary>
        public string ModeName { get; set; }
        /// <summary>The files only kept.</summary>
        public string Include { get; set; }
        /// <summary>The files left out.</summary>
        public string Exclude { get; set; }
        /// <summary>The attributes left out (hidden, system...).</summary>
        public int ExcludedAttributes { get; set; }
        /// <summary>The folders left out.</summary>
        public List<string> ExcludedFolders { get; set; } = new List<string>();
        /// <summary>Compare the files' contents.</summary>
        public bool CheckContents { get; set; }
        /// <summary>It was switched on.</summary>
        public bool Active { get; set; } = true;
        /// <summary>Deleted files went to the Recycle Bin.</summary>
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
        /// <summary>Where SyncToy keeps its pairs.</summary>
        public static string DefaultFile =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "SyncToy", "2.0", "SyncToyDirPairs.bin");

        /// <summary>True when SyncToy's file is there.</summary>
        public static bool Available => File.Exists(DefaultFile);

        /// <summary>Reads SyncToy's pairs from a file.</summary>
        public static List<SyncToyPair> Read(string file)
        {
            using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                return Read(stream);
        }

        /// <summary>Reads SyncToy's pairs from a stream.</summary>
        public static List<SyncToyPair> Read(Stream stream)
        {
            var found = new List<SyncToyPair>();
            try
            {
                foreach (var root in NrbfReader.ReadAll(stream))
                    foreach (var obj in Objects(root))
                    {
                        var pair = FromObject(obj);
                        if (pair != null) found.Add(pair);
                    }
            }
            catch (Exception ex) when (ex is ArgumentException || ex is IndexOutOfRangeException || ex is InvalidCastException ||
                                       ex is OverflowException || ex is FormatException || ex is KeyNotFoundException || ex is OutOfMemoryException)
            {
                // A damaged or made-up file: say so rather than stop the app.
                throw new InvalidDataException("This file isn't SyncToy settings that can be read.", ex);
            }
            return found;
        }

        /// <summary>Every object in a graph (the file may hold one settings object per pair, or a list of them).</summary>
        /// <remarks>Walked with a list rather than by calling itself, so a very long chain of objects can't run out of room.</remarks>
        private static IEnumerable<NrbfObject> Objects(object root)
        {
            var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
            var waiting = new Stack<object>();
            waiting.Push(root);
            while (waiting.Count > 0)
            {
                var value = waiting.Pop();
                if (value == null || value is string || !seen.Add(value)) continue;
                if (value is NrbfObject obj)
                {
                    yield return obj;
                    foreach (var member in obj.Members.Values.Reverse()) waiting.Push(member);
                }
                else if (value is object[] array)
                {
                    for (var i = array.Length - 1; i >= 0; i--) waiting.Push(array[i]);
                }
            }
        }

        private sealed class ReferenceEqualityComparer : IEqualityComparer<object>
        {
            /// <summary>The one comparer.</summary>
            public static readonly ReferenceEqualityComparer Instance = new ReferenceEqualityComparer();
            /// <summary>True only when both are the very same object.</summary>
            public new bool Equals(object a, object b) => ReferenceEquals(a, b);
            /// <summary>The object's own hash.</summary>
            public int GetHashCode(object o) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o);
        }

        /// <summary>A SyncToy pair from its object.</summary>
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

        /// <summary>Every text inside a value (an array, a list...), each object once.</summary>
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

        /// <summary>The number of an enum value.</summary>
        private static int EnumValue(object value)
        {
            if (value is int i) return i;
            if (value is NrbfObject obj && obj.Get("value__") is int v) return v;
            return 0;
        }

        /// <summary>The name of an enum value.</summary>
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
                CompareContents = p.CheckContents,
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
