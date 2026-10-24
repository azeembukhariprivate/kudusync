using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Interop;
using System.Windows.Threading;
using Kudusync.Core;
using Forms = System.Windows.Forms;

namespace Kudusync.Views
{
    /// <summary>
    /// KUDUSYNC.exe --tray: an icon in the notification area that watches for a pair's drive being plugged in (and shows
    /// a notification; nothing runs until it's clicked), and keeps live-sync pairs in step a short while after files
    /// change. It reloads the pairs whenever they're changed in the app.
    /// </summary>
    public sealed class Tray
    {
        private const int WM_DEVICECHANGE = 0x0219;
        private const int DBT_DEVICEARRIVAL = 0x8000;
        private const int DBT_DEVICEREMOVECOMPLETE = 0x8004;
        private const int DBT_DEVTYP_VOLUME = 2;

        private Forms.NotifyIcon _icon;
        private HwndSource _window;
        private readonly List<LiveWatch> _watches = new List<LiveWatch>();
        private List<FolderPair> _pairs = new List<FolderPair>();
        private FileSystemWatcher _pairsWatcher;
        private DispatcherTimer _reload;
        private Action _balloonClick;
        private bool _paused;
        private Forms.ToolStripMenuItem _pauseItem;
        private EventWaitHandle _stopSignal;

        public static string LogFile => Path.Combine(DataStore.Folder, "tray.log");

        public void Start()
        {
            Log("Started");
            _icon = new Forms.NotifyIcon { Icon = Notifier.AppIcon(Forms.SystemInformation.SmallIconSize.Width), Text = "KUDUSYNC", Visible = true };
            var menu = new Forms.ContextMenuStrip();
            menu.Items.Add("Open KUDUSYNC", null, (s, e) => Notifier.OpenApp());
            menu.Items.Add("Sync all pairs now", null, (s, e) => RunPairs(_pairs.Where(p => p.InRunAll).ToList(), Triggers.Manual));
            _pauseItem = new Forms.ToolStripMenuItem("Pause live sync", null, (s, e) => SetPaused(!_paused));
            menu.Items.Add(_pauseItem);
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("Stop running in the notification area", null, (s, e) => Exit());
            _icon.ContextMenuStrip = menu;
            _icon.DoubleClick += (s, e) => Notifier.OpenApp();
            _icon.BalloonTipClicked += (s, e) =>
            {
                var action = _balloonClick;
                _balloonClick = null;
                action?.Invoke();
            };

            // A hidden top-level window: drive arrival messages are only sent to top-level windows.
            _window = new HwndSource(new HwndSourceParameters("KUDUSYNC tray") { Width = 0, Height = 0, WindowStyle = 0 });
            _window.AddHook(WndProc);

            _reload = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _reload.Tick += (s, e) =>
            {
                _reload.Stop();
                Reload();
            };
            Directory.CreateDirectory(DataStore.Folder);
            _pairsWatcher = new FileSystemWatcher(DataStore.Folder, "pairs.json") { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size };
            FileSystemEventHandler changed = (s, e) => Ui(() => { _reload.Stop(); _reload.Start(); });
            _pairsWatcher.Changed += changed;
            _pairsWatcher.Created += changed;
            _pairsWatcher.Renamed += (s, e) => changed(s, e);
            _pairsWatcher.EnableRaisingEvents = true;

            _stopSignal = new EventWaitHandle(false, EventResetMode.AutoReset, StopSignalName);
            var dispatcher = System.Windows.Application.Current.Dispatcher;
            Task.Run(() =>
            {
                _stopSignal.WaitOne();
                dispatcher.BeginInvoke(new Action(Exit));
            });

            Reload();
        }

        private static string StopSignalName => "KUDUSYNC-tray-stop-" + DataStore.StableHash(Environment.UserName + "|" + DataStore.Folder);

        /// <summary>Asks a running tray to close (when it's turned off in Settings, or KUDUSYNC is uninstalled).</summary>
        public static void StopRunning()
        {
            try
            {
                if (EventWaitHandle.TryOpenExisting(StopSignalName, out var signal))
                    using (signal) signal.Set();
            }
            catch { /* not running */ }
        }

        public static void StartRunning() => Notifier.OpenApp("--tray");

        private void Exit()
        {
            Log("Stopped");
            foreach (var w in _watches) w.Dispose();
            _watches.Clear();
            _pairsWatcher?.Dispose();
            if (_icon != null)
            {
                _icon.Visible = false;
                _icon.Dispose();
            }
            _window?.Dispose();
            System.Windows.Application.Current.Shutdown();
        }

        private void SetPaused(bool paused)
        {
            _paused = paused;
            _pauseItem.Text = paused ? "Resume live sync" : "Pause live sync";
            Reload();
        }

        private void Reload()
        {
            _pairs = DataStore.LoadPairs();
            foreach (var w in _watches) w.Dispose();
            _watches.Clear();
            if (!_paused)
                foreach (var pair in _pairs.Where(p => p.LiveSync))
                {
                    var watch = LiveWatch.TryCreate(pair, RunLive);
                    if (watch != null) _watches.Add(watch);
                }
            var text = _paused ? "KUDUSYNC · live sync paused" : _watches.Count == 0 ? "KUDUSYNC" : $"KUDUSYNC · live sync on for {Format.Count(_watches.Count, "pair")}";
            _icon.Text = text.Length > 63 ? text.Substring(0, 63) : text;
            Log($"Watching {_watches.Count} live pair(s): {string.Join(", ", _watches.Select(w => w.Pair.Name))}");
        }

        private void RunLive(FolderPair pair, Action done)
        {
            Log("Live sync: " + pair.Name);
            Task.Run(() => Runner.RunUnattended(pair, Triggers.Live)).ContinueWith(t =>
            {
                var result = t.Result;
                Log($"Live sync finished: {pair.Name}: {result.StatusText} · {result.Summary}");
                if (Runner.ShouldNotify(result, Triggers.Live)) Show(result);
                done();
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        private void RunPairs(List<FolderPair> pairs, string trigger)
        {
            if (pairs.Count == 0) return;
            Task.Run(() => pairs.Select(p => Runner.RunUnattended(p, trigger)).ToList()).ContinueWith(t =>
            {
                var results = t.Result;
                foreach (var r in results) Log($"{trigger}: {r.PairName}: {r.StatusText} · {r.Summary}");
                if (results.Count == 1)
                {
                    if (Runner.ShouldNotify(results[0], trigger) || trigger != Triggers.Scheduled) Show(results[0]);
                }
                else
                {
                    var bad = results.Count(r => r.Status == RunStatus.Failed || r.Status == RunStatus.Warnings);
                    _balloonClick = Notifier.OpenHistory;
                    _icon.ShowBalloonTip(10000, bad == 0 ? $"{Format.Count(results.Count, "pair")} synced" : $"{Format.Count(bad, "pair")} had problems",
                        Notifier.Trim(string.Join("\n", results.Select(r => r.PairName + ": " + r.Summary))), bad == 0 ? Forms.ToolTipIcon.Info : Forms.ToolTipIcon.Warning);
                }
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        private void Show(RunResult result)
        {
            _balloonClick = Notifier.OpenHistory;
            _icon.ShowBalloonTip(10000, Notifier.Title(result), Notifier.Trim(result.Summary), Notifier.IconFor(result));
        }

        // ------------------------------------------------------------------ drives being plugged in

        [StructLayout(LayoutKind.Sequential)]
        private struct DEV_BROADCAST_VOLUME
        {
            public int Size;
            public int DeviceType;
            public int Reserved;
            public int UnitMask;
            public short Flags;
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg != WM_DEVICECHANGE || lParam == IntPtr.Zero) return IntPtr.Zero;
            var code = wParam.ToInt32();
            if (code != DBT_DEVICEARRIVAL && code != DBT_DEVICEREMOVECOMPLETE) return IntPtr.Zero;
            var header = Marshal.PtrToStructure<DEV_BROADCAST_VOLUME>(lParam);
            if (header.DeviceType != DBT_DEVTYP_VOLUME) return IntPtr.Zero;
            var letters = Enumerable.Range(0, 26).Where(i => (header.UnitMask & (1 << i)) != 0).Select(i => (char)('A' + i) + ":").ToList();
            if (code == DBT_DEVICEARRIVAL)
            {
                // Give Windows a moment to finish mounting the drive.
                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                timer.Tick += (s, e) =>
                {
                    timer.Stop();
                    DriveArrived(letters);
                };
                timer.Start();
            }
            else
            {
                Log("Drive removed: " + string.Join(", ", letters));
                _reload.Stop();
                _reload.Start();
            }
            return IntPtr.Zero;
        }

        public void DriveArrived(List<string> letters)
        {
            foreach (var letter in letters)
            {
                var volume = VolumeInfo.Of(letter);
                if (volume == null) continue;
                var pairs = _pairs.Where(p => p.NotifyOnPlug && (p.Left.VolumeSerial == volume.Serial || p.Right.VolumeSerial == volume.Serial)).ToList();
                Log($"Drive arrived: {volume.Describe()} [{volume.Serial}]: {pairs.Count} pair(s) can sync: {string.Join(", ", pairs.Select(p => p.Name))}");
                if (pairs.Count == 0) continue;
                _balloonClick = () => RunPairs(pairs, Triggers.Plugged);
                var names = string.Join(", ", pairs.Select(p => p.Name));
                _icon.ShowBalloonTip(15000, $"{volume.Describe()} is connected",
                    Notifier.Trim($"Click to sync {(pairs.Count == 1 ? "\"" + names + "\"" : names)}."), Forms.ToolTipIcon.Info);
            }
            _reload.Stop();
            _reload.Start();
        }

        // ------------------------------------------------------------------ helpers

        private static void Ui(Action action) => System.Windows.Application.Current?.Dispatcher.BeginInvoke(action);

        public static void Log(string line)
        {
            try
            {
                Directory.CreateDirectory(DataStore.Folder);
                var file = LogFile;
                if (File.Exists(file) && new FileInfo(file).Length > 1024 * 1024) File.Delete(file);
                File.AppendAllText(file, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + line + Environment.NewLine);
            }
            catch { /* best effort */ }
        }
    }

    /// <summary>Watches a live-sync pair's folders and runs it once things have been quiet for its delay.</summary>
    public sealed class LiveWatch : IDisposable
    {
        private readonly List<FileSystemWatcher> _watchers = new List<FileSystemWatcher>();
        private readonly DispatcherTimer _timer;
        private readonly Action<FolderPair, Action> _run;
        private bool _running;
        private bool _dirty;

        public FolderPair Pair { get; }

        private LiveWatch(FolderPair pair, Action<FolderPair, Action> run)
        {
            Pair = pair;
            _run = run;
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(Math.Max(1, pair.LiveDelaySeconds)) };
            _timer.Tick += (s, e) => Fire();
        }

        public static LiveWatch TryCreate(FolderPair pair, Action<FolderPair, Action> run)
        {
            var watch = new LiveWatch(pair, run);
            var sides = pair.Mode == SyncMode.TwoWay ? new[] { pair.Left, pair.Right } : new[] { pair.Left };
            foreach (var side in sides)
            {
                var resolved = Locations.Resolve(side);
                if (!resolved.Ok || !Directory.Exists(resolved.Root)) continue;
                try
                {
                    var w = new FileSystemWatcher(resolved.Root)
                    {
                        IncludeSubdirectories = true,
                        InternalBufferSize = 64 * 1024,
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size
                    };
                    FileSystemEventHandler handler = (s, e) => watch.Changed(e.FullPath);
                    w.Changed += handler;
                    w.Created += handler;
                    w.Deleted += handler;
                    w.Renamed += (s, e) => watch.Changed(e.FullPath);
                    w.Error += (s, e) => watch.Changed(null);
                    w.EnableRaisingEvents = true;
                    watch._watchers.Add(w);
                }
                catch (Exception ex) when (ex is ArgumentException || ex is IOException || ex is UnauthorizedAccessException)
                {
                    Tray.Log($"Can't watch {resolved.Root}: {ex.Message}");
                }
            }
            if (watch._watchers.Count > 0) return watch;
            watch.Dispose();
            return null;
        }

        private void Changed(string path)
        {
            if (path != null && (path.IndexOf(Paths.HistoryName, StringComparison.OrdinalIgnoreCase) >= 0 || path.EndsWith(Paths.TempSuffix, StringComparison.OrdinalIgnoreCase))) return;
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_running)
                {
                    _dirty = true;
                    return;
                }
                _timer.Stop();
                _timer.Start();
            }));
        }

        private void Fire()
        {
            _timer.Stop();
            _running = true;
            _dirty = false;
            _run(Pair, () =>
            {
                _running = false;
                if (!_dirty) return;
                _dirty = false;
                _timer.Start();
            });
        }

        public void Dispose()
        {
            _timer.Stop();
            foreach (var w in _watchers) w.Dispose();
            _watchers.Clear();
        }
    }
}
