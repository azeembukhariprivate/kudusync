using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;

namespace Kudusync.Core
{
    /// <summary>One earlier version of a file: the copy a sync replaced or deleted, kept in the history folder.</summary>
    public sealed class SavedVersion
    {
        /// <summary>The file's place in the pair's folder, e.g. "Photos\2024\beach.jpg".</summary>
        public string Relative { get; set; }
        /// <summary>When the sync that replaced or deleted it ran (this version was in the folder until then).</summary>
        public DateTime ReplacedAt { get; set; }
        /// <summary>The kept copy.</summary>
        public string FullPath { get; set; }
        public long Size { get; set; }
        /// <summary>The file's own "modified" time.</summary>
        public DateTime Modified { get; set; }
    }

    /// <summary>
    /// The earlier versions kept for one side of a pair: read from its history folder ("_KUDUSYNC History\&lt;time of the
    /// run&gt;\&lt;the file's place&gt;"), so a file can be put back, or the whole folder copied as it was at a time.
    /// </summary>
    public sealed class Versions
    {
        private readonly Dictionary<string, List<SavedVersion>> _byFile = new Dictionary<string, List<SavedVersion>>(Paths.Comparer);

        private Versions()
        {
        }

        /// <summary>The times of the syncs that kept something, newest first.</summary>
        public List<DateTime> Runs { get; } = new List<DateTime>();

        /// <summary>Every file with at least one earlier version, A to Z.</summary>
        public IReadOnlyList<string> Files => _byFile.Keys.OrderBy(k => k, Paths.Comparer).ToList();

        public int Count => _byFile.Values.Sum(v => v.Count);
        public long Bytes => _byFile.Values.SelectMany(v => v).Sum(v => v.Size);

        /// <summary>A file's earlier versions, newest first.</summary>
        public IReadOnlyList<SavedVersion> Of(string relative) =>
            _byFile.TryGetValue(relative ?? string.Empty, out var list) ? list : (IReadOnlyList<SavedVersion>)new List<SavedVersion>();

        /// <summary>Reads a side's history folder (it may not exist: then there are no earlier versions).</summary>
        public static Versions Read(string historyFolder, CancellationToken cancel = default)
        {
            var v = new Versions();
            var folder = Paths.Long(historyFolder);
            if (!Directory.Exists(folder)) return v;
            foreach (var run in Directory.GetDirectories(folder))
            {
                cancel.ThrowIfCancellationRequested();
                if (!DateTime.TryParseExact(Path.GetFileName(run), History.StampFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var when)) continue;
                var any = false;
                IEnumerable<FileInfo> files;
                try { files = new DirectoryInfo(run).EnumerateFiles("*", SearchOption.AllDirectories).ToList(); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { continue; }
                foreach (var f in files)
                {
                    var relative = f.FullName.Substring(run.Length).TrimStart('\\', '/');
                    if (!Paths.IsWindows) relative = relative.Replace('/', '\\');
                    if (!v._byFile.TryGetValue(relative, out var list)) v._byFile[relative] = list = new List<SavedVersion>();
                    list.Add(new SavedVersion { Relative = relative, ReplacedAt = when, FullPath = f.FullName, Size = f.Length, Modified = f.LastWriteTime });
                    any = true;
                }
                if (any) v.Runs.Add(when);
            }
            foreach (var list in v._byFile.Values) list.Sort((a, b) => b.ReplacedAt.CompareTo(a.ReplacedAt));
            v.Runs.Sort((a, b) => b.CompareTo(a));
            return v;
        }

        /// <summary>
        /// What the folder held just before the sync at <paramref name="before"/>: for each file, the earliest version kept
        /// at or after that time (the copy that was there until then), else the file as it is now. Files added to the
        /// folder later, and never replaced since, can't be told apart and are included as they are now.
        /// </summary>
        public List<(string Relative, string From)> AsItWas(DateTime before, IEnumerable<string> currentFiles, Func<string, string> currentPath)
        {
            var result = new Dictionary<string, string>(Paths.Comparer);
            foreach (var relative in currentFiles) result[relative] = currentPath(relative);
            foreach (var pair in _byFile)
            {
                var then = pair.Value.Where(x => x.ReplacedAt >= before).OrderBy(x => x.ReplacedAt).FirstOrDefault();
                if (then != null) result[pair.Key] = then.FullPath;
            }
            return result.OrderBy(r => r.Key, Paths.Comparer).Select(r => (r.Key, r.Value)).ToList();
        }

        /// <summary>The files in a side's folder now (leaving out the history folder, unfinished copies and Windows' own folders).</summary>
        public static List<string> CurrentFiles(string root, CancellationToken cancel = default)
        {
            var skip = new HashSet<string>(new[] { Paths.HistoryName, "$RECYCLE.BIN", "System Volume Information" }, Paths.Comparer);
            var result = new List<string>();
            var top = Paths.Long(root);
            if (!Directory.Exists(top)) return result;
            var stack = new Stack<string>();
            stack.Push(top);
            while (stack.Count > 0)
            {
                cancel.ThrowIfCancellationRequested();
                var dir = stack.Pop();
                try
                {
                    foreach (var sub in Directory.GetDirectories(dir))
                        if (!(dir == top && skip.Contains(Path.GetFileName(sub)))) stack.Push(sub);
                    foreach (var file in Directory.GetFiles(dir))
                        if (!file.EndsWith(Paths.TempSuffix, StringComparison.OrdinalIgnoreCase))
                            result.Add(file.Substring(top.Length).TrimStart('\\', '/').Replace('/', '\\'));
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    // A folder that can't be read is left out.
                }
            }
            return result;
        }

        /// <summary>Why a folder can't take a copy of the folder as it was, or null when it can (new, or empty).</summary>
        public static string CheckTarget(string target, string sideRoot, string historyFolder)
        {
            if (string.IsNullOrWhiteSpace(target)) return "Choose a folder for the copy.";
            string Norm(string p) => Path.GetFullPath(p).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            var t = Norm(target);
            if (t.StartsWith(Norm(sideRoot), Paths.Comparison) || Norm(sideRoot).StartsWith(t, Paths.Comparison))
                return "Choose a folder outside the backup, so the copy and the backup stay apart.";
            if (!string.IsNullOrEmpty(historyFolder) && t.StartsWith(Norm(historyFolder), Paths.Comparison))
                return "Choose a folder outside the kept versions.";
            var full = Paths.Long(target);
            if (Directory.Exists(full) && Directory.EnumerateFileSystemEntries(full).Any())
                return "Choose an empty folder (or a new one), so nothing in it is mixed up or replaced.";
            return null;
        }

        /// <summary>
        /// Copies the folder as it was into <paramref name="target"/>. <paramref name="progress"/> gets files done and the
        /// total. Returns the number of files copied; files that can't be read are listed in <paramref name="problems"/>.
        /// </summary>
        public static int CopyAsItWas(List<(string Relative, string From)> plan, string target, Action<int, int> progress, List<string> problems, CancellationToken cancel)
        {
            var done = 0;
            foreach (var (relative, from) in plan)
            {
                cancel.ThrowIfCancellationRequested();
                try
                {
                    var to = Paths.Full(target, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(to));
                    File.Copy(from, to, false);
                    File.SetLastWriteTimeUtc(to, File.GetLastWriteTimeUtc(from));
                    var a = File.GetAttributes(to);
                    if ((a & FileAttributes.ReadOnly) != 0) File.SetAttributes(to, a & ~FileAttributes.ReadOnly);
                    done++;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    problems?.Add(relative + ": " + ex.Message);
                }
                progress?.Invoke(done, plan.Count);
            }
            return done;
        }

        /// <summary>
        /// Puts an earlier version back in its usual place. The file there now (if any) goes to the history folder first,
        /// so this can be undone; the earlier version stays kept too.
        /// </summary>
        public static void PutBack(SavedVersion version, FolderPair pair, Side side, string leftRoot, string rightRoot, DateTime now)
        {
            var root = side == Side.Left ? leftRoot : rightRoot;
            var place = Paths.Full(root, version.Relative);
            if (File.Exists(place)) new History(pair, leftRoot, rightRoot, now, always: true).Keep(side, root, version.Relative);
            Directory.CreateDirectory(Path.GetDirectoryName(place));
            var temp = place + Paths.TempSuffix;
            File.Copy(version.FullPath, temp, true);
            File.SetLastWriteTimeUtc(temp, File.GetLastWriteTimeUtc(version.FullPath));
            File.Move(temp, place);
        }
    }
}
