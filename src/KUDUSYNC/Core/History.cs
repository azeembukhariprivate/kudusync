using System;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Kudusync.Core
{
    /// <summary>
    /// Where replaced and deleted files go: "_KUDUSYNC History\2026-10-24 091530\…" at the top of that side's folder
    /// (or a folder chosen for the pair), so nothing a sync removes is lost straight away. Old runs are cleaned up after
    /// the pair's number of days.
    /// </summary>
    public sealed class History
    {
        public const string StampFormat = "yyyy-MM-dd HHmmss";

        private readonly FolderPair _pair;
        private readonly string _leftRoot;
        private readonly string _rightRoot;
        private readonly string _stamp;

        public History(FolderPair pair, string leftRoot, string rightRoot, DateTime started)
        {
            _pair = pair;
            _leftRoot = leftRoot;
            _rightRoot = rightRoot;
            _stamp = started.ToString(StampFormat, CultureInfo.InvariantCulture);
        }

        public bool Enabled => _pair.KeepDays >= 0;

        /// <summary>The folder holding all of a side's runs.</summary>
        public static string BaseFolder(FolderPair pair, string sideRoot, Side side)
        {
            if (string.IsNullOrWhiteSpace(pair.HistoryFolder)) return Path.Combine(sideRoot, Paths.HistoryName);
            var folder = Path.Combine(pair.HistoryFolder, Paths.SafeName(pair.Name));
            return pair.Mode == SyncMode.TwoWay ? Path.Combine(folder, side == Side.Left ? "Folder 1" : "Folder 2") : folder;
        }

        public string RunFolder(Side side) => Path.Combine(BaseFolder(_pair, side == Side.Left ? _leftRoot : _rightRoot, side), _stamp);

        /// <summary>Moves a file into this run's history folder (or deletes it, if the pair keeps no history).</summary>
        public void Keep(Side side, string root, string relative)
        {
            var source = Paths.Full(root, relative);
            if (!Enabled)
            {
                DeleteFile(source);
                return;
            }

            var baseFolder = BaseFolder(_pair, side == Side.Left ? _leftRoot : _rightRoot, side);
            var fresh = !Directory.Exists(Paths.Long(baseFolder));
            var target = Paths.Full(RunFolder(side), relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            if (fresh && string.IsNullOrWhiteSpace(_pair.HistoryFolder))
            {
                try { File.SetAttributes(Paths.Long(baseFolder), File.GetAttributes(Paths.Long(baseFolder)) | FileAttributes.Hidden); }
                catch { /* not important */ }
            }
            if (File.Exists(target)) target = FreeName(target);
            File.Move(source, target);
        }

        private static string FreeName(string path)
        {
            var dir = Path.GetDirectoryName(path);
            var stem = Path.GetFileNameWithoutExtension(path);
            var ext = Path.GetExtension(path);
            for (var n = 2; ; n++)
            {
                var candidate = Path.Combine(dir, $"{stem} ({n}){ext}");
                if (!File.Exists(candidate)) return candidate;
            }
        }

        public static void DeleteFile(string path)
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReadOnly) != 0) File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
            File.Delete(path);
        }

        /// <summary>Removes runs older than the pair's number of days. Returns how many were removed.</summary>
        public static int Cleanup(FolderPair pair, string sideRoot, Side side, DateTime now)
        {
            if (pair.KeepDays <= 0) return 0;
            var folder = Paths.Long(BaseFolder(pair, sideRoot, side));
            if (!Directory.Exists(folder)) return 0;
            var removed = 0;
            foreach (var run in Directory.GetDirectories(folder))
            {
                if (!DateTime.TryParseExact(Path.GetFileName(run), StampFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var when)) continue;
                if ((now - when).TotalDays <= pair.KeepDays) continue;
                try
                {
                    DeleteTree(run);
                    removed++;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    // Something in it is open; try again next time.
                }
            }
            return removed;
        }

        public static void DeleteTree(string folder)
        {
            foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).ToList())
            {
                var a = File.GetAttributes(file);
                if ((a & FileAttributes.ReadOnly) != 0) File.SetAttributes(file, a & ~FileAttributes.ReadOnly);
            }
            Directory.Delete(folder, true);
        }

        /// <summary>The size and number of files kept for a side, for showing in the app.</summary>
        public static (int Files, long Bytes) Measure(FolderPair pair, string sideRoot, Side side)
        {
            var folder = Paths.Long(BaseFolder(pair, sideRoot, side));
            if (!Directory.Exists(folder)) return (0, 0);
            try
            {
                var files = new DirectoryInfo(folder).EnumerateFiles("*", SearchOption.AllDirectories).ToList();
                return (files.Count, files.Sum(f => f.Length));
            }
            catch
            {
                return (0, 0);
            }
        }
    }
}
