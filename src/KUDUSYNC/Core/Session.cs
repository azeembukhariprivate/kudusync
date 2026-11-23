// One run of a pair from start to end: finding both folders, listing them, making the plan, checking it's safe, doing
// it, and noting the result; and the lock so a pair runs once at a time.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace Kudusync.Core
{
    /// <summary>A reason a pair can't run right now, in words for people.</summary>
    public sealed class SyncProblem : Exception
    {
        /// <summary>
        /// A problem that stops the run (notConnected: a folder wasn't there, so a scheduled run can wait).
        /// </summary>
        public SyncProblem(string message, bool notConnected = false) : base(message) => NotConnected = notConnected;

        /// <summary>A drive or share isn't there (a scheduled run then just notes it, without a notification).</summary>
        public bool NotConnected { get; }
    }

    public sealed class SafetyIssue
    {
        /// <summary>What's wrong, in words.</summary>
        public string Message { get; set; }
        /// <summary>Needs a "yes" before running; scheduled and other unattended runs stop instead.</summary>
        public bool NeedsConfirmation { get; set; }
    }

    /// <summary>
    /// One run of a pair: finds both folders, compares them, works out the plan and carries it out. Only one run of a
    /// pair can happen at a time, across all KUDUSYNC processes.
    /// </summary>
    public sealed class SyncSession : IDisposable
    {
        /// <summary>The pair's lock.</summary>
        private readonly IDisposable _lock;
        /// <summary>Does the plan.</summary>
        private Executor _executor;

        /// <summary>The pair.</summary>
        public FolderPair Pair { get; }
        /// <summary>What started the run.</summary>
        public string Trigger { get; }
        /// <summary>Folder 1, as found.</summary>
        public ResolvedLocation Left { get; private set; }
        /// <summary>Folder 2.</summary>
        public ResolvedLocation Right { get; private set; }
        /// <summary>What's in folder 1.</summary>
        public Tree LeftTree { get; private set; }
        /// <summary>What's in folder 2.</summary>
        public Tree RightTree { get; private set; }
        /// <summary>The record of what was synced last time.</summary>
        public SyncRecord Record { get; private set; }
        /// <summary>The plan.</summary>
        public Plan Plan { get; private set; }
        /// <summary>When the run started.</summary>
        public DateTime Started { get; private set; }
        /// <summary>Notes for the person (a drive found under another letter...).</summary>
        public List<string> Notes { get; } = new List<string>();

        /// <summary>
        /// How far apart two files' times may be and still count as the same (2 seconds, for FAT drives).
        /// </summary>
        public long Tolerance { get; set; } = TimeSpan.FromSeconds(2).Ticks;

        /// <summary>A side is a FAT or exFAT drive: a file time exactly an hour off (the clocks changed) still counts as the same.</summary>
        public bool HourShift => (Left?.Volume?.StoresLocalTime ?? false) || (Right?.Volume?.StoresLocalTime ?? false);

        /// <summary>A run of this pair (made by the static method that takes its lock).</summary>
        private SyncSession(FolderPair pair, string trigger, IDisposable runLock)
        {
            Pair = pair;
            Trigger = trigger;
            _lock = runLock;
        }

        /// <summary>Folder 1's path.</summary>
        public string LeftRoot => Left?.Root;
        /// <summary>Folder 2's path.</summary>
        public string RightRoot => Right?.Root;

        /// <summary>Gets a pair ready to run. Throws <see cref="SyncProblem"/> when it can't run now.</summary>
        public static SyncSession Open(FolderPair pair, string trigger, CancellationToken token = default, int networkAttempts = 3)
        {
            var runLock = PairLock.TryTake(pair.Id);
            if (runLock == null) throw new SyncProblem($"\"{pair.Name}\" is already running.");
            var session = new SyncSession(pair, trigger, runLock);
            try
            {
                session.Find(token, networkAttempts);
                return session;
            }
            catch
            {
                session.Dispose();
                throw;
            }
        }

        /// <summary>Finds both folders (connecting to network folders, retrying a few times).</summary>
        private void Find(CancellationToken token, int networkAttempts)
        {
            Left = Locate(Pair.Left, Pair.LeftTitle, token, networkAttempts);
            Right = Locate(Pair.Right, Pair.RightTitle, token, networkAttempts);

            if (Paths.Overlap(Left.Root, Right.Root))
                throw new SyncProblem($"The two folders are the same, or one is inside the other ({Paths.Display(Left.Root)} and {Paths.Display(Right.Root)}). Edit the pair to choose different folders.");

            Record = SyncRecord.Load(DataStore.RecordFile(Pair.Id));
            if (!string.Equals(Record.LeftRoot, RecordKey(Pair.Left), StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Record.RightRoot, RecordKey(Pair.Right), StringComparison.OrdinalIgnoreCase))
                Record = new SyncRecord(); // the folders were changed: start afresh

            var leftExists = Directory.Exists(Paths.Long(Left.Root));
            var rightExists = Directory.Exists(Paths.Long(Right.Root));
            if (Pair.IsOneWay)
            {
                if (!leftExists) throw new SyncProblem($"The source folder {Paths.Display(Left.Root)} wasn't found.", Left.NotConnected);
            }
            else if (!Record.IsEmpty && (!leftExists || !rightExists))
            {
                var missing = !leftExists ? Left.Root : Right.Root;
                throw new SyncProblem($"The folder {Paths.Display(missing)} wasn't found. (It was there at the last sync, so KUDUSYNC won't treat everything in it as deleted.)");
            }
        }

        /// <summary>Finds one folder.</summary>
        private static ResolvedLocation Locate(Location location, string title, CancellationToken token, int networkAttempts)
        {
            var resolved = Locations.Resolve(location);
            if (!resolved.Ok) throw new SyncProblem(title + ": " + resolved.Problem, resolved.NotConnected);
            if (location.IsNetwork && Paths.IsWindows)
            {
                var problem = NetworkShares.Connect(location.Path, networkAttempts, 5, token);
                if (problem != null) throw new SyncProblem(title + ": " + problem);
            }
            return resolved;
        }

        /// <summary>Identifies the folders a record belongs to, so it's not used after the pair is changed to other folders.</summary>
        public static string RecordKey(Location location) => (location.Path ?? string.Empty) + "|" + (location.VolumeSerial ?? string.Empty);

        /// <summary>Lists both folders. <paramref name="progress"/> gets the number of items looked at so far.</summary>
        public void Scan(Action<int> progress = null, CancellationToken token = default)
        {
            _scanToken = token;
            Started = DateTime.Now;
            var leftCount = 0;
            LeftTree = ScanSide(Left, Side.Left, n => progress?.Invoke(leftCount = n), token);
            RightTree = ScanSide(Right, Side.Right, n => progress?.Invoke(leftCount + n), token);
        }

        /// <summary>Stops the listing.</summary>
        private CancellationToken _scanToken;

        /// <summary>Compares a file's two copies byte by byte (for <see cref="FolderPair.CompareContents"/>).</summary>
        private bool ContentsDiffer(string relative)
        {
            _scanToken.ThrowIfCancellationRequested();
            try
            {
                return !Paths.SameContents(Paths.Full(Left.Root, relative), Paths.Full(Right.Root, relative), _scanToken);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Notes.Add($"Couldn't compare the contents of {relative}: {ex.Message}");
                return false;
            }
        }

        /// <summary>Lists one side.</summary>
        private Tree ScanSide(ResolvedLocation location, Side side, Action<int> progress, CancellationToken token)
        {
            if (!Directory.Exists(Paths.Long(location.Root))) return new Tree();
            var skip = new List<string>();
            if (!string.IsNullOrWhiteSpace(Pair.HistoryFolder))
            {
                var inside = Paths.Relative(location.Root, Pair.HistoryFolder);
                if (!string.IsNullOrEmpty(inside)) skip.Add(inside);
            }
            var filter = new FileFilter(Pair.Filters, skip);
            var stable = location.Volume?.StableFileIds ?? false;
            return Scanner.Scan(location.Root, filter, progress, token, stable);
        }

        /// <summary>
        /// Makes the plan, and the checks that keep it safe (too much deleted, a side empty, too little room).
        /// </summary>
        public Plan BuildPlan(DateTime? now = null)
        {
            Plan = Planner.Build(LeftTree, RightTree, Record, new PlanOptions
            {
                Mode = Pair.Mode,
                DetectRenames = Pair.DetectRenames,
                Tolerance = Tolerance,
                HourShift = HourShift,
                ContentsDiffer = Pair.CompareContents ? ContentsDiffer : (Func<string, bool>)null,
                Now = now ?? DateTime.Now
            });
            Notes.Clear();
            foreach (var tree in new[] { LeftTree, RightTree })
            {
                var title = tree == LeftTree ? Pair.LeftTitle : Pair.RightTitle;
                foreach (var p in tree.Problems.Take(20)) Notes.Add($"{title}: couldn't read {p}. Nothing in that folder was changed.");
                if (tree.SkippedLinks > 0) Notes.Add($"{title}: {Format.Count(tree.SkippedLinks, "link")} to other folders skipped.");
            }
            if (Plan.Clashes.Count > 0)
                Notes.Add($"{Format.Count(Plan.Clashes.Count, "item")} left alone because a file on one side has the same name as a folder on the other: " + string.Join(", ", Plan.Clashes.Take(5)) + (Plan.Clashes.Count > 5 ? ", …" : string.Empty));
            return Plan;
        }

        /// <summary>Checks for plans that look like a mistake (an unplugged or emptied folder) before anything is deleted.</summary>
        public List<SafetyIssue> SafetyCheck(bool pauseMassDeletes = true)
        {
            var issues = new List<SafetyIssue>();
            foreach (var side in new[] { Side.Left, Side.Right })
            {
                var target = side == Side.Left ? LeftTree : RightTree;
                var other = side == Side.Left ? RightTree : LeftTree;
                var deletes = Plan.DeletesOn(side);
                if (deletes == 0) continue;
                var title = side == Side.Left ? Pair.LeftTitle : Pair.RightTitle;
                var otherTitle = side == Side.Left ? Pair.RightTitle : Pair.LeftTitle;
                var files = target.FileCount;
                if (other.FileCount == 0)
                {
                    issues.Add(new SafetyIssue
                    {
                        Message = $"The {otherTitle.ToLowerInvariant()} folder is empty, so this would delete all {Format.Count(deletes, "file")} in the {title.ToLowerInvariant()}. If the folder was emptied by mistake, or a drive isn't working, don't go ahead.",
                        NeedsConfirmation = true
                    });
                }
                else if (pauseMassDeletes && deletes > 10 && deletes * 2 > files)
                {
                    issues.Add(new SafetyIssue
                    {
                        Message = $"This would delete {Format.Count(deletes, "file")}, more than half of the {Format.Count(files, "file")} in the {title.ToLowerInvariant()}.",
                        NeedsConfirmation = true
                    });
                }
            }

            foreach (var side in new[] { Side.Left, Side.Right })
            {
                var bytes = Plan.BytesTo(side);
                if (bytes == 0) continue;
                var root = side == Side.Left ? Left.Root : Right.Root;
                var free = Locations.FreeSpace(ExistingParent(root));
                if (free >= 0 && bytes > free)
                    issues.Add(new SafetyIssue { Message = $"The {(side == Side.Left ? Pair.LeftTitle : Pair.RightTitle).ToLowerInvariant()} needs {Format.Size(bytes)} but only has {Format.Size(free)} free. Files that don't fit will be skipped." });
            }
            return issues;
        }

        /// <summary>The nearest folder that's there, going up from this one.</summary>
        private static string ExistingParent(string folder)
        {
            var path = folder;
            while (!string.IsNullOrEmpty(path) && !Directory.Exists(Paths.Long(path))) path = Path.GetDirectoryName(path);
            return string.IsNullOrEmpty(path) ? folder : path;
        }

        /// <summary>Carries out the plan, saves the record and the log, and returns what happened.</summary>
        public RunResult Execute(Action<RunProgress> progress = null, CancellationToken token = default)
        {
            var result = new RunResult { PairId = Pair.Id, PairName = Pair.Name, Mode = Pair.Mode, Trigger = Trigger, Started = Started == default ? DateTime.Now : Started };
            foreach (var root in new[] { Left.Root, Right.Root })
                if (!Directory.Exists(Paths.Long(root))) Directory.CreateDirectory(Paths.Long(root));

            _executor = new Executor(Pair, Left.Root, Right.Root, LeftTree, RightTree, result.Started);
            if (progress != null) _executor.Progress += progress;
            var finished = _executor.Run(Plan, token);

            if (Pair.Mode != SyncMode.Move)
            {
                var record = BuildRecord(LeftTree, RightTree, Record, Tolerance, HourShift);
                record.LeftRoot = RecordKey(Pair.Left);
                record.RightRoot = RecordKey(Pair.Right);
                try { record.Save(DataStore.RecordFile(Pair.Id)); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { Notes.Add("Couldn't save the record of this sync: " + ex.Message); }
            }

            if (finished)
            {
                foreach (var side in new[] { Side.Left, Side.Right })
                {
                    try { History.Cleanup(Pair, side == Side.Left ? Left.Root : Right.Root, side, DateTime.Now); }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
                }
            }

            Fill(result, Plan);
            result.Status = !finished ? RunStatus.Stopped
                : result.Failed > 0 || Plan.Actions.Any(a => a.State == ActionState.Done && a.Error != null) || Notes.Count > 0 ? RunStatus.Warnings
                : RunStatus.Ok;
            result.Finished = DateTime.Now;
            result.LogFile = RunLog.WriteText(result, Left.Root, Right.Root, Pair, Notes, _executor.Log);
            RunLog.Add(result);
            return result;
        }

        /// <summary>Copies the plan's counts into a run's result.</summary>
        public static void Fill(RunResult result, Plan plan)
        {
            var done = plan.Actions.Where(a => a.State == ActionState.Done).ToList();
            result.Moved = done.Count(a => a.RemoveSource && a.Error == null) + done.Count(a => a.Kind == ActionKind.Delete && a.Permanent);
            result.Copied = done.Count(a => a.Kind == ActionKind.Copy && !a.Conflict && !(a.RemoveSource && a.Error == null));
            result.Updated = done.Count(a => a.Kind == ActionKind.Update && !(a.RemoveSource && a.Error == null));
            result.Deleted = done.Count(a => a.Kind == ActionKind.Delete && !a.Permanent);
            result.Renamed = done.Count(a => a.Kind == ActionKind.Rename && !a.Conflict);
            result.Conflicts = done.Count(a => a.Kind == ActionKind.Rename && a.Conflict);
            result.FoldersCreated = done.Count(a => a.Kind == ActionKind.CreateFolder);
            result.FoldersDeleted = done.Count(a => a.Kind == ActionKind.DeleteFolder);
            result.Failed = plan.Actions.Count(a => a.State == ActionState.Failed);
            result.Bytes = done.Where(a => a.IsCopy).Sum(a => a.Size);
            result.Problems = plan.Actions.Where(a => a.State == ActionState.Failed || (a.State == ActionState.Done && a.Error != null))
                .Take(20).Select(a => a.Path + ": " + a.Error).ToList();
        }

        /// <summary>
        /// The record after a run: files that are the same on both sides are recorded as they are now. Files that differ
        /// (a step failed or was unticked) keep their previous record, so the next run sees the same change again.
        /// </summary>
        public static SyncRecord BuildRecord(Tree left, Tree right, SyncRecord old, long tolerance, bool hourShift = false)
        {
            var record = new SyncRecord();
            foreach (var l in left.Items.Values)
            {
                var r = right.Get(l.Path);
                if (r != null && Planner.Same(l, r, tolerance, hourShift))
                {
                    record.Items[l.Path] = new RecordEntry
                    {
                        Path = l.Path, IsDir = l.IsDir, Size = l.Size,
                        LeftMtime = l.Mtime, RightMtime = r.Mtime, LeftId = l.FileId, RightId = r.FileId
                    };
                }
            }
            foreach (var b in old.Items.Values)
            {
                if (record.Items.ContainsKey(b.Path)) continue;
                var onSomeSide = left.Get(b.Path) != null || right.Get(b.Path) != null;
                var unreadable = left.IsUnderUnreadable(b.Path) || right.IsUnderUnreadable(b.Path);
                if (onSomeSide || unreadable) record.Items[b.Path] = b;
            }
            return record;
        }

        /// <summary>Lets go of the pair's lock.</summary>
        public void Dispose() => _lock?.Dispose();
    }

    /// <summary>
    /// Makes sure a pair only runs once at a time, across processes. It's a lock file held open while the pair runs, so it
    /// can be released from any thread, and Windows releases it if the process ends unexpectedly.
    /// </summary>
    public static class PairLock
    {
        /// <summary>Takes a pair's lock, or null when it's already running.</summary>
        public static IDisposable TryTake(string pairId)
        {
            var folder = Path.Combine(DataStore.Folder, "Locks");
            try
            {
                Directory.CreateDirectory(folder);
                return new FileStream(Path.Combine(folder, Paths.SafeName(pairId) + ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
            }
            catch (IOException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
        }

        /// <summary>True when a pair is running.</summary>
        public static bool IsRunning(string pairId)
        {
            using (var taken = TryTake(pairId)) return taken == null;
        }
    }
}
