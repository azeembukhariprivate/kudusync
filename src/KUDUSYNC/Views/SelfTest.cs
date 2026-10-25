using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Formatters.Binary;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Kudusync.Core;

namespace Kudusync.Views
{
    /// <summary>
    /// KUDUSYNC.exe --self-test: syncs real folders on this PC in every mode (and checks every byte), a virtual USB
    /// drive (VHD) that changes letter, a FAT32 drive, a network share, Task Scheduler, the tray's live sync and drive
    /// notifications, the right-click menu, the SyncToy import, and every screen in both themes. Needs administrator
    /// rights for the virtual drives and the share (the GitHub build has them). Writes self-test.log next to the exe and
    /// exits with 0 when everything passed. Works in a scratch folder; never touches the user's own pairs or files.
    /// </summary>
    public static class SelfTest
    {
        public static bool Running { get; private set; }
        public static List<string> Errors { get; } = new List<string>();

        private static StringBuilder _log;
        private static string _temp;
        private static readonly List<string> Vhds = new List<string>();
        private static string _usbLetter;
        private static string _usbVhd;
        private static string _share;
        private static FolderPair _usbPair;

        public static int Run()
        {
            Running = true;
            _log = new StringBuilder();
            _temp = Path.Combine(Path.GetTempPath(), "kudusync-selftest-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(_temp);
            DataStore.OverrideFolder = Path.Combine(_temp, "data");
            var savedNotify = Settings.Notify;
            Settings.Notify = NotifyMode.Problems;

            Step("Mirror, update and move on NTFS, byte for byte", EngineRound);
            Step("Two-way sync: changes, deletions, conflicts", TwoWayRound);
            Step("Renames recognised by NTFS file ID (no copying)", RenameById);
            Step("A file in use is skipped, then copied next time", LockedFile);
            Step("Paths longer than 260 characters", LongPaths);
            Step("Read-only and hidden files", Attributes);
            Step("USB drive (VHD) found again after its letter changes", UsbDriveRound);
            Step("FAT32 drive: 2-second times don't cause copies", Fat32Round);
            Step("Network share", NetworkRound);
            Step("Windows Credential Manager login", () =>
            {
                NetworkShares.SaveLogin(@"\\kudusync-test\share", @"TEST\user", "pa55 wörd");
                var login = NetworkShares.LoadLogin(@"\\kudusync-test\share\sub");
                Expect(login != null && login.User == @"TEST\user" && login.Password == "pa55 wörd", "the login is read back");
                NetworkShares.DeleteLogin(@"\\kudusync-test\share");
                Expect(NetworkShares.LoadLogin(@"\\kudusync-test\share") == null, "the login is removed");
            });
            Step("Task Scheduler: every kind of schedule, and a real scheduled run", ScheduleRound);
            Step("Tray: live sync and drive notifications", TrayRound);
            Step("Right-click menu and starting with Windows", () =>
            {
                ShellIntegration.Register();
                Expect(ShellIntegration.IsRegistered, "the menu is added");
                Expect((ShellIntegration.CommandFor("Sync") ?? string.Empty).Contains("--sync-folder \"%1\""), "folder command");
                Expect((ShellIntegration.CommandFor("Backup", true) ?? string.Empty).Contains("--new-pair \"%1\""), "drive command");
                ShellIntegration.Remove();
                Expect(!ShellIntegration.IsRegistered, "the menu is removed");
                ShellIntegration.SetStartWithWindows(true);
                Expect(ShellIntegration.StartsWithWindows, "start-up entry added");
                ShellIntegration.SetStartWithWindows(false);
                Expect(!ShellIntegration.StartsWithWindows, "start-up entry removed");
            });
            Step("Second start hands over to the open window", () =>
            {
                string[] received = null;
                using (var first = new SingleInstance("selftest"))
                {
                    Expect(first.IsFirst, "first instance");
                    first.Listen(a => received = a);
                    var t = new Thread(() =>
                    {
                        using (var second = new SingleInstance("selftest"))
                        {
                            if (second.IsFirst) throw new Exception("second start thinks it's first");
                            second.Forward(new[] { "--sync-folder", @"D:\Photos" });
                        }
                    });
                    t.Start();
                    t.Join(10000);
                    WaitFor(() => received != null, 10, "the command line to arrive");
                }
                Expect(received.Length == 2 && received[1] == @"D:\Photos", "the command line arrives intact");
            });
            Step("Import from SyncToy", SyncToyRound);

            Step("Load themes and styles", () => ThemeManager.Init(ThemeMode.Light));
            Step("Theme keys exist", () =>
            {
                foreach (var key in new[] { "WindowBg", "SurfaceBg", "SurfaceAltBg", "Text", "SubtleText", "Accent", "AccentText", "LinkText", "OkBg", "OkText", "WarnBg",
                                            "WarnText", "BadBg", "BadText", "NeutralChipBg", "SelectionBg", "OffDot", "UiFont", "PrimaryButton", "SecondaryButton",
                                            "GhostButton", "LinkButton", "Card", "OptionCard", "ChipRadio", "ChipToggle", "TableList", "IconSync", "IconArrowRight",
                                            "IconTwoWay", "IconUsb", "IconEdit", "IconMore", "IconEye", "IconImport", "IconHistory", "IconServer", "IconDrive",
                                            "IconMove", "IconWarn", "IconInfo", "IconCheck", "IconStop", "IconBack", "IconPlus", "IconFolder", "IconSettings" })
                    Application.Current.FindResource(key);
            });
            UiRound();

            Step("Remove test drives, share and tasks", Cleanup);
            Settings.Notify = savedNotify;

            _log.AppendLine(Errors.Count == 0 ? "SELF-TEST PASSED" : $"SELF-TEST FAILED ({Errors.Count})");
            try { File.WriteAllText(Path.Combine(Path.GetDirectoryName(AppInfo.ExePath) ?? ".", "self-test.log"), _log.ToString()); }
            catch { /* read-only folder */ }
            try { Directory.Delete(_temp, true); }
            catch { /* ignore */ }
            return Errors.Count == 0 ? 0 : 1;
        }

        // ================================================================== helpers

        private static void Step(string name, Action action)
        {
            var before = Errors.Count;
            var started = DateTime.UtcNow;
            try
            {
                action();
                Pump();
                if (Errors.Count > before) throw new Exception(string.Join(" | ", Errors.Skip(before)));
                _log.AppendLine($"ok    {name} ({(DateTime.UtcNow - started).TotalSeconds:0.0}s)");
            }
            catch (Exception ex)
            {
                if (Errors.Count == before) Errors.Add(name + ": " + ex.Message);
                _log.AppendLine("FAIL  " + name + ": " + ex);
            }
        }

        private static void Note(string text) => _log.AppendLine("      " + text);

        private static void Expect(bool condition, string what)
        {
            if (!condition) throw new Exception("expected " + what);
        }

        private static void WaitFor(Func<bool> condition, int seconds, string what)
        {
            var until = DateTime.UtcNow.AddSeconds(seconds);
            while (!condition())
            {
                if (DateTime.UtcNow > until) throw new Exception("timed out waiting for " + what);
                Pump();
                Thread.Sleep(100);
            }
        }

        private static string Dir(string name)
        {
            var dir = Path.Combine(_temp, name);
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static byte[] Pattern(int length, int seed)
        {
            var data = new byte[length];
            var x = (uint)seed * 2654435761u + 1;
            for (var i = 0; i < length; i++)
            {
                x ^= x << 13;
                x ^= x >> 17;
                x ^= x << 5;
                data[i] = (byte)x;
            }
            return data;
        }

        private static void Put(string root, string relative, byte[] data)
        {
            var path = Paths.Long(Path.Combine(root, relative));
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllBytes(path, data);
        }

        private static bool SameBytes(string a, string b) => File.Exists(Paths.Long(a)) && File.Exists(Paths.Long(b)) &&
                                                             File.ReadAllBytes(Paths.Long(a)).SequenceEqual(File.ReadAllBytes(Paths.Long(b)));

        private static FolderPair Pair(string name, string left, string right, SyncMode mode) => new FolderPair
        {
            Name = name,
            Left = Locations.FromPath(left),
            Right = Locations.FromPath(right),
            Mode = mode
        };

        private static RunResult Sync(FolderPair pair, Action<Plan> check = null)
        {
            using (var s = SyncSession.Open(pair, Triggers.Manual))
            {
                s.Scan();
                check?.Invoke(s.BuildPlan());
                if (check == null) s.BuildPlan();
                return s.Execute();
            }
        }

        private static Plan PlanOf(FolderPair pair)
        {
            using (var s = SyncSession.Open(pair, Triggers.Manual))
            {
                s.Scan();
                return s.BuildPlan();
            }
        }

        private static void Pump()
        {
            foreach (Window w in Application.Current.Windows) w.UpdateLayout();
            Application.Current.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
            Thread.Sleep(20);
            Application.Current.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
        }

        private static void OffScreen(Window w)
        {
            w.WindowStartupLocation = WindowStartupLocation.Manual;
            w.WindowState = WindowState.Normal;
            w.Left = -20000;
            w.Top = -20000;
            w.ShowActivated = false;
            w.ShowInTaskbar = false;
        }

        private static void ShowAndClose(Window w)
        {
            OffScreen(w);
            w.Show();
            Pump();
            w.Close();
        }

        private static (int Code, string Output) Exec(string file, string args, int timeoutMs = 120000)
        {
            var info = new ProcessStartInfo(file, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using (var p = Process.Start(info))
            {
                var output = p.StandardOutput.ReadToEndAsync();
                var error = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(timeoutMs))
                {
                    p.Kill();
                    throw new Exception(Path.GetFileName(file) + " didn't finish");
                }
                return (p.ExitCode, output.Result + error.Result);
            }
        }

        private static void Diskpart(params string[] commands)
        {
            var script = Path.Combine(_temp, "diskpart-" + Guid.NewGuid().ToString("N").Substring(0, 6) + ".txt");
            File.WriteAllLines(script, commands);
            var (code, output) = Exec("diskpart.exe", "/s \"" + script + "\"", 180000);
            if (code != 0) throw new Exception("diskpart failed (" + code + "): " + output);
        }

        private static string FreeLetter(params char[] avoid)
        {
            var used = new HashSet<char>(DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).Concat(avoid));
            return "RSTUVWXYZQPONMLKJ".First(c => !used.Contains(c)) + ":";
        }

        private static string CreateVhd(string name, string fs, string label)
        {
            var letter = FreeLetter();
            var vhd = Path.Combine(_temp, name + ".vhdx");
            Diskpart($"create vdisk file=\"{vhd}\" maximum=128 type=expandable", $"select vdisk file=\"{vhd}\"", "attach vdisk",
                "create partition primary", $"format fs={fs} quick label={label}", $"assign letter={letter[0]}");
            Vhds.Add(vhd);
            WaitFor(() => Directory.Exists(letter + "\\"), 30, "drive " + letter);
            return letter;
        }

        // ================================================================== engine on real folders

        private static void EngineRound()
        {
            var src = Dir("mirror-src");
            var dst = Dir("mirror-dst");
            Put(src, @"Photos\2026\IMG_0001.jpg", Pattern(2_500_000, 1));
            Put(src, @"Photos\2026\IMG_0002.jpg", Pattern(1_200_000, 2));
            Put(src, @"Docs\Report.docx", Pattern(40_000, 3));
            Put(src, @"Ünïcødé ファイル.txt", Encoding.UTF8.GetBytes("unicode"));
            Put(src, @"Docs\~$Report.docx", Pattern(100, 4)); // Office lock file: excluded by default
            Put(dst, @"old.txt", Pattern(10, 5));
            var pair = Pair("Mirror test", src, dst, SyncMode.Mirror);
            pair.Verify = true;
            var r = Sync(pair, plan => Expect(plan.NewFiles == 4 && plan.DeletedFiles == 1, $"4 new and 1 delete ({plan.NewFiles}, {plan.DeletedFiles})"));
            Expect(r.Status == RunStatus.Ok, "status OK: " + r.Summary + " " + string.Join("; ", r.Problems));
            foreach (var f in new[] { @"Photos\2026\IMG_0001.jpg", @"Photos\2026\IMG_0002.jpg", @"Docs\Report.docx", @"Ünïcødé ファイル.txt" })
                Expect(SameBytes(Path.Combine(src, f), Path.Combine(dst, f)), f + " copied byte for byte");
            Expect(!File.Exists(Path.Combine(dst, @"Docs\~$Report.docx")), "lock file left out");
            Expect(!File.Exists(Path.Combine(dst, "old.txt")) && Directory.GetFiles(Path.Combine(dst, Paths.HistoryName), "old.txt", SearchOption.AllDirectories).Length == 1, "deleted file kept as an old version");
            Expect((File.GetAttributes(Path.Combine(dst, Paths.HistoryName)) & FileAttributes.Hidden) != 0, "history folder is hidden");
            Expect(File.GetLastWriteTimeUtc(Path.Combine(src, @"Docs\Report.docx")) == File.GetLastWriteTimeUtc(Path.Combine(dst, @"Docs\Report.docx")), "modified time kept");
            Expect(PlanOf(pair).IsEmpty, "nothing to do the second time");

            Put(src, @"Docs\Report.docx", Pattern(41_000, 6));
            r = Sync(pair);
            Expect(r.Updated == 1 && SameBytes(Path.Combine(src, @"Docs\Report.docx"), Path.Combine(dst, @"Docs\Report.docx")), "changed file updated");

            var upd = Pair("Update test", src, Dir("update-dst"), SyncMode.Update);
            Put(Locations.Resolve(upd.Right).Root, "mine.txt", Pattern(5, 7));
            r = Sync(upd);
            Expect(File.Exists(Path.Combine(Locations.Resolve(upd.Right).Root, "mine.txt")) && r.Deleted == 0, "Update keeps files only in the destination");

            var card = Dir("move-card");
            var archive = Dir("move-archive");
            Put(card, @"DCIM\100\A.jpg", Pattern(300_000, 8));
            Put(card, @"DCIM\100\B.jpg", Pattern(310_000, 9));
            var a = Pattern(300_000, 8);
            r = Sync(Pair("Move test", card, archive, SyncMode.Move));
            Expect(r.Moved == 2, "2 moved: " + r.Summary);
            Expect(File.ReadAllBytes(Path.Combine(archive, @"DCIM\100\A.jpg")).SequenceEqual(a), "moved file intact");
            Expect(!Directory.Exists(Path.Combine(card, "DCIM")), "emptied folders removed from the source");
        }

        private static void TwoWayRound()
        {
            var a = Dir("tw-a");
            var b = Dir("tw-b");
            Put(a, "shared.txt", Pattern(1000, 10));
            Put(a, "a-only.txt", Pattern(1000, 11));
            Put(b, "b-only.txt", Pattern(1000, 12));
            var pair = Pair("Two-way test", a, b, SyncMode.TwoWay);
            var r = Sync(pair, plan => Expect(plan.DeletedFiles == 0, "first run deletes nothing"));
            Expect(File.Exists(Path.Combine(a, "b-only.txt")) && File.Exists(Path.Combine(b, "a-only.txt")), "merged");

            File.Delete(Path.Combine(a, "a-only.txt"));
            Put(b, "b-only.txt", Pattern(2000, 13));
            File.SetLastWriteTimeUtc(Path.Combine(b, "b-only.txt"), DateTime.UtcNow.AddMinutes(1));
            Put(a, "shared.txt", Pattern(1500, 14));
            File.SetLastWriteTimeUtc(Path.Combine(a, "shared.txt"), DateTime.UtcNow.AddMinutes(-5));
            Put(b, "shared.txt", Pattern(1600, 15));
            File.SetLastWriteTimeUtc(Path.Combine(b, "shared.txt"), DateTime.UtcNow);
            r = Sync(pair, plan => Expect(plan.DeletedFiles == 1 && plan.ChangedFiles == 1 && plan.Conflicts == 1, $"1 delete, 1 change, 1 conflict ({plan.DeletedFiles}, {plan.ChangedFiles}, {plan.Conflicts})"));
            Expect(!File.Exists(Path.Combine(b, "a-only.txt")), "delete reached the other side");
            Expect(SameBytes(Path.Combine(a, "b-only.txt"), Path.Combine(b, "b-only.txt")), "change reached the other side");
            var conflict = Directory.GetFiles(a, "shared (conflict *).txt").SingleOrDefault();
            Expect(conflict != null && File.Exists(Path.Combine(b, Path.GetFileName(conflict))), "older copy kept on both sides as a conflict copy");
            Expect(File.ReadAllBytes(Path.Combine(a, "shared.txt")).SequenceEqual(Pattern(1600, 15)), "newer copy keeps the name");
            Expect(PlanOf(pair).IsEmpty, "in step afterwards");
        }

        private static void RenameById()
        {
            var src = Dir("ren-src");
            var dst = Dir("ren-dst");
            Put(src, "small.txt", Pattern(300, 16)); // too small to be guessed: the NTFS file ID proves it
            Put(src, "other.txt", Pattern(300, 17));
            var pair = Pair("Rename test", src, dst, SyncMode.Mirror);
            Sync(pair);
            using (var s = SyncSession.Open(pair, Triggers.Manual))
            {
                s.Scan();
                Expect(s.LeftTree.StableFileIds && s.LeftTree.Items.Values.All(e => e.FileId != 0), "file IDs read while listing");
            }
            Directory.CreateDirectory(Path.Combine(src, "Moved"));
            File.Move(Path.Combine(src, "small.txt"), Path.Combine(src, "Moved", "renamed.txt"));
            var r = Sync(pair, plan => Expect(plan.Renamed == 1 && plan.NewFiles == 0, $"one rename, no copies ({plan.Renamed}, {plan.NewFiles})"));
            Expect(File.Exists(Path.Combine(dst, "Moved", "renamed.txt")) && !File.Exists(Path.Combine(dst, "small.txt")), "renamed in the destination");
            Expect(r.Renamed == 1 && r.Bytes == 0, "nothing copied");
        }

        private static void LockedFile()
        {
            var src = Dir("lock-src");
            var dst = Dir("lock-dst");
            Put(src, "open.pst", Pattern(50_000, 18));
            Put(src, "free.txt", Pattern(500, 19));
            var pair = Pair("Locked test", src, dst, SyncMode.Mirror);
            RunResult r;
            using (new FileStream(Path.Combine(src, "open.pst"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                r = Sync(pair);
            Expect(r.Status == RunStatus.Warnings && r.Failed == 1 && r.Problems.Any(p => p.Contains("In use")), "in-use file reported: " + string.Join("; ", r.Problems));
            Expect(File.Exists(Path.Combine(dst, "free.txt")) && !File.Exists(Path.Combine(dst, "open.pst")), "the rest was copied");
            Expect(!Directory.GetFiles(dst, "*" + Paths.TempSuffix, SearchOption.AllDirectories).Any(), "no half-written file left");
            r = Sync(pair);
            Expect(r.Copied == 1 && SameBytes(Path.Combine(src, "open.pst"), Path.Combine(dst, "open.pst")), "copied once it's free");
        }

        private static void LongPaths()
        {
            var src = Dir("long-src");
            var dst = Dir("long-dst");
            var deep = string.Join("\\", Enumerable.Range(0, 12).Select(i => "A rather long folder name " + i));
            Put(src, Path.Combine(deep, "deep file.txt"), Pattern(1234, 20));
            Expect(Path.Combine(dst, deep, "deep file.txt").Length > 300, "the test path is over 300 characters");
            var r = Sync(Pair("Long paths", src, dst, SyncMode.Mirror));
            Expect(r.Status == RunStatus.Ok, "status OK: " + string.Join("; ", r.Problems));
            Expect(SameBytes(Path.Combine(src, deep, "deep file.txt"), Path.Combine(dst, deep, "deep file.txt")), "deep file copied");
        }

        private static void Attributes()
        {
            var src = Dir("attr-src");
            var dst = Dir("attr-dst");
            Put(src, "readonly.txt", Pattern(100, 21));
            Put(src, "hidden.txt", Pattern(100, 22));
            File.SetAttributes(Path.Combine(src, "readonly.txt"), FileAttributes.ReadOnly);
            File.SetAttributes(Path.Combine(src, "hidden.txt"), FileAttributes.Hidden);
            var pair = Pair("Attributes", src, dst, SyncMode.Mirror);
            Sync(pair);
            Expect((File.GetAttributes(Path.Combine(dst, "readonly.txt")) & FileAttributes.ReadOnly) != 0, "read-only kept");
            Expect((File.GetAttributes(Path.Combine(dst, "hidden.txt")) & FileAttributes.Hidden) != 0, "hidden kept");
            File.SetAttributes(Path.Combine(src, "readonly.txt"), FileAttributes.Normal);
            Put(src, "readonly.txt", Pattern(200, 23));
            var r = Sync(pair);
            Expect(r.Updated == 1 && SameBytes(Path.Combine(src, "readonly.txt"), Path.Combine(dst, "readonly.txt")), "a read-only destination file is replaced: " + string.Join("; ", r.Problems));
        }

        // ================================================================== drives and shares

        private static void UsbDriveRound()
        {
            _usbLetter = CreateVhd("usb", "ntfs", "KUDUUSB");
            _usbVhd = Vhds.Last();
            var src = Dir("usb-src");
            Put(src, "photo.jpg", Pattern(100_000, 30));
            _usbPair = Pair("USB backup", src, _usbLetter + @"\Backup", SyncMode.Mirror);
            Expect(!string.IsNullOrEmpty(_usbPair.Right.VolumeSerial) && _usbPair.Right.VolumeLabel == "KUDUUSB", "the drive's serial number and label are remembered");
            var r = Sync(_usbPair);
            Expect(r.Status == RunStatus.Ok && File.Exists(_usbLetter + @"\Backup\photo.jpg"), "first backup to the drive");

            var newLetter = FreeLetter(_usbLetter[0]);
            Diskpart($"select volume={_usbLetter[0]}", $"remove letter={_usbLetter[0]}", $"assign letter={newLetter[0]}");
            WaitFor(() => Directory.Exists(newLetter + "\\"), 30, "the new letter");
            var resolved = Locations.Resolve(_usbPair.Right);
            Expect(resolved.Ok && resolved.Root.StartsWith(newLetter, StringComparison.OrdinalIgnoreCase), $"found on {newLetter} ({resolved.Root} {resolved.Problem})");
            Put(src, "photo2.jpg", Pattern(100_000, 31));
            r = Sync(_usbPair);
            Expect(r.Copied == 1 && File.Exists(newLetter + @"\Backup\photo2.jpg"), "synced to the drive under its new letter");
            Note($"USB drive moved from {_usbLetter} to {newLetter}");
            var oldLetter = _usbLetter;
            _usbLetter = newLetter;

            // Another drive now has the old letter: it must not be used.
            var other = CreateVhdAt("other", "ntfs", "OTHER", oldLetter);
            resolved = Locations.Resolve(_usbPair.Right);
            Expect(resolved.Ok && resolved.Root.StartsWith(newLetter, StringComparison.OrdinalIgnoreCase), "a different drive on the old letter is ignored");
            Diskpart($"select vdisk file=\"{other}\"", "detach vdisk");

            // Unplugged: a scheduled run just notes it.
            Diskpart($"select volume={_usbLetter[0]}", $"remove letter={_usbLetter[0]}");
            WaitFor(() => !Directory.Exists(_usbLetter + "\\"), 30, "the drive to go");
            r = Runner.RunUnattended(_usbPair, Triggers.Scheduled);
            Expect(r.Status == RunStatus.Skipped && r.Message.Contains("isn't connected"), "unplugged drive is skipped: " + r.Message);
            Expect(!Runner.ShouldNotify(r, Triggers.Scheduled), "no notification for an unplugged drive");
            Diskpart($"select vdisk file=\"{_usbVhd}\"", "detach vdisk");
        }

        private static string CreateVhdAt(string name, string fs, string label, string letter)
        {
            var vhd = Path.Combine(_temp, name + ".vhdx");
            Diskpart($"create vdisk file=\"{vhd}\" maximum=64 type=expandable", $"select vdisk file=\"{vhd}\"", "attach vdisk",
                "create partition primary", $"format fs={fs} quick label={label}", $"assign letter={letter[0]}");
            Vhds.Add(vhd);
            WaitFor(() => Directory.Exists(letter + "\\"), 30, "drive " + letter);
            return vhd;
        }

        private static void Fat32Round()
        {
            var letter = CreateVhd("fat32", "fat32", "KUDUFAT");
            var src = Dir("fat-src");
            for (var i = 0; i < 20; i++)
            {
                Put(src, $"file{i}.bin", Pattern(5000 + i, 40 + i));
                File.SetLastWriteTimeUtc(Path.Combine(src, $"file{i}.bin"), new DateTime(2026, 3, 1, 10, 0, 1, DateTimeKind.Utc).AddMilliseconds(i * 77)); // odd seconds
            }
            var pair = Pair("FAT32 test", src, letter + @"\Copy", SyncMode.Mirror);
            var r = Sync(pair);
            Expect(r.Copied == 20, "20 copied");
            var plan = PlanOf(pair);
            Expect(plan.IsEmpty, "nothing to copy again (" + string.Join(", ", plan.Actions.Take(3)) + ")");
            using (var s = SyncSession.Open(pair, Triggers.Manual))
            {
                s.Scan();
                Expect(!s.RightTree.StableFileIds, "FAT32 doesn't count as having stable file IDs");
            }
        }

        private static void NetworkRound()
        {
            var folder = Dir("share");
            var (code, output) = Exec("net.exe", $"share KuduSyncTest=\"{folder}\" /grant:everyone,full");
            Expect(code == 0, "the share is created: " + output);
            _share = "KuduSyncTest";
            var src = Dir("net-src");
            Put(src, @"Docs\a.txt", Pattern(70_000, 50));
            Put(src, "b.txt", Pattern(80_000, 51));
            var unc = @"\\localhost\KuduSyncTest\Backup";
            var pair = Pair("Network test", src, unc, SyncMode.Mirror);
            Expect(pair.Right.IsNetwork && pair.Right.VolumeSerial == null, "network location");
            var r = Sync(pair);
            Expect(r.Status == RunStatus.Ok && r.Copied == 2, "copied to the share: " + r.Summary + " " + string.Join("; ", r.Problems));
            Expect(SameBytes(Path.Combine(src, @"Docs\a.txt"), Path.Combine(folder, @"Backup\Docs\a.txt")), "file arrived intact");
            Expect(PlanOf(pair).IsEmpty, "nothing to do the second time");
            File.Move(Path.Combine(src, "b.txt"), Path.Combine(src, "c.txt"));
            r = Sync(pair, plan => Expect(plan.Renamed == 1, "rename recognised over the network"));

            var offline = Pair("Offline share", src, @"\\kudusync-no-such-server\share", SyncMode.Mirror);
            var started = DateTime.UtcNow;
            r = Runner.RunUnattended(offline, Triggers.Scheduled);
            Expect(r.Status == RunStatus.Failed && r.Message.Contains("kudusync-no-such-server"), "unreachable share reported: " + r.Message);
            Note($"unreachable share reported after {(DateTime.UtcNow - started).TotalSeconds:0}s");
        }

        // ================================================================== Task Scheduler

        private static void ScheduleRound()
        {
            var src = Dir("sched-src");
            var dst = Dir("sched-dst");
            Put(src, "nightly.txt", Pattern(4000, 60));
            var pair = Pair("Self-test schedule", src, dst, SyncMode.Mirror);
            DataStore.SavePairs(new[] { pair });
            foreach (var kind in new[] { ScheduleKind.Daily, ScheduleKind.Weekly, ScheduleKind.EveryHours, ScheduleKind.AtLogon })
            {
                pair.Schedule.Kind = kind;
                pair.Schedule.Days = (1 << 1) | (1 << 3);
                var problem = Schedules.Apply(pair);
                Expect(problem == null, kind + " schedule accepted by Windows: " + problem);
                Expect(Schedules.Exists(pair), kind + " task exists");
            }
            pair.Schedule.Kind = ScheduleKind.Daily;
            Schedules.Apply(pair);
            Expect(Schedules.TaskNames().Count(n => n.EndsWith("(" + pair.Id.Substring(0, 8) + ")")) == 1, "one task per pair");

            Expect(Schedules.RunNow(pair), "the task starts");
            WaitFor(() => RunLog.All().Any(r => r.PairId == pair.Id && r.Trigger == Triggers.Scheduled), 90, "the scheduled run to finish");
            var run = RunLog.All().Last(r => r.PairId == pair.Id);
            Expect(run.Status == RunStatus.Ok && File.Exists(Path.Combine(dst, "nightly.txt")), "the scheduled run synced: " + run.Summary);

            var renamed = pair.Clone();
            renamed.Name = "Self-test schedule renamed";
            Schedules.Apply(renamed);
            Expect(Schedules.Exists(renamed) && !Schedules.Exists(pair), "renaming the pair replaces its task");
            renamed.Schedule.Kind = ScheduleKind.None;
            Schedules.Apply(renamed);
            Expect(!Schedules.Exists(renamed), "removing the schedule removes the task");
            DataStore.SavePairs(new FolderPair[0]);
        }

        // ================================================================== tray

        private static void TrayRound()
        {
            var src = Dir("live-src");
            var dst = Dir("live-dst");
            var live = Pair("Live test", src, dst, SyncMode.Mirror);
            live.LiveSync = true;
            live.LiveDelaySeconds = 2;
            var pairs = new List<FolderPair> { live };

            var plugLetter = CreateVhd("plug", "ntfs", "KUDUPLUG");
            var plugVhd = Vhds.Last();
            var plug = Pair("Plug test", Dir("plug-src"), plugLetter + @"\Backup", SyncMode.Mirror);
            pairs.Add(plug);
            DataStore.SavePairs(pairs);
            Diskpart($"select vdisk file=\"{plugVhd}\"", "detach vdisk");

            var tray = Process.Start(new ProcessStartInfo(AppInfo.ExePath, "--tray --data " + Args.Quote(DataStore.OverrideFolder)) { UseShellExecute = false });
            try
            {
                WaitFor(() => File.Exists(Tray.LogFile) && File.ReadAllText(Tray.LogFile).Contains("Watching 1 live pair"), 30, "the tray to start watching");
                var second = Process.Start(new ProcessStartInfo(AppInfo.ExePath, "--tray --data " + Args.Quote(DataStore.OverrideFolder)) { UseShellExecute = false });
                Expect(second.WaitForExit(15000), "a second tray exits straight away");

                Put(src, "new file.txt", Pattern(3000, 70));
                WaitFor(() => File.Exists(Path.Combine(dst, "new file.txt")), 60, "live sync to copy the new file");
                Expect(SameBytes(Path.Combine(src, "new file.txt"), Path.Combine(dst, "new file.txt")), "live sync copied it intact");
                Note("live sync copied a new file");

                Diskpart($"select vdisk file=\"{plugVhd}\"", "attach vdisk");
                try
                {
                    WaitFor(() => File.ReadAllText(Tray.LogFile).Contains("Drive arrived: KUDUPLUG"), 45, "the tray to notice the drive");
                    Expect(File.ReadAllText(Tray.LogFile).Contains("1 pair(s) can sync: Plug test"), "the drive's pair is offered");
                    Note("tray noticed the drive being plugged in");
                }
                finally
                {
                    Diskpart($"select vdisk file=\"{plugVhd}\"", "detach vdisk");
                }

                Tray.StopRunning();
                Expect(tray.WaitForExit(15000), "the tray stops when asked");
            }
            finally
            {
                if (!tray.HasExited) tray.Kill();
                DataStore.SavePairs(new FolderPair[0]);
            }
        }

        // ================================================================== SyncToy

        private static void SyncToyRound()
        {
            var file = Path.Combine(_temp, "SyncToyDirPairs.bin");
            using (var stream = new FileStream(file, FileMode.Create))
            {
                var bf = new BinaryFormatter();
                bf.Serialize(stream, new SyncToy.SyncEngineConfig("Pictures", @"C:\Users\Test\Pictures", @"E:\Backup\Pictures", "Echo", "*.jpg;*.png", "*.tmp", FileAttributes.Hidden | FileAttributes.System, true));
                bf.Serialize(stream, new SyncToy.SyncEngineConfig("Work", @"C:\Work", @"\\nas\work", "Synchronize", "*", string.Empty, 0, false));
            }
            var found = SyncToyImport.Read(file);
            Expect(found.Count == 2 && found[0].Mode == SyncMode.Mirror && found[1].Mode == SyncMode.TwoWay, "both pairs read");
            var window = new ImportWindow(null, new List<FolderPair>(), file);
            OffScreen(window);
            window.Show();
            Pump();
            window.Close();
            var pair = SyncToyImport.ToPair(found[0]);
            Expect(pair.Filters.Include == "*.jpg; *.png" && pair.Filters.SkipHidden && pair.CompareContents && !pair.Verify, "settings carried over");
        }

        // ================================================================== the window

        private static void UiRound()
        {
            MainWindow main = null;
            var src = Dir("ui-src");
            var dst = Dir("ui-dst");
            Put(src, @"Album\one.jpg", Pattern(200_000, 80));
            Put(src, "two.txt", Pattern(3000, 81));

            Step("Main window: welcome", () =>
            {
                DataStore.SavePairs(new FolderPair[0]);
                main = new MainWindow();
                OffScreen(main);
                Application.Current.MainWindow = main;
                main.Show();
                Pump();
                Expect(main.PageName == "welcome", "the welcome page with no pairs");
            });
            Step("Pair editor: create a pair", () =>
            {
                var editor = new PairEditor(main, new FolderPair(), main.Pairs.ToList(), true);
                OffScreen(editor);
                editor.Show();
                Pump();
                editor.Fill("UI test", src, dst, SyncMode.Mirror);
                Pump();
                editor.SaveForTest();
                Pump();
                Expect(editor.Result != null && editor.Result.Right.VolumeSerial != null, "the editor made the pair");
                ((List<FolderPair>)main.Pairs).Add(editor.Result);
                main.AfterSave(editor.Result);
                Pump();
                Expect(main.PageName == "pairs" && main.Selected?.Name == "UI test", "the pair is shown");
                Expect(DataStore.LoadPairs().Any(p => p.Name == "UI test"), "the pair is saved");
            });
            Step("Preview, then sync", () =>
            {
                var task = main.RunPairsAsync(new List<FolderPair> { main.Selected }, true);
                WaitFor(() => main.PageName == "preview", 30, "the preview");
                Expect(main.CurrentPlan.NewFiles == 2, "2 new files in the preview");
                main.AnswerPreview(true);
                WaitFor(() => task.IsCompleted, 60, "the sync");
                Expect(main.PageName == "results" && main.LastResults.Single().Status == RunStatus.Ok, "results shown");
                Expect(SameBytes(Path.Combine(src, @"Album\one.jpg"), Path.Combine(dst, @"Album\one.jpg")), "synced through the window");
            });
            Step("Sync now with nothing to do", () =>
            {
                var task = main.RunPairsAsync(new List<FolderPair> { main.Selected }, false);
                WaitFor(() => task.IsCompleted, 60, "the sync");
                Expect(main.LastResults.Single().Summary.StartsWith("Already in step"), "already in step: " + main.LastResults.Single().Summary);
            });
            Step("Stopping a sync", () =>
            {
                for (var i = 0; i < 10; i++) Put(src, $"big{i}.bin", Pattern(40_000_000, 90 + i));
                main.Selected.Verify = true; // write-through and read back: slow enough to stop part-way
                var task = main.RunPairsAsync(new List<FolderPair> { main.Selected }, false);
                WaitFor(() => main.PageName == "running", 30, "the sync to start");
                main.Stop();
                WaitFor(() => task.IsCompleted, 60, "the sync to stop");
                Expect(main.LastResults.Single().Status == RunStatus.Stopped, "stopped: " + main.LastResults.Single().Status);
                Expect(!Directory.GetFiles(dst, "*" + Paths.TempSuffix, SearchOption.AllDirectories).Any(), "no half-written files after stopping");
            });
            Step("Right-click: sync a folder", () =>
            {
                main.HandleCommandLine(new[] { "--sync-folder", src });
                WaitFor(() => !main.IsBusy && main.PageName == "results", 90, "the right-click sync");
                Expect(main.LastResults.Single().Trigger == Triggers.RightClick && main.LastResults.Single().Status == RunStatus.Ok, "synced from the right-click menu");
            });
            Step("History page", () =>
            {
                main.ShowHistory();
                Pump();
                Expect(main.PageName == "history", "history shown");
            });
            Step("Edit window for an existing pair", () => ShowAndClose(new PairEditor(main, main.Pairs[0].Clone(), main.Pairs.ToList(), false)));
            Step("Dark theme", () =>
            {
                ThemeManager.Apply(ThemeMode.Dark);
                main.ShowPairs();
                Pump();
                main.ShowHistory();
                Pump();
                ShowAndClose(new PairEditor(main, new FolderPair(), main.Pairs.ToList(), true));
                ThemeManager.Apply(ThemeMode.Light);
                main.ShowPairs();
                Pump();
            });
            Step("Settings window", () => ShowAndClose(new SettingsWindow(main)));
            Step("About window", () => ShowAndClose(About.Build(main)));
            Step("Delete the pair", () =>
            {
                main.DeletePair(main.Pairs[0], false);
                Pump();
                Expect(main.Pairs.Count == 0 && main.PageName == "welcome", "back to the welcome page");
                Expect(File.Exists(Path.Combine(dst, "two.txt")), "deleting a pair leaves the files alone");
            });
            Step("Close main window", () =>
            {
                main?.Close();
                Pump();
            });
        }

        private static void Cleanup()
        {
            Schedules.RemoveAll();
            if (_share != null) Exec("net.exe", $"share {_share} /delete /y");
            foreach (var vhd in Vhds)
            {
                try { Diskpart($"select vdisk file=\"{vhd}\"", "detach vdisk"); }
                catch { /* already detached */ }
            }
        }
    }
}

namespace SyncToy
{
    // Stand-ins with the same field names as SyncToy 2.1's settings classes, to write a realistic file to import.
    [Serializable]
    public class SyncEngineConfig
    {
        private string name;
        private string leftDir;
        private string rightDir;
        private string searchPattern;
        private string excludedPattern;
        private string syncModeName;
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
