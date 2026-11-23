// Works out what a run will do: compares both sides with each other and with the record of last time, by the pair's way
// of syncing, finding new, changed, deleted and renamed files and clashes.
using System;
using System.Collections.Generic;
using System.Linq;

namespace Kudusync.Core
{
    /// <summary>Which side: folder 1 (left) or folder 2 (right).</summary>
    public enum Side { Left, Right }

    /// <summary>What an action does.</summary>
    public enum ActionKind { Copy, Update, Delete, Rename, CreateFolder, DeleteFolder }

    /// <summary>Where an action is up to.</summary>
    public enum ActionState { Pending, Done, Failed, Skipped }

    /// <summary>One thing a sync will do.</summary>
    public sealed class SyncAction
    {
        /// <summary>What it does.</summary>
        public ActionKind Kind { get; set; }
        /// <summary>The side that changes.</summary>
        public Side Target { get; set; }
        /// <summary>The path on the target side.</summary>
        public string Path { get; set; }
        /// <summary>Rename: the old path on the target side. Copy/Update: the path on the other side, when it differs.</summary>
        public string From { get; set; }
        /// <summary>The file's size.</summary>
        public long Size { get; set; }
        /// <summary>Why, in words.</summary>
        public string Reason { get; set; }
        /// <summary>Part of keeping both copies of a file that changed on both sides.</summary>
        public bool Conflict { get; set; }
        /// <summary>Move mode: remove the source file once it's safely copied.</summary>
        public bool RemoveSource { get; set; }
        /// <summary>Delete without keeping an old version (Move mode's source files, which are already in the destination).</summary>
        public bool Permanent { get; set; }
        /// <summary>Unticked in the preview: skipped this time.</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>Where it's up to.</summary>
        public ActionState State { get; set; }
        /// <summary>Why it failed.</summary>
        public string Error { get; set; }

        /// <summary>The side it copies from.</summary>
        public Side SourceSide => Target == Side.Left ? Side.Right : Side.Left;
        /// <summary>The path it copies from.</summary>
        public string SourcePath => From ?? Path;
        /// <summary>True when it copies a file.</summary>
        public bool IsCopy => Kind == ActionKind.Copy || Kind == ActionKind.Update;

        /// <summary>A short description for the tests.</summary>
        public override string ToString() => $"{Kind} {Target} {(From != null ? From + " -> " : string.Empty)}{Path}";
    }

    public sealed class PlanOptions
    {
        /// <summary>The way of syncing.</summary>
        public SyncMode Mode { get; set; }
        /// <summary>Notice renamed files and rename them on the other side too (instead of copying again).</summary>
        public bool DetectRenames { get; set; } = true;
        /// <summary>How far apart two modified times may be and still count as the same (FAT drives store them to 2 seconds).</summary>
        public long Tolerance { get; set; } = TimeSpan.FromSeconds(2).Ticks;
        /// <summary>
        /// A side is a FAT or exFAT drive, which stores local time: when the clocks change, every file's time moves by
        /// exactly one hour, so a one-hour difference also counts as the same.
        /// </summary>
        public bool HourShift { get; set; }
        /// <summary>
        /// When set, files whose size and time match are also compared by contents: it gets a file's relative path and
        /// returns true when the two copies differ.
        /// </summary>
        public Func<string, bool> ContentsDiffer { get; set; }
        /// <summary>The time now (set in the tests).</summary>
        public DateTime Now { get; set; } = DateTime.Now;

        /// <summary>The same size and time, and (when contents are compared) the same contents too.</summary>
        internal bool Same(Entry a, Entry b) =>
            Planner.Same(a, b, Tolerance, HourShift) && (a.IsDir || ContentsDiffer == null || !ContentsDiffer(a.Path));
    }

    public sealed class Plan
    {
        /// <summary>The way of syncing.</summary>
        public SyncMode Mode { get; set; }
        /// <summary>Every action.</summary>
        public List<SyncAction> Actions { get; } = new List<SyncAction>();
        /// <summary>Files changed on both sides since last time.</summary>
        public List<string> Clashes { get; } = new List<string>();
        /// <summary>Move mode: remove source folders that end up empty.</summary>
        public bool CleanSourceFolders { get; set; }

        /// <summary>The actions that will be done (not switched off).</summary>
        public IEnumerable<SyncAction> Active => Actions.Where(a => a.Enabled);

        /// <summary>True when there's nothing to do.</summary>
        public bool IsEmpty => !Active.Any();

        /// <summary>How many actions of this kind (only clashes, or none, when asked).</summary>
        public int Count(ActionKind kind, bool? conflict = null) =>
            Active.Count(a => a.Kind == kind && (conflict == null || a.Conflict == conflict.Value));

        /// <summary>How many new files will be copied.</summary>
        public int NewFiles => Active.Count(a => a.Kind == ActionKind.Copy && !a.Conflict);
        /// <summary>How many changed files will be updated.</summary>
        public int ChangedFiles => Count(ActionKind.Update);
        /// <summary>How many files will be deleted.</summary>
        public int DeletedFiles => Active.Count(a => a.Kind == ActionKind.Delete && !a.Permanent);
        /// <summary>How many files are taken out of the source (move).</summary>
        public int RemovedFromSource => Active.Count(a => a.Kind == ActionKind.Delete && a.Permanent) + Active.Count(a => a.RemoveSource);
        /// <summary>How many files will be renamed.</summary>
        public int Renamed => Active.Count(a => a.Kind == ActionKind.Rename && !a.Conflict);
        /// <summary>How many clashes (kept with a new name).</summary>
        public int Conflicts => Active.Count(a => a.Kind == ActionKind.Rename && a.Conflict);
        /// <summary>How many folders will be made.</summary>
        public int FoldersCreated => Count(ActionKind.CreateFolder);
        /// <summary>How many folders will be removed.</summary>
        public int FoldersDeleted => Count(ActionKind.DeleteFolder);
        /// <summary>How many bytes will be copied.</summary>
        public long BytesToCopy => Active.Where(a => a.IsCopy).Sum(a => a.Size);
        /// <summary>How many bytes will be copied to a side.</summary>
        public long BytesTo(Side side) => Active.Where(a => a.IsCopy && a.Target == side).Sum(a => a.Size);
        /// <summary>How many files will be deleted on a side.</summary>
        public int DeletesOn(Side side) => Active.Count(a => a.Kind == ActionKind.Delete && a.Target == side && !a.Permanent);
    }

    /// <summary>Works out what a sync has to do by comparing both sides (and the record of the last sync).</summary>
    public static class Planner
    {
        /// <summary>What happened to a file since last time.</summary>
        private enum Change { None, New, Unchanged, Changed, Deleted }

        /// <summary>Files smaller than this are only treated as renamed when the file system's ID proves it.</summary>
        public const long MinGuessedRenameSize = 1024;

        /// <summary>Makes the plan for two sides and the record of last time.</summary>
        public static Plan Build(Tree left, Tree right, SyncRecord record, PlanOptions options)
        {
            record = record ?? new SyncRecord();
            var plan = new Plan { Mode = options.Mode };
            switch (options.Mode)
            {
                case SyncMode.TwoWay: TwoWay(plan, left, right, record, options); break;
                case SyncMode.Move: Move(plan, left, right, options); break;
                default: OneWay(plan, left, right, record, options); break;
            }

            // A stable order: renames, new folders, copies, deletes, removed folders (deepest first).
            var order = plan.Actions
                .OrderBy(a => Phase(a))
                .ThenBy(a => a.Kind == ActionKind.DeleteFolder ? -Paths.Depth(a.Path) : 0)
                .ThenBy(a => a.Path, StringComparer.OrdinalIgnoreCase)
                .ToList();
            plan.Actions.Clear();
            plan.Actions.AddRange(order);
            return plan;
        }

        /// <summary>The order actions run in. Conflict renames first, so the names they free up can be filled.</summary>
        public static int Phase(SyncAction a)
        {
            switch (a.Kind)
            {
                case ActionKind.Rename: return a.Conflict ? 0 : 1;
                case ActionKind.CreateFolder: return 2;
                case ActionKind.Copy:
                case ActionKind.Update: return 3;
                case ActionKind.Delete: return 4;
                default: return 5;
            }
        }

        /// <summary>
        /// True when two files are the same (size, and times within tolerance; an hour apart counts too when hourShift,
        /// for FAT drives and summer time).
        /// </summary>
        public static bool Same(Entry a, Entry b, long tolerance, bool hourShift = false)
        {
            if (a == null || b == null || a.IsDir != b.IsDir) return false;
            if (a.IsDir) return true;
            return a.Size == b.Size && TimesMatch(a.Mtime, b.Mtime, tolerance, hourShift);
        }

        /// <summary>True when a file is as the record says.</summary>
        public static bool SameAsRecord(Entry e, RecordEntry r, Side side, long tolerance, bool hourShift = false)
        {
            if (e == null || r == null || e.IsDir != r.IsDir) return false;
            if (e.IsDir) return true;
            return e.Size == r.Size && TimesMatch(e.Mtime, r.Mtime(side), tolerance, hourShift);
        }

        /// <summary>Two modified times count as the same within the tolerance, or exactly one hour apart (see <see cref="PlanOptions.HourShift"/>).</summary>
        public static bool TimesMatch(long a, long b, long tolerance, bool hourShift)
        {
            var apart = Math.Abs(a - b);
            return apart <= tolerance || (hourShift && Math.Abs(apart - TimeSpan.TicksPerHour) <= tolerance);
        }

        /// <summary>
        /// True when a path is in a folder that couldn't be read on either side (it's never deleted).
        /// </summary>
        private static bool Protected(string path, Tree left, Tree right) => left.IsUnderUnreadable(path) || right.IsUnderUnreadable(path);

        // ------------------------------------------------------------------ Mirror and Update

        /// <summary>
        /// The one-way plan: copy what's new or changed, and (for a mirror) delete what's gone from the source.
        /// </summary>
        private static void OneWay(Plan plan, Tree left, Tree right, SyncRecord record, PlanOptions o)
        {
            var mirror = o.Mode == SyncMode.Mirror;
            var newFiles = new List<Entry>();
            var deletes = new List<SyncAction>();

            foreach (var l in left.Items.Values)
            {
                if (Protected(l.Path, left, right)) continue;
                var r = right.Get(l.Path);
                if (l.IsDir)
                {
                    if (r == null) plan.Actions.Add(new SyncAction { Kind = ActionKind.CreateFolder, Target = Side.Right, Path = l.Path, Reason = "New folder" });
                    else if (!r.IsDir) plan.Clashes.Add(l.Path);
                    continue;
                }
                if (r == null) newFiles.Add(l);
                else if (r.IsDir) plan.Clashes.Add(l.Path);
                else if (o.Same(l, r))
                {
                    if (!string.Equals(l.Path, r.Path, StringComparison.Ordinal))
                        plan.Actions.Add(new SyncAction { Kind = ActionKind.Rename, Target = Side.Right, From = r.Path, Path = l.Path, Size = l.Size, Reason = "Name's capitalisation changed" });
                }
                else
                {
                    plan.Actions.Add(new SyncAction
                    {
                        Kind = ActionKind.Update, Target = Side.Right, Path = l.Path, Size = l.Size,
                        Reason = Same(l, r, o.Tolerance, o.HourShift) ? "Same size and time, but different contents"
                            : r.Mtime > l.Mtime + o.Tolerance ? "Different from the source (the destination's copy is newer)" : "Changed"
                    });
                }
            }

            var rightOnly = right.Items.Values.Where(r => left.Get(r.Path) == null && !Protected(r.Path, left, right)).ToList();
            if (mirror)
            {
                foreach (var r in rightOnly)
                {
                    var a = r.IsDir
                        ? new SyncAction { Kind = ActionKind.DeleteFolder, Target = Side.Right, Path = r.Path, Reason = "Not in the source" }
                        : new SyncAction { Kind = ActionKind.Delete, Target = Side.Right, Path = r.Path, Size = r.Size, Reason = "Not in the source" };
                    if (r.IsDir) plan.Actions.Add(a);
                    else deletes.Add(a);
                }
            }

            // Renamed or moved in the source: rename in the destination instead of copying again.
            var renamedAway = new HashSet<string>(Paths.Comparer);
            if (o.DetectRenames && !record.IsEmpty)
            {
                var candidates = rightOnly.Where(r => !r.IsDir).ToList();
                foreach (var match in MatchRenames(newFiles, candidates, left, record, Side.Left, Side.Right, o))
                {
                    plan.Actions.Add(new SyncAction { Kind = ActionKind.Rename, Target = Side.Right, From = match.Value.Path, Path = match.Key.Path, Size = match.Key.Size, Reason = "Renamed or moved in the source" });
                    newFiles.Remove(match.Key);
                    deletes.RemoveAll(d => d.Path.Equals(match.Value.Path, Paths.Comparison));
                    renamedAway.Add(match.Value.Path);
                }
            }

            foreach (var l in newFiles)
                plan.Actions.Add(new SyncAction { Kind = ActionKind.Copy, Target = Side.Right, Path = l.Path, Size = l.Size, Reason = "New" });
            plan.Actions.AddRange(deletes);

            if (!mirror && renamedAway.Count > 0)
            {
                // Update never deletes, but a folder whose files were all renamed away is removed rather than left empty.
                foreach (var dir in rightOnly.Where(r => r.IsDir).OrderByDescending(r => Paths.Depth(r.Path)))
                {
                    var prefix = dir.Path + "\\";
                    var inside = right.Items.Values.Where(e => e.Path.StartsWith(prefix, Paths.Comparison)).ToList();
                    if (inside.Count > 0 && inside.All(e => e.IsDir ? plan.Actions.Any(a => a.Kind == ActionKind.DeleteFolder && a.Path.Equals(e.Path, Paths.Comparison)) : renamedAway.Contains(e.Path)))
                        plan.Actions.Add(new SyncAction { Kind = ActionKind.DeleteFolder, Target = Side.Right, Path = dir.Path, Reason = "Its files were moved" });
                }
            }
        }

        /// <summary>
        /// Pairs files that are new on one side with files that were removed there since the last sync but are still
        /// unchanged on the other side. On NTFS the file ID makes the match exact; otherwise size and modified time
        /// must match exactly one file. Ambiguous matches are left alone (the file is simply copied).
        /// </summary>
        private static Dictionary<Entry, Entry> MatchRenames(List<Entry> newFiles, List<Entry> otherSideOnly, Tree sourceTree,
            SyncRecord record, Side sourceSide, Side otherSide, PlanOptions o)
        {
            var result = new Dictionary<Entry, Entry>();
            var pool = new List<KeyValuePair<Entry, RecordEntry>>();
            foreach (var candidate in otherSideOnly)
            {
                var b = record.Get(candidate.Path);
                if (b == null || b.IsDir || b.Size == 0) continue;
                if (sourceTree.Get(candidate.Path) != null) continue;
                if (!SameAsRecord(candidate, b, otherSide, o.Tolerance, o.HourShift)) continue; // changed on that side since: not a pure rename
                pool.Add(new KeyValuePair<Entry, RecordEntry>(candidate, b));
            }
            if (pool.Count == 0) return result;

            var bySize = pool.GroupBy(p => p.Value.Size).ToDictionary(g => g.Key, g => g.ToList());
            var claims = new Dictionary<Entry, List<Entry>>();
            foreach (var n in newFiles)
            {
                if (n.Size == 0 || !bySize.TryGetValue(n.Size, out var sameSize)) continue;
                var matches = sameSize.Where(p => TimesMatch(n.Mtime, p.Value.Mtime(sourceSide), o.Tolerance, o.HourShift)).ToList();
                var byId = sourceTree.StableFileIds && n.FileId != 0 ? matches.Where(p => p.Value.Id(sourceSide) == n.FileId).ToList() : null;
                if (byId != null && byId.Count > 0) matches = byId;
                else
                {
                    // Without a file ID, size and time alone could match two different small files: also require the same
                    // kind of file, and skip small files (copying those again costs next to nothing anyway).
                    if (n.Size < MinGuessedRenameSize) continue;
                    var ext = System.IO.Path.GetExtension(n.Path);
                    matches = matches.Where(p => string.Equals(System.IO.Path.GetExtension(p.Key.Path), ext, StringComparison.OrdinalIgnoreCase)).ToList();
                }
                if (matches.Count != 1) continue;
                var target = matches[0].Key;
                if (!claims.TryGetValue(target, out var list)) claims[target] = list = new List<Entry>();
                list.Add(n);
            }
            foreach (var claim in claims)
                if (claim.Value.Count == 1) result[claim.Value[0]] = claim.Key;
            return result;
        }

        // ------------------------------------------------------------------ Move

        /// <summary>The move plan: copy, then take the files out of the source.</summary>
        private static void Move(Plan plan, Tree left, Tree right, PlanOptions o)
        {
            plan.CleanSourceFolders = true;
            foreach (var l in left.Items.Values)
            {
                if (Protected(l.Path, left, right)) continue;
                var r = right.Get(l.Path);
                if (l.IsDir)
                {
                    if (r == null) plan.Actions.Add(new SyncAction { Kind = ActionKind.CreateFolder, Target = Side.Right, Path = l.Path, Reason = "New folder" });
                    else if (!r.IsDir) plan.Clashes.Add(l.Path);
                    continue;
                }
                if (r == null)
                    plan.Actions.Add(new SyncAction { Kind = ActionKind.Copy, Target = Side.Right, Path = l.Path, Size = l.Size, RemoveSource = true, Reason = "Moved" });
                else if (r.IsDir)
                    plan.Clashes.Add(l.Path);
                else if (o.Same(l, r))
                    plan.Actions.Add(new SyncAction { Kind = ActionKind.Delete, Target = Side.Left, Path = l.Path, Size = l.Size, Permanent = true, Reason = "Already in the destination" });
                else
                    plan.Actions.Add(new SyncAction { Kind = ActionKind.Update, Target = Side.Right, Path = l.Path, Size = l.Size, RemoveSource = true, Reason = "Moved (replaces the destination's copy)" });
            }
        }

        // ------------------------------------------------------------------ Two-way

        /// <summary>
        /// The two-way plan: each side's changes go to the other; a file changed on both sides is a clash (both kept).
        /// </summary>
        private static void TwoWay(Plan plan, Tree left, Tree right, SyncRecord record, PlanOptions o)
        {
            var paths = new SortedSet<string>(left.Items.Keys.Concat(right.Items.Keys), Paths.Comparer);
            var newLeft = new List<Entry>();
            var newRight = new List<Entry>();
            var deleteLeft = new List<SyncAction>();
            var deleteRight = new List<SyncAction>();
            var conflicts = new List<string>();
            var copies = new List<SyncAction>();

            foreach (var path in paths)
            {
                if (Protected(path, left, right)) continue;
                var l = left.Get(path);
                var r = right.Get(path);
                var b = record.Get(path);

                if (l != null && r != null && l.IsDir != r.IsDir)
                {
                    plan.Clashes.Add(path);
                    continue;
                }

                if ((l?.IsDir ?? false) || (r?.IsDir ?? false))
                {
                    if (l != null && r != null) continue;
                    var known = b != null && b.IsDir;
                    if (l != null)
                        plan.Actions.Add(known
                            ? new SyncAction { Kind = ActionKind.DeleteFolder, Target = Side.Left, Path = path, Reason = "Deleted in " + SideName(Side.Right) }
                            : new SyncAction { Kind = ActionKind.CreateFolder, Target = Side.Right, Path = path, Reason = "New folder" });
                    else
                        plan.Actions.Add(known
                            ? new SyncAction { Kind = ActionKind.DeleteFolder, Target = Side.Right, Path = path, Reason = "Deleted in " + SideName(Side.Left) }
                            : new SyncAction { Kind = ActionKind.CreateFolder, Target = Side.Left, Path = path, Reason = "New folder" });
                    continue;
                }

                var lc = ChangeOf(l, b, Side.Left, o);
                var rc = ChangeOf(r, b, Side.Right, o);

                if (l != null && r != null)
                {
                    if (o.Same(l, r)) continue;
                    if (lc == Change.Changed && rc == Change.Unchanged)
                        copies.Add(new SyncAction { Kind = ActionKind.Update, Target = Side.Right, Path = path, Size = l.Size, Reason = "Changed in " + SideName(Side.Left) });
                    else if (rc == Change.Changed && lc == Change.Unchanged)
                        copies.Add(new SyncAction { Kind = ActionKind.Update, Target = Side.Left, Path = path, Size = r.Size, Reason = "Changed in " + SideName(Side.Right) });
                    else
                        conflicts.Add(path);
                }
                else if (l != null)
                {
                    if (lc == Change.New) newLeft.Add(l);
                    else if (lc == Change.Unchanged) deleteLeft.Add(new SyncAction { Kind = ActionKind.Delete, Target = Side.Left, Path = path, Size = l.Size, Reason = "Deleted in " + SideName(Side.Right) });
                    else copies.Add(new SyncAction { Kind = ActionKind.Copy, Target = Side.Right, Path = path, Size = l.Size, Reason = $"Changed in {SideName(Side.Left)} after it was deleted in {SideName(Side.Right)}: the change is kept" });
                }
                else if (r != null)
                {
                    if (rc == Change.New) newRight.Add(r);
                    else if (rc == Change.Unchanged) deleteRight.Add(new SyncAction { Kind = ActionKind.Delete, Target = Side.Right, Path = path, Size = r.Size, Reason = "Deleted in " + SideName(Side.Left) });
                    else copies.Add(new SyncAction { Kind = ActionKind.Copy, Target = Side.Left, Path = path, Size = r.Size, Reason = $"Changed in {SideName(Side.Right)} after it was deleted in {SideName(Side.Left)}: the change is kept" });
                }
            }

            if (o.DetectRenames && !record.IsEmpty)
            {
                // Renamed on the left: the old name is still on the right (and would be deleted there).
                ApplyRenames(plan, newLeft, deleteRight, left, right, record, Side.Left, Side.Right, o);
                ApplyRenames(plan, newRight, deleteLeft, right, left, record, Side.Right, Side.Left, o);
            }

            foreach (var l in newLeft) copies.Add(new SyncAction { Kind = ActionKind.Copy, Target = Side.Right, Path = l.Path, Size = l.Size, Reason = "New in " + SideName(Side.Left) });
            foreach (var r in newRight) copies.Add(new SyncAction { Kind = ActionKind.Copy, Target = Side.Left, Path = r.Path, Size = r.Size, Reason = "New in " + SideName(Side.Right) });
            plan.Actions.AddRange(copies);
            plan.Actions.AddRange(deleteLeft);
            plan.Actions.AddRange(deleteRight);

            // Changed on both sides: keep both. The newer copy keeps the name on both sides; the other gets "(conflict date)".
            var taken = new HashSet<string>(left.Items.Keys.Concat(right.Items.Keys), Paths.Comparer);
            foreach (var path in conflicts)
            {
                var l = left.Get(path);
                var r = right.Get(path);
                var newer = l.Mtime >= r.Mtime ? Side.Left : Side.Right;
                var older = newer == Side.Left ? Side.Right : Side.Left;
                var olderEntry = older == Side.Left ? l : r;
                var newerEntry = newer == Side.Left ? l : r;
                var conflictPath = ConflictName(path, o.Now, taken);
                taken.Add(conflictPath);
                var why = record.Get(path) == null ? "Different files with the same name on both sides" : "Changed on both sides";
                plan.Actions.Add(new SyncAction { Kind = ActionKind.Rename, Target = older, From = path, Path = conflictPath, Size = olderEntry.Size, Conflict = true, Reason = why + $": the older copy (in {SideName(older)}) is kept as \"{Paths.Name(conflictPath)}\"" });
                plan.Actions.Add(new SyncAction { Kind = ActionKind.Copy, Target = older, Path = path, Size = newerEntry.Size, Conflict = true, Reason = why + ": the newer copy keeps the name" });
                plan.Actions.Add(new SyncAction { Kind = ActionKind.Copy, Target = newer, Path = conflictPath, Size = olderEntry.Size, Conflict = true, Reason = why + ": the older copy, kept on both sides" });
            }
        }

        /// <summary>Turns a deleted file and a new one with the same id (or size and time) into a rename.</summary>
        private static void ApplyRenames(Plan plan, List<Entry> newFiles, List<SyncAction> otherDeletes, Tree sourceTree, Tree otherTree,
            SyncRecord record, Side sourceSide, Side otherSide, PlanOptions o)
        {
            var candidates = otherDeletes.Select(d => otherTree.Get(d.Path)).Where(e => e != null).ToList();
            foreach (var match in MatchRenames(newFiles, candidates, sourceTree, record, sourceSide, otherSide, o))
            {
                plan.Actions.Add(new SyncAction { Kind = ActionKind.Rename, Target = otherSide, From = match.Value.Path, Path = match.Key.Path, Size = match.Key.Size, Reason = "Renamed or moved in " + SideName(sourceSide) });
                newFiles.Remove(match.Key);
                otherDeletes.RemoveAll(d => d.Path.Equals(match.Value.Path, Paths.Comparison));
            }
        }

        /// <summary>What happened to a file since last time, on one side.</summary>
        private static Change ChangeOf(Entry e, RecordEntry b, Side side, PlanOptions o)
        {
            if (e == null) return b != null ? Change.Deleted : Change.None;
            if (b == null || b.IsDir) return Change.New;
            return SameAsRecord(e, b, side, o.Tolerance, o.HourShift) ? Change.Unchanged : Change.Changed;
        }

        /// <summary>A side's name.</summary>
        public static string SideName(Side side) => side == Side.Left ? "Folder 1" : "Folder 2";

        /// <summary>"Report (conflict 2026-10-24).docx", with a number added if that's taken too.</summary>
        public static string ConflictName(string path, DateTime when, ISet<string> taken)
        {
            var folder = Paths.Parent(path);
            var name = Paths.Name(path);
            var dot = name.LastIndexOf('.');
            var stem = dot > 0 ? name.Substring(0, dot) : name;
            var ext = dot > 0 ? name.Substring(dot) : string.Empty;
            var date = when.ToString("yyyy-MM-dd");
            for (var n = 1; ; n++)
            {
                var candidate = Paths.Join(folder, $"{stem} (conflict {date}{(n > 1 ? " " + n : string.Empty)}){ext}");
                if (!taken.Contains(candidate)) return candidate;
            }
        }
    }
}
