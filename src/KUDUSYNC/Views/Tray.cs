// The tray part: the icon by the clock with its menu, live sync, running pairs when their drive is plugged in, and
// alerts every 5 minutes.
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
        /// <summary>Windows' message about devices changing.</summary>
        private const int WM_DEVICECHANGE = 0x0219;
        /// <summary>A device was plugged in.</summary>
        private const int DBT_DEVICEARRIVAL = 0x8000;
        /// <summary>A device was taken out.</summary>
        private const int DBT_DEVICEREMOVECOMPLETE = 0x8004;
        /// <summary>The device is a drive.</summary>
        private const int DBT_DEVTYP_VOLUME = 2;

        /// <summary>The icon by the clock.</summary>
        private Forms.NotifyIcon _icon;
        /// <summary>A hidden window that gets Windows' device messages.</summary>
        private HwndSource _window;
        /// <summary>The pairs watched for live sync.</summary>
        private readonly List<LiveWatch> _watches = new List<LiveWatch>();
        /// <summary>Every pair.</summary>
        private List<FolderPair> _pairs = new List<FolderPair>();
        /// <summary>Notices when the pairs change.</summary>
        private FileSystemWatcher _pairsWatcher;
        /// <summary>Reads the pairs again a moment after they change.</summary>
        private DispatcherTimer _reload;
        /// <summary>What clicking the last balloon does.</summary>
        private Action _balloonClick;
        /// <summary>True while paused.</summary>
        private bool _paused;
        /// <summary>The "Pause" menu item.</summary>
        private Forms.ToolStripMenuItem _pauseItem;
        /// <summary>Set by another part to close the tray.</summary>
        private EventWaitHandle _stopSignal;
        /// <summary>Looks for alerts every 5 minutes.</summary>
        private DispatcherTimer _alertTimer;
        /// <summary>The icon, and the icon with a dot for alerts.</summary>
        private System.Drawing.Icon _plainIcon, _alertIcon;
        /// <summary>The icon's tooltip.</summary>
        private string _liveText = "KUDUSYNC";
        /// <summary>The alerts found last.</summary>
        private List<Alert> _alerts = new List<Alert>();

        /// <summary>The backups that need looking at, as the tray icon shows them (for the self-test).</summary>
        public IReadOnlyList<Alert> ShownAlerts => _alerts;
        /// <summary>The icon's tooltip (for the self-test).</summary>
        public string IconText => _icon?.Text;
        /// <summary>True when the icon shows the alert dot.</summary>
        public bool IconMarked => _icon != null && _alertIcon != null && ReferenceEquals(_icon.Icon, _alertIcon);

        /// <summary>The tray's own log.</summary>
        public static string LogFile => Path.Combine(DataStore.Folder, "tray.log");

        /// <summary>Starts the tray: the icon, the menu, the device messages, live sync and alerts.</summary>
        public void Start()
        {
            Log("Started");
            _plainIcon = Notifier.AppIcon(Forms.SystemInformation.SmallIconSize.Width);
            _icon = new Forms.NotifyIcon { Icon = _plainIcon, Text = "KUDUSYNC", Visible = true };
            var menu = new Forms.ContextMenuStrip();
            menu.Items.Add(Lang.T("Open KUDUSYNC"), null, (s, e) => Notifier.OpenApp());
            menu.Items.Add(Lang.T("Sync all pairs now"), null, (s, e) => RunPairs(_pairs.Where(p => p.InRunAll).ToList(), Triggers.Manual));
            _pauseItem = new Forms.ToolStripMenuItem(Lang.T("Pause live sync"), null, (s, e) => SetPaused(!_paused));
            menu.Items.Add(_pauseItem);
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add(Lang.T("Stop running in the notification area"), null, (s, e) => Exit());
            _icon.ContextMenuStrip = menu;
            _icon.DoubleClick += (s, e) => Notifier.OpenApp(_alerts.Count > 0 ? Toasts.Link : string.Empty);
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

            // Every few minutes: scheduled backups that failed, had problems or were missed. The first look is soon after
            // Windows starts, so a backup missed while the PC was off is said straight away.
            _alertTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
            _alertTimer.Tick += (s, e) =>
            {
                _alertTimer.Interval = TimeSpan.FromMinutes(5);
                CheckAlerts();
            };
            _alertTimer.Start();
        }

        /// <summary>Notifies new alerts (once each) and shows on the icon whether any are waiting to be looked at.</summary>
        public void CheckAlerts()
        {
            try
            {
                var fresh = Alerts.TakeNew(DateTime.Now);
                if (Settings.Notify != NotifyMode.Never)
                    foreach (var alert in fresh.Take(3))
                        if (!Toasts.Show(alert))
                        {
                            _balloonClick = () => Notifier.OpenApp(Toasts.Link);
                            _icon.ShowBalloonTip(12000, alert.Title, Notifier.Trim(alert.Text), alert.IsRed ? Forms.ToolTipIcon.Error : Forms.ToolTipIcon.Warning);
                        }
                _alerts = Alerts.Current(DateTime.Now);
                Log($"Alerts: {_alerts.Count} waiting ({string.Join(", ", _alerts.Select(a => a.PairName + " " + a.Level))}), {fresh.Count} new");
            }
            catch (Exception ex)
            {
                Log("Alerts: " + ex.Message);
            }
            ShowIconState();
        }

        /// <summary>The icon with a red dot (a backup failed or was missed) or an orange one (one ran with problems).</summary>
        private void ShowIconState()
        {
            _alertIcon?.Dispose();
            _alertIcon = null;
            if (_alerts.Count == 0) _icon.Icon = _plainIcon;
            else
            {
                var red = _alerts.Any(a => a.IsRed);
                using (var bitmap = _plainIcon.ToBitmap())
                {
                    using (var g = System.Drawing.Graphics.FromImage(bitmap))
                    using (var brush = new System.Drawing.SolidBrush(red ? System.Drawing.Color.FromArgb(0xD1, 0x34, 0x38) : System.Drawing.Color.FromArgb(0xE8, 0x8A, 0x00)))
                    using (var edge = new System.Drawing.Pen(System.Drawing.Color.White, 1))
                    {
                        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                        var d = bitmap.Width / 2f;
                        g.FillEllipse(brush, bitmap.Width - d - 0.5f, bitmap.Height - d - 0.5f, d, d);
                        g.DrawEllipse(edge, bitmap.Width - d - 0.5f, bitmap.Height - d - 0.5f, d, d);
                    }
                    var handle = bitmap.GetHicon();
                    _alertIcon = (System.Drawing.Icon)System.Drawing.Icon.FromHandle(handle).Clone();
                    DestroyIcon(handle);
                }
                _icon.Icon = _alertIcon;
            }
            var text = _alerts.Count == 0 ? _liveText
                : _alerts.Count == 1 ? Lang.T("KUDUSYNC · {0}", _alerts[0].Title)
                : Lang.T("KUDUSYNC · {0} backups need looking at", _alerts.Count);
            _icon.Text = text.Length > 63 ? text.Substring(0, 62) + "…" : text;
        }

        /// <summary>Frees an icon made from a handle.</summary>
        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr handle);

        /// <summary>The name of the signal that closes the tray (one per user and data folder).</summary>
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

        /// <summary>Starts the tray part.</summary>
        public static void StartRunning() => Notifier.OpenApp("--tray");

        /// <summary>Closes the tray.</summary>
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

        /// <summary>Pauses or goes on with live sync and drives.</summary>
        private void SetPaused(bool paused)
        {
            _paused = paused;
            _pauseItem.Text = Lang.T(paused ? "Resume live sync" : "Pause live sync");
            Reload();
        }

        /// <summary>Reads the pairs again and watches the ones with live sync.</summary>
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
            _liveText = _paused ? Lang.T("KUDUSYNC · live sync paused") : _watches.Count == 0 ? "KUDUSYNC" : Lang.T("KUDUSYNC · live sync on for {0}", Format.Count(_watches.Count, "pair"));
            if (_liveText.Length > 63) _liveText = _liveText.Substring(0, 63);
            if (_alerts.Count == 0) _icon.Text = _liveText;
            Log($"Watching {_watches.Count} live pair(s): {string.Join(", ", _watches.Select(w => w.Pair.Name))}");
        }

        /// <summary>Runs a pair for live sync (done is called after).</summary>
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

        /// <summary>Runs pairs one after another in the background.</summary>
        private void RunPairs(List<FolderPair> pairs, string trigger)
        {
            if (pairs.Count == 0) return;
            Task.Run(() => pairs.Select(p => Runner.RunUnattended(p, trigger)).ToList()).ContinueWith(t =>
            {
                var results = t.Result;
                foreach (var r in results) Log($"{trigger}: {r.PairName}: {r.StatusText} · {r.Summary}");
                CheckAlerts(); // a backup run from here may put an alert right
                if (results.Count == 1)
                {
                    if (Runner.ShouldNotify(results[0], trigger) || trigger != Triggers.Scheduled) Show(results[0]);
                }
                else
                {
                    var bad = results.Count(r => r.Status == RunStatus.Failed || r.Status == RunStatus.Warnings);
                    _balloonClick = Notifier.OpenHistory;
                    _icon.ShowBalloonTip(10000, bad == 0 ? Lang.T("{0} synced", Format.Count(results.Count, "pair")) : Lang.T("{0} had problems", Format.Count(bad, "pair")),
                        Notifier.Trim(string.Join("\n", results.Select(r => r.PairName + ": " + r.Summary))), bad == 0 ? Forms.ToolTipIcon.Info : Forms.ToolTipIcon.Warning);
                }
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        /// <summary>Shows how a run went.</summary>
        private void Show(RunResult result)
        {
            _balloonClick = Notifier.OpenHistory;
            _icon.ShowBalloonTip(10000, Notifier.Title(result), Notifier.Trim(result.Summary), Notifier.IconFor(result));
        }

        // ------------------------------------------------------------------ drives being plugged in

        [StructLayout(LayoutKind.Sequential)]
        private struct DEV_BROADCAST_VOLUME
        {
            /// <summary>The size of this structure.</summary>
            public int Size;
            /// <summary>What kind of device.</summary>
            public int DeviceType;
            /// <summary>Not used.</summary>
            public int Reserved;
            /// <summary>The drive letters, one bit each.</summary>
            public int UnitMask;
            /// <summary>Flags.</summary>
            public short Flags;
        }

        /// <summary>Gets Windows' messages: a drive plugged in runs the pairs on it.</summary>
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

        /// <summary>Runs the pairs set to run when their drive (found by its serial number) is plugged in.</summary>
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
                _icon.ShowBalloonTip(15000, Lang.T("{0} is connected", volume.Describe()),
                    Notifier.Trim(Lang.T("Click to sync {0}.", pairs.Count == 1 ? "\"" + names + "\"" : names)), Forms.ToolTipIcon.Info);
            }
            _reload.Stop();
            _reload.Start();
        }

        // ------------------------------------------------------------------ helpers

        /// <summary>Runs an action on the window's thread.</summary>
        private static void Ui(Action action) => System.Windows.Application.Current?.Dispatcher.BeginInvoke(action);

        /// <summary>Writes a line in the tray's log.</summary>
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
        /// <summary>Watches each folder of the pair.</summary>
        private readonly List<FileSystemWatcher> _watchers = new List<FileSystemWatcher>();
        /// <summary>Waits a moment after the last change.</summary>
        private readonly DispatcherTimer _timer;
        /// <summary>Runs the pair.</summary>
        private readonly Action<FolderPair, Action> _run;
        /// <summary>True while it runs.</summary>
        private bool _running;
        /// <summary>True when something changed while it ran (it runs again after).</summary>
        private bool _dirty;

        /// <summary>The pair.</summary>
        public FolderPair Pair { get; }

        /// <summary>Watches a pair.</summary>
        private LiveWatch(FolderPair pair, Action<FolderPair, Action> run)
        {
            Pair = pair;
            _run = run;
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(Math.Max(1, pair.LiveDelaySeconds)) };
            _timer.Tick += (s, e) => Fire();
        }

        /// <summary>
        /// Watches a pair's folders (both for two-way, the source otherwise), or null when they can't be watched.
        /// </summary>
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

        /// <summary>A file changed: waits, then runs (leaving out KUDUSYNC's own files).</summary>
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

        /// <summary>Runs the pair (again later when something changed meanwhile).</summary>
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

        /// <summary>Stops watching.</summary>
        public void Dispose()
        {
            _timer.Stop();
            foreach (var w in _watchers) w.Dispose();
            _watchers.Clear();
        }
    }
}
