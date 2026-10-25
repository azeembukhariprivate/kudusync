using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization.Formatters.Binary;
using Kudusync.Core;

namespace Kudusync.Tests
{
    public static class Program
    {
        private static int _failed;
        private static int _passed;
        private static string _root;

        public static int Main()
        {
            _root = Path.Combine(Path.GetTempPath(), "kudusync-tests-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(_root);
            DataStore.OverrideFolder = Path.Combine(_root, "data");
            try
            {
                foreach (var m in typeof(Program).GetMethods(BindingFlags.NonPublic | BindingFlags.Static).Where(m => m.Name.StartsWith("Test")))
                {
                    try
                    {
                        m.Invoke(null, null);
                        _passed++;
                        Console.WriteLine("PASS " + m.Name);
                    }
                    catch (TargetInvocationException ex)
                    {
                        _failed++;
                        Console.WriteLine("FAIL " + m.Name + ": " + ex.InnerException);
                    }
                }
            }
            finally
            {
                try { Directory.Delete(_root, true); }
                catch { /* ignore */ }
            }
            Console.WriteLine($"{_passed} passed, {_failed} failed");
            return _failed == 0 ? 0 : 1;
        }

        // ================================================================== helpers

        private static void Check(bool condition, string what)
        {
            if (!condition) throw new Exception("Check failed: " + what);
        }

        private static void Equal<T>(T expected, T actual, string what)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"{what}: expected {expected}, got {actual}");
        }

        private static readonly DateTime Base = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        private static string NewDir(string name)
        {
            var dir = Path.Combine(_root, name + "-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static void Put(string root, string relative, string content, int minutes = 0)
        {
            var path = Path.Combine(root, relative.Replace('\\', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, content);
            File.SetLastWriteTimeUtc(path, Base.AddMinutes(minutes));
        }

        private static string Read(string root, string relative)
        {
            var path = Path.Combine(root, relative.Replace('\\', Path.DirectorySeparatorChar));
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }

        private static bool Has(string root, string relative) => File.Exists(Path.Combine(root, relative.Replace('\\', Path.DirectorySeparatorChar)));
        private static bool HasDir(string root, string relative) => Directory.Exists(Path.Combine(root, relative.Replace('\\', Path.DirectorySeparatorChar)));

        private static FolderPair Pair(string left, string right, SyncMode mode, string name = null) => new FolderPair
        {
            Name = name ?? "Test " + mode,
            Left = new Location { Path = left },
            Right = new Location { Path = right },
            Mode = mode
        };

        private static RunResult Run(FolderPair pair, Action<Plan> check = null)
        {
            using (var s = SyncSession.Open(pair, Triggers.Manual))
            {
                s.Scan();
                var plan = s.BuildPlan(new DateTime(2026, 10, 24));
                check?.Invoke(plan);
                return s.Execute();
            }
        }

        private static Plan PlanOnly(FolderPair pair)
        {
            using (var s = SyncSession.Open(pair, Triggers.Manual))
            {
                s.Scan();
                return s.BuildPlan(new DateTime(2026, 10, 24));
            }
        }

        private static List<string> HistoryFiles(string root) =>
            Directory.Exists(Path.Combine(root, Paths.HistoryName))
                ? Directory.GetFiles(Path.Combine(root, Paths.HistoryName), "*", SearchOption.AllDirectories).Select(Path.GetFileName).OrderBy(n => n).ToList()
                : new List<string>();

        // ================================================================== Mirror, Update, Move

        private static void TestMirrorCopiesUpdatesAndDeletes()
        {
            var l = NewDir("l");
            var r = NewDir("r");
            Put(l, "a.txt", "new a", 10);
            Put(l, @"sub\b.txt", "b");
            Put(l, @"sub\deep\c.txt", "c");
            Directory.CreateDirectory(Path.Combine(l, "empty"));
            Put(r, "a.txt", "old a", 0);
            Put(r, "x.txt", "extra");
            Put(r, @"gone\y.txt", "y");

            var result = Run(Pair(l, r, SyncMode.Mirror), plan =>
            {
                Equal(2, plan.NewFiles, "new files");
                Equal(1, plan.ChangedFiles, "changed");
                Equal(2, plan.DeletedFiles, "deleted");
            });
            Equal(RunStatus.Ok, result.Status, "status");
            Equal("new a", Read(r, "a.txt"), "updated a");
            Equal("c", Read(r, @"sub\deep\c.txt"), "deep copy");
            Check(HasDir(r, "empty"), "empty folder copied");
            Check(!Has(r, "x.txt") && !HasDir(r, "gone"), "extras removed");
            Equal(Base.AddMinutes(10), File.GetLastWriteTimeUtc(Path.Combine(r, "a.txt")), "modified time kept");
            var kept = HistoryFiles(r);
            Check(kept.SequenceEqual(new[] { "a.txt", "x.txt", "y.txt" }), "old versions kept: " + string.Join(",", kept));
            Equal("old a", File.ReadAllText(Directory.GetFiles(Path.Combine(r, Paths.HistoryName), "a.txt", SearchOption.AllDirectories)[0]), "old a kept");
            Check(!Directory.GetFiles(r, "*" + Paths.TempSuffix, SearchOption.AllDirectories).Any(), "no temp files");

            var again = PlanOnly(Pair(l, r, SyncMode.Mirror));
            Check(again.IsEmpty, "second run has nothing to do: " + string.Join("; ", again.Actions));
        }

        private static void TestUpdateNeverDeletes()
        {
            var l = NewDir("l");
            var r = NewDir("r");
            Put(l, "a.txt", "a");
            Put(r, "keep.txt", "mine");
            var result = Run(Pair(l, r, SyncMode.Update));
            Equal(1, result.Copied, "copied");
            Equal(0, result.Deleted, "deleted");
            Check(Has(r, "keep.txt") && Has(r, "a.txt"), "both there");
        }

        private static void TestMoveEmptiesSource()
        {
            var l = NewDir("l");
            var r = NewDir("r");
            Put(l, @"cam\1.jpg", "one");
            Put(l, @"cam\2.jpg", "two");
            Put(l, "same.txt", "same", 5);
            Put(r, "same.txt", "same", 5);
            Directory.CreateDirectory(Path.Combine(l, "was-empty"));
            var result = Run(Pair(l, r, SyncMode.Move));
            Equal(3, result.Moved, "moved");
            Check(Has(r, @"cam\1.jpg") && Has(r, @"cam\2.jpg"), "in destination");
            Check(!Has(l, @"cam\1.jpg") && !Has(l, "same.txt"), "removed from source");
            Check(!HasDir(l, "cam"), "emptied folder removed");
            Check(HasDir(l, "was-empty"), "folder that was already empty stays");
            Check(Directory.Exists(l), "source folder itself stays");
            Check(!File.Exists(DataStore.RecordFile(Pair(l, r, SyncMode.Move).Id)), "no record for Move");
        }

        private static void TestFiltersAreLeftAlone()
        {
            var l = NewDir("l");
            var r = NewDir("r");
            Put(l, "doc.txt", "d");
            Put(l, "skip.tmp", "t");
            Put(l, @"node_modules\x.js", "x");
            Put(l, @"Projects\bin\out.dll", "o");
            Put(l, @"Projects\src\a.cs", "a");
            Put(r, "dest-only.tmp", "excluded in the destination too");
            var pair = Pair(l, r, SyncMode.Mirror);
            pair.Filters.ExcludeFolders = @"node_modules; Projects\bin";
            Run(pair);
            Check(Has(r, "doc.txt") && Has(r, @"Projects\src\a.cs"), "included copied");
            Check(!Has(r, "skip.tmp") && !HasDir(r, "node_modules") && !HasDir(r, @"Projects\bin"), "excluded not copied");
            Check(Has(r, "dest-only.tmp"), "excluded file in the destination isn't deleted");

            var only = Pair(l, NewDir("r2"), SyncMode.Mirror);
            only.Filters.Include = "*.cs; *.txt";
            var plan = PlanOnly(only);
            Check(plan.Actions.Where(a => a.IsCopy).Select(a => Paths.Name(a.Path)).OrderBy(n => n).SequenceEqual(new[] { "a.cs", "doc.txt" }), "include patterns");
        }

        private static void TestRenameDetectedInMirror()
        {
            var l = NewDir("l");
            var r = NewDir("r");
            Put(l, @"Videos\holiday.mp4", new string('v', 5000), 3);
            Put(l, "other.txt", "o");
            var pair = Pair(l, r, SyncMode.Mirror);
            Run(pair);

            Directory.CreateDirectory(Path.Combine(l, "Archive"));
            File.Move(Path.Combine(l, "Videos", "holiday.mp4"), Path.Combine(l, "Archive", "Holiday 2026.mp4"));
            Directory.Delete(Path.Combine(l, "Videos"));
            var result = Run(pair, plan =>
            {
                Equal(1, plan.Renamed, "renamed");
                Equal(0, plan.NewFiles, "nothing copied");
                Equal(0, plan.DeletedFiles, "nothing deleted");
            });
            Equal(1, result.Renamed, "renamed result");
            Check(Has(r, @"Archive\Holiday 2026.mp4") && !Has(r, @"Videos\holiday.mp4"), "renamed in destination");
            Check(!HasDir(r, "Videos"), "old empty folder removed");
            Equal(0, HistoryFiles(r).Count, "nothing went to history");
        }

        private static void TestRenameIgnoredWhenAmbiguous()
        {
            var l = NewDir("l");
            var r = NewDir("r");
            Put(l, "a.bin", new string('1', 2000), 1);
            Put(l, "b.bin", new string('2', 2000), 1);
            var pair = Pair(l, r, SyncMode.Mirror);
            Run(pair);
            File.Move(Path.Combine(l, "a.bin"), Path.Combine(l, "c.bin"));
            File.Move(Path.Combine(l, "b.bin"), Path.Combine(l, "d.bin"));
            var plan = PlanOnly(pair);
            if (Paths.IsWindows)
            {
                // NTFS file IDs tell the two files apart, so both renames are certain.
                Equal(2, plan.Renamed, "renames proven by file ID");
                Check(plan.Actions.Any(a => a.Kind == ActionKind.Rename && a.From == "a.bin" && a.Path == "c.bin"), "matched to the right file");
            }
            else
            {
                Equal(0, plan.Renamed, "ambiguous renames aren't guessed");
                Equal(2, plan.NewFiles, "copied instead");
            }
        }

        private static void TestCaseOnlyRename()
        {
            var left = new Tree();
            var right = new Tree();
            left.Add(new Entry { Path = "Photo.JPG", Size = 3, Mtime = 100 });
            right.Add(new Entry { Path = "photo.jpg", Size = 3, Mtime = 100 });
            var plan = Planner.Build(left, right, null, new PlanOptions { Mode = SyncMode.Mirror });
            Equal(1, plan.Actions.Count, "one action");
            Equal(ActionKind.Rename, plan.Actions[0].Kind, "rename");
            Equal("photo.jpg", plan.Actions[0].From, "from");
        }

        private static void TestClockChangeOnFatDrives()
        {
            var hour = TimeSpan.TicksPerHour;
            var two = TimeSpan.FromSeconds(2).Ticks;
            Check(Planner.TimesMatch(1000, 1000 + hour, two, true), "exactly an hour apart, on FAT");
            Check(Planner.TimesMatch(1000, 1000 + hour + two, two, true), "an hour and two seconds (FAT's 2-second steps)");
            Check(!Planner.TimesMatch(1000, 1000 + hour, two, false), "an hour apart elsewhere is a change");
            Check(!Planner.TimesMatch(1000, 1000 + 2 * hour, two, true), "two hours apart is a change");
            Check(!Planner.TimesMatch(1000, 1000 + 30 * TimeSpan.TicksPerMinute, two, true), "half an hour apart is a change");

            // After the clocks change, every file on a FAT drive looks an hour off: nothing is copied again.
            var left = new Tree();
            var right = new Tree();
            for (var i = 0; i < 5; i++)
            {
                left.Add(new Entry { Path = $"photo{i}.jpg", Size = 5000 + i, Mtime = Base.Ticks });
                right.Add(new Entry { Path = $"photo{i}.jpg", Size = 5000 + i, Mtime = Base.Ticks + hour });
            }
            right.Add(new Entry { Path = "edited.jpg", Size = 7, Mtime = Base.Ticks + hour });
            left.Add(new Entry { Path = "edited.jpg", Size = 8, Mtime = Base.Ticks + 2 * hour }); // really changed: different size
            Equal(6, Planner.Build(left, right, null, new PlanOptions { Mode = SyncMode.Mirror }).ChangedFiles, "without the allowance every file is copied");
            var plan = Planner.Build(left, right, null, new PlanOptions { Mode = SyncMode.Mirror, HourShift = true });
            Equal(1, plan.ChangedFiles, "with the allowance only the edited file is copied");
            Equal("edited.jpg", plan.Actions.Single(a => a.Kind == ActionKind.Update).Path, "the edited file");

            // Two-way: the record was made before the clocks changed; both sides shifted, nothing changed.
            var record = RecordOfPhotos(left, hour);
            var twoWay = Planner.Build(Shift(left, hour), Shift(right, hour), record, new PlanOptions { Mode = SyncMode.TwoWay, HourShift = true });
            Equal(0, twoWay.Actions.Count(a => a.Path.StartsWith("photo")), "two-way leaves shifted files alone");
        }

        private static void TestCompareContents()
        {
            var l = NewDir("l");
            var r = NewDir("r");
            Put(l, "same.txt", "hello", 0);
            Put(r, "same.txt", "hello", 0);
            Put(l, "sneaky.txt", "AAAA", 0); // same size and time, different bytes
            Put(r, "sneaky.txt", "BBBB", 0);

            var pair = Pair(l, r, SyncMode.Mirror);
            Equal(0, PlanOnly(pair).ChangedFiles, "size and time alone see no change");
            pair.CompareContents = true;
            var result = Run(pair, plan =>
            {
                Equal(1, plan.ChangedFiles, "contents compared");
                Equal("sneaky.txt", plan.Actions.Single(a => a.Kind == ActionKind.Update).Path, "the changed file");
            });
            Equal(RunStatus.Ok, result.Status, "status");
            Equal("AAAA", Read(r, "sneaky.txt"), "copied");

            // Two-way: neither side can be said to be the changed one, so both copies are kept.
            var l2 = NewDir("l");
            var r2 = NewDir("r");
            Put(l2, "notes.txt", "left", 0);
            Put(r2, "notes.txt", "rite", 0);
            var two = Pair(l2, r2, SyncMode.TwoWay);
            two.CompareContents = true;
            Equal(1, PlanOnly(two).Conflicts, "two-way keeps both");

            Check(Paths.SameContents(Path.Combine(l, "same.txt"), Path.Combine(r, "same.txt")), "same bytes");
            Check(!Paths.SameContents(Path.Combine(l, "same.txt"), Path.Combine(l, "sneaky.txt")), "different sizes");
        }

        private static Tree Shift(Tree tree, long by)
        {
            var copy = new Tree();
            foreach (var e in tree.Items.Values) copy.Add(new Entry { Path = e.Path, Size = e.Size, Mtime = e.Mtime + by, IsDir = e.IsDir });
            return copy;
        }

        private static SyncRecord RecordOfPhotos(Tree left, long hour)
        {
            // A record of the photos as both sides were at the last sync (the right side an hour ahead, as above).
            var record = new SyncRecord();
            foreach (var e in left.Items.Values.Where(e => e.Path.StartsWith("photo")))
                record.Items[e.Path] = new RecordEntry { Path = e.Path, Size = e.Size, LeftMtime = e.Mtime, RightMtime = e.Mtime + hour };
            return record;
        }

        // ================================================================== Two-way

        private static void TestTwoWayFirstRunMerges()
        {
            var l = NewDir("l");
            var r = NewDir("r");
            Put(l, "only-left.txt", "L");
            Put(r, "only-right.txt", "R");
            Put(l, "both.txt", "left version", 1);
            Put(r, "both.txt", "right version!", 2);
            var result = Run(Pair(l, r, SyncMode.TwoWay), plan => Equal(0, plan.DeletedFiles, "first run never deletes"));
            Check(Has(l, "only-right.txt") && Has(r, "only-left.txt"), "merged");
            Equal(1, result.Conflicts, "conflict");
            var conflict = "both (conflict 2026-10-24).txt";
            Equal("right version!", Read(l, "both.txt"), "newer keeps the name on the left");
            Equal("right version!", Read(r, "both.txt"), "newer keeps the name on the right");
            Equal("left version", Read(l, conflict), "older kept on the left");
            Equal("left version", Read(r, conflict), "older kept on the right");
        }

        private static void TestTwoWayPropagatesChangesAndDeletes()
        {
            var l = NewDir("l");
            var r = NewDir("r");
            Put(l, "a.txt", "a");
            Put(l, "b.txt", "b");
            Put(l, @"dir\c.txt", "c");
            var pair = Pair(l, r, SyncMode.TwoWay);
            Run(pair);

            File.Delete(Path.Combine(l, "a.txt"));
            Put(r, "b.txt", "b changed on the right", 30);
            File.Delete(Path.Combine(r, "dir", "c.txt"));
            Directory.Delete(Path.Combine(r, "dir"));
            Put(r, "new-right.txt", "n");

            var result = Run(pair, plan =>
            {
                Equal(2, plan.DeletedFiles, "two deletes");
                Equal(1, plan.ChangedFiles, "one change");
                Equal(1, plan.NewFiles, "one new");
                Equal(0, plan.Conflicts, "no conflicts");
            });
            Equal(RunStatus.Ok, result.Status, "status");
            Check(!Has(r, "a.txt"), "delete on left reaches the right");
            Check(!Has(l, @"dir\c.txt") && !HasDir(l, "dir"), "folder deleted on right is removed on the left");
            Equal("b changed on the right", Read(l, "b.txt"), "change on right reaches the left");
            Check(Has(l, "new-right.txt"), "new file reaches the left");
            Check(HistoryFiles(r).Contains("a.txt") && HistoryFiles(l).Contains("b.txt") && HistoryFiles(l).Contains("c.txt"), "old versions kept on both sides");
            Check(PlanOnly(pair).IsEmpty, "in step afterwards");
        }

        private static void TestTwoWayChangeBeatsDelete()
        {
            var l = NewDir("l");
            var r = NewDir("r");
            Put(l, "doc.txt", "v1");
            var pair = Pair(l, r, SyncMode.TwoWay);
            Run(pair);
            Put(l, "doc.txt", "v2 longer", 9);
            File.Delete(Path.Combine(r, "doc.txt"));
            Run(pair);
            Equal("v2 longer", Read(r, "doc.txt"), "the changed file comes back");
        }

        private static void TestTwoWayRenameOnRight()
        {
            var l = NewDir("l");
            var r = NewDir("r");
            Put(l, "report.docx", new string('r', 3000), 4);
            var pair = Pair(l, r, SyncMode.TwoWay);
            Run(pair);
            File.Move(Path.Combine(r, "report.docx"), Path.Combine(r, "Final report.docx"));
            var result = Run(pair, plan => Equal(1, plan.Renamed, "rename"));
            Check(Has(l, "Final report.docx") && !Has(l, "report.docx"), "renamed on the left");
            Equal(0, result.Copied, "nothing copied");
        }

        private static void TestTwoWayMissingFolderIsRefused()
        {
            var l = NewDir("l");
            var r = NewDir("r");
            Put(l, "a.txt", "a");
            var pair = Pair(l, r, SyncMode.TwoWay);
            Run(pair);
            Directory.Delete(r, true);
            try
            {
                SyncSession.Open(pair, Triggers.Manual).Dispose();
                throw new Exception("should have refused");
            }
            catch (SyncProblem p)
            {
                Check(p.Message.Contains("wasn't found"), "message: " + p.Message);
            }
            Check(Has(l, "a.txt"), "nothing deleted");
        }

        // ================================================================== safety and plumbing

        private static void TestEmptySourceNeedsConfirmation()
        {
            var l = NewDir("l");
            var r = NewDir("r");
            for (var i = 0; i < 5; i++) Put(r, $"f{i}.txt", "x");
            var pair = Pair(l, r, SyncMode.Mirror);
            using (var s = SyncSession.Open(pair, Triggers.Manual))
            {
                s.Scan();
                s.BuildPlan();
                Check(s.SafetyCheck().Any(i => i.NeedsConfirmation), "empty source flagged");
            }
            var result = Runner.RunUnattended(pair, Triggers.Scheduled);
            Equal(RunStatus.Skipped, result.Status, "unattended run pauses");
            Check(result.Message.StartsWith("Paused"), "message");
            Equal(5, Directory.GetFiles(r).Length, "nothing deleted");
        }

        private static void TestMassDeletePaused()
        {
            var l = NewDir("l");
            var r = NewDir("r");
            for (var i = 0; i < 30; i++) Put(l, $"f{i}.txt", "x");
            var pair = Pair(l, r, SyncMode.Mirror);
            Run(pair);
            for (var i = 0; i < 20; i++) File.Delete(Path.Combine(l, $"f{i}.txt"));
            using (var s = SyncSession.Open(pair, Triggers.Manual))
            {
                s.Scan();
                s.BuildPlan();
                Check(s.SafetyCheck(true).Any(i => i.NeedsConfirmation && i.Message.Contains("more than half")), "mass delete flagged");
                Check(!s.SafetyCheck(false).Any(i => i.NeedsConfirmation), "not flagged when turned off");
            }
        }

        private static void TestOverlappingFoldersRefused()
        {
            var l = NewDir("l");
            var inner = Path.Combine(l, "backup");
            Directory.CreateDirectory(inner);
            try
            {
                SyncSession.Open(Pair(l, inner, SyncMode.Mirror), Triggers.Manual).Dispose();
                throw new Exception("should have refused");
            }
            catch (SyncProblem p)
            {
                Check(p.Message.Contains("inside"), p.Message);
            }
        }

        private static void TestOnlyOneRunAtATime()
        {
            var pair = Pair(NewDir("l"), NewDir("r"), SyncMode.Mirror);
            using (SyncSession.Open(pair, Triggers.Manual))
            {
                try
                {
                    SyncSession.Open(pair, Triggers.Manual).Dispose();
                    throw new Exception("second run allowed");
                }
                catch (SyncProblem p)
                {
                    Check(p.Message.Contains("already running"), p.Message);
                }
            }
            SyncSession.Open(pair, Triggers.Manual).Dispose();
        }

        private static void TestUnreadableFolderIsProtected()
        {
            var left = new Tree();
            var right = new Tree();
            left.Add(new Entry { Path = "Docs", IsDir = true });
            left.Unreadable.Add("Docs");
            right.Add(new Entry { Path = "Docs", IsDir = true });
            right.Add(new Entry { Path = @"Docs\a.txt", Size = 1 });
            right.Add(new Entry { Path = "other.txt", Size = 1 });
            var plan = Planner.Build(left, right, null, new PlanOptions { Mode = SyncMode.Mirror });
            Check(plan.Actions.All(a => !a.Path.StartsWith("Docs")), "nothing under an unreadable folder is touched");
            Check(plan.Actions.Any(a => a.Path == "other.txt" && a.Kind == ActionKind.Delete), "the rest still syncs");
        }

        private static void TestUntickedActionIsRetried()
        {
            var l = NewDir("l");
            var r = NewDir("r");
            Put(l, "a.txt", "a");
            Put(l, "b.txt", "b");
            var pair = Pair(l, r, SyncMode.TwoWay);
            using (var s = SyncSession.Open(pair, Triggers.Manual))
            {
                s.Scan();
                var plan = s.BuildPlan();
                plan.Actions.First(a => a.Path == "b.txt").Enabled = false;
                s.Execute();
            }
            Check(Has(r, "a.txt") && !Has(r, "b.txt"), "only the ticked one ran");
            var again = PlanOnly(pair);
            Check(again.Actions.Any(a => a.Path == "b.txt" && a.Kind == ActionKind.Copy), "unticked step comes back next time (not a delete)");
        }

        private static void TestLeftoverTempRemovedAndVerify()
        {
            var l = NewDir("l");
            var r = NewDir("r");
            Put(l, "big.bin", new string('z', 3 * 1024 * 1024 + 17));
            Put(r, "big.bin" + Paths.TempSuffix, "half-written");
            var pair = Pair(l, r, SyncMode.Mirror);
            pair.Verify = true;
            var result = Run(pair);
            Equal(RunStatus.Ok, result.Status, "status");
            Equal(new FileInfo(Path.Combine(l, "big.bin")).Length, new FileInfo(Path.Combine(r, "big.bin")).Length, "full copy");
            Check(!Has(r, "big.bin" + Paths.TempSuffix), "leftover removed");
        }

        private static void TestNoHistoryMeansDelete()
        {
            var l = NewDir("l");
            var r = NewDir("r");
            Put(r, "old.txt", "o");
            Put(l, "new.txt", "n");
            var pair = Pair(l, r, SyncMode.Mirror);
            pair.KeepDays = -1;
            Run(pair);
            Check(!Has(r, "old.txt") && !HasDir(r, Paths.HistoryName), "deleted permanently");
        }

        private static void TestCustomHistoryFolderAndCleanup()
        {
            var l = NewDir("l");
            var r = NewDir("r");
            var h = NewDir("h");
            Put(l, "a.txt", "new", 5);
            Put(r, "a.txt", "old", 1);
            var pair = Pair(l, r, SyncMode.Mirror, "My backup");
            pair.HistoryFolder = h;
            pair.KeepDays = 7;
            Run(pair);
            var folder = Path.Combine(h, "My backup");
            Equal(1, Directory.GetFiles(folder, "a.txt", SearchOption.AllDirectories).Length, "kept in the chosen folder");
            Directory.CreateDirectory(Path.Combine(folder, "2020-01-01 101010"));
            Directory.CreateDirectory(Path.Combine(folder, "not a run"));
            Equal(1, History.Cleanup(pair, r, Side.Right, DateTime.Now), "old run removed");
            Check(Directory.Exists(Path.Combine(folder, "not a run")), "other folders left alone");
        }

        private static void TestRecordRoundTrip()
        {
            var record = new SyncRecord { LeftRoot = "L", RightRoot = "R" };
            record.Items["a\\b.txt"] = new RecordEntry { Path = "a\\b.txt", Size = 5, LeftMtime = 1, RightMtime = 2, LeftId = 3, RightId = 4 };
            record.Items["a"] = new RecordEntry { Path = "a", IsDir = true };
            var file = Path.Combine(_root, "rec.db");
            record.Save(file);
            var back = SyncRecord.Load(file);
            Equal(2, back.Items.Count, "count");
            Equal(4L, back.Get("A\\B.TXT").RightId, "case-insensitive lookup");
            Equal("R", back.RightRoot, "roots");
            File.WriteAllText(file, "garbage");
            Check(SyncRecord.Load(file).IsEmpty, "damaged record is ignored");
        }

        private static void TestPairsSaveAndLoad()
        {
            var pair = Pair(@"C:\a", @"\\nas\share\b", SyncMode.TwoWay, "Photos");
            pair.Schedule.Kind = ScheduleKind.Weekly;
            pair.Filters.Exclude = "*.bak";
            DataStore.SavePairs(new[] { pair });
            var back = DataStore.LoadPairs().Single();
            Equal("Photos", back.Name, "name");
            Equal(SyncMode.TwoWay, back.Mode, "mode");
            Equal(ScheduleKind.Weekly, back.Schedule.Kind, "schedule");
            Equal("*.bak", back.Filters.Exclude, "filters");
            Check(back.Right.IsNetwork, "network");
            Equal(30, back.LiveDelaySeconds, "defaults survive");

            File.WriteAllText(DataStore.PairsFile, "[{\"Id\":\"x\",\"Name\":\"Old\"}]");
            var old = DataStore.LoadPairs().Single();
            Check(old.DetectRenames && old.InRunAll && old.KeepDays == 30 && old.Filters.Include == "*", "missing settings get defaults");
            DataStore.SavePairs(new FolderPair[0]);
        }

        private static void TestRunLog()
        {
            var l = NewDir("l");
            var r = NewDir("r");
            Put(l, "a.txt", "a");
            var pair = Pair(l, r, SyncMode.Mirror, "Logged");
            var result = Run(pair);
            var latest = RunLog.Latest(RunLog.All(), pair.Id);
            Equal(result.Started, latest.Started, "logged");
            Check(File.Exists(latest.LogFile) && File.ReadAllText(latest.LogFile).Contains("Copied in Destination: a.txt"), "text log");
            Equal("1 copied · 1 B", latest.Summary, "summary");
        }

        private static void TestPaths()
        {
            Equal(@"\\nas\photos", Paths.ShareRoot(@"\\nas\photos\2026\jan"), "share root");
            Check(Paths.IsUnc(@"\\nas\x") && !Paths.IsUnc(@"C:\x") && !Paths.IsUnc(@"\\?\C:\x"), "unc");
            Equal("E:", Paths.DriveOf(@"e:\stuff"), "drive");
            Equal("Pair_ x", Paths.SafeName("Pair: x"), "safe name");
            Check(Paths.Overlap(_root, Path.Combine(_root, "x")), "nested overlaps");
            Check(!Paths.Overlap(Path.Combine(_root, "ab"), Path.Combine(_root, "abc")), "prefix isn't overlap");
            Equal("x" + Path.DirectorySeparatorChar + "y", Paths.Relative(_root, Path.Combine(_root, "x", "y")).Replace('\\', Path.DirectorySeparatorChar), "relative");
            var taken = new HashSet<string>(Paths.Comparer) { @"d\a (conflict 2026-10-24).txt" };
            Equal(@"d\a (conflict 2026-10-24 2).txt", Planner.ConflictName(@"d\a.txt", new DateTime(2026, 10, 24), taken), "conflict name");
            Equal("Makefile (conflict 2026-10-24)", Planner.ConflictName("Makefile", new DateTime(2026, 10, 24), new HashSet<string>()), "no extension");
        }

        private static void TestWildcards()
        {
            var f = new FileFilter(new Filters { Include = "*.jpg; IMG_????.png", Exclude = "~$*", SkipHidden = true });
            Check(f.IncludeFile("a.JPG", "a.JPG", FileAttributes.Normal), "case-insensitive");
            Check(f.IncludeFile("IMG_0001.png", "IMG_0001.png", FileAttributes.Normal), "? pattern");
            Check(!f.IncludeFile("IMG_01.png", "IMG_01.png", FileAttributes.Normal), "? needs exactly one");
            Check(!f.IncludeFile("x.jpg", "x.jpg", FileAttributes.Hidden), "hidden skipped");
            Check(!f.IncludeFolder(Paths.HistoryName, Paths.HistoryName, FileAttributes.Directory), "history folder always skipped");
            Check(f.IncludeFolder(@"x\" + Paths.HistoryName, Paths.HistoryName, FileAttributes.Directory), "only at the top");
        }

        private static void TestSchedules()
        {
            var pair = Pair(@"C:\a", @"D:\b", SyncMode.Mirror, "Nightly");
            pair.Schedule = new Schedule { Kind = ScheduleKind.Weekly, Time = "21:30", Days = (1 << 1) | (1 << 5) };
            var xml = Schedules.TaskXml(pair, @"C:\Program Files\KUDUSYNC\KUDUSYNC.exe", @"PC\azeem", new DateTime(2026, 10, 24));
            Check(xml.Contains("<StartBoundary>2026-10-24T21:30:00</StartBoundary>"), "start");
            Check(xml.Contains("<Monday /><Friday />"), "days");
            Check(xml.Contains("--run-scheduled " + pair.Id), "action");
            Check(System.Xml.Linq.XDocument.Parse(xml.Substring(xml.IndexOf("<Task"))) != null, "valid XML");
            Equal("Mon, Fri at 21:30", pair.Schedule.Describe(), "describe");
            Equal(new DateTime(2026, 10, 30, 21, 30, 0), pair.Schedule.NextAfter(new DateTime(2026, 10, 27, 9, 0, 0)), "next run (Tue → Fri)");
            pair.Schedule = new Schedule { Kind = ScheduleKind.EveryHours, Hours = 4 };
            Equal(new DateTime(2026, 10, 24, 12, 0, 0), pair.Schedule.NextAfter(new DateTime(2026, 10, 24, 9, 15, 0)), "every 4 hours");
            Check(Schedules.TaskXml(pair, "k.exe", "u", DateTime.Today).Contains("<Interval>PT4H</Interval>"), "repetition");
        }

        private static void TestSyncToyImport()
        {
            var a = new SyncToy.SyncEngineConfig("Photos", @"C:\Pictures", @"E:\Backup\Pictures", "Echo", "*.jpg", "*.tmp", FileAttributes.Hidden, true);
            var b = new SyncToy.SyncEngineConfig("Docs", @"C:\Docs", @"\\nas\docs", "Synchronize", "*", "", 0, false);
            b.exceptionalDirs = new SyncToy.ExceptionalDirList();
            b.exceptionalDirs.LeftDirectories.Add(@"C:\Docs\Temp");
            var c = new SyncToy.SyncEngineConfig("Add only", @"C:\In", @"D:\Out", "Contribute", "*", "", 0, false);
            var stream = new MemoryStream();
#pragma warning disable SYSLIB0011
            var bf = new BinaryFormatter();
            bf.Serialize(stream, a);
            bf.Serialize(stream, b);
            bf.Serialize(stream, c);
#pragma warning restore SYSLIB0011
            stream.Position = 0;
            var found = SyncToyImport.Read(stream);
            Equal(3, found.Count, "pairs found");
            Equal(SyncMode.Mirror, found[0].Mode, "echo = mirror");
            Equal(SyncMode.TwoWay, found[1].Mode, "synchronize = two-way");
            Equal(SyncMode.Update, found[2].Mode, "contribute = update");
            var photos = SyncToyImport.ToPair(found[0]);
            Equal("Photos", photos.Name, "name");
            Equal(@"E:\Backup\Pictures", found[0].Right, "right");
            Equal("*.jpg", photos.Filters.Include, "include");
            Check(photos.Filters.Exclude.Contains("*.tmp"), "exclude");
            Check(photos.Filters.SkipHidden && photos.CompareContents && !photos.Verify, "attributes, and \"check file contents\" compares contents");
            Check(found[1].ExcludedFolders.Contains(@"C:\Docs\Temp"), "excluded folders");

            try
            {
                SyncToyImport.Read(new MemoryStream(new byte[] { 1, 2, 3 }));
                throw new Exception("garbage accepted");
            }
            catch (InvalidDataException) { }
        }
    }
}

namespace SyncToy
{
    // Stand-ins with the same field names as SyncToy 2.1's settings classes, to produce a realistic file to import.
    [Serializable]
    public class ExceptionalDirList
    {
        public System.Collections.ArrayList LeftDirectories = new System.Collections.ArrayList();
        public System.Collections.ArrayList RightDirectories = new System.Collections.ArrayList();
    }

    [Serializable]
    public class SyncEngineConfig
    {
        private string name;
        private string leftDir;
        private string rightDir;
        private string searchPattern;
        private string excludedPattern;
        private string syncModeName;
        public ExceptionalDirList exceptionalDirs;
        private bool computeHash;
        private bool active = true;
        private bool backupOlderFile = true;
        private DateTime dateTimeLastSync = DateTime.Now;
        private System.IO.FileAttributes excludedFileAttributes;

        public SyncEngineConfig(string name, string left, string right, string mode, string include, string exclude, System.IO.FileAttributes attributes, bool hash)
        {
            this.name = name;
            leftDir = left;
            rightDir = right;
            syncModeName = mode;
            searchPattern = include;
            excludedPattern = exclude;
            excludedFileAttributes = attributes;
            computeHash = hash;
        }

        public override string ToString() => name + leftDir + rightDir + searchPattern + excludedPattern + syncModeName + computeHash + active + backupOlderFile + dateTimeLastSync + excludedFileAttributes;
    }
}
