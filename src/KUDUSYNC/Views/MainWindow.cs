using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using Kudusync.Controls;
using Kudusync.Core;

namespace Kudusync.Views
{
    /// <summary>The main window: the list of folder pairs, a pair's details, and running, previewing and history.</summary>
    public sealed partial class MainWindow : Window
    {
        private readonly ContentControl _page = new ContentControl { Focusable = false };
        private readonly Grid _footer = new Grid();
        private readonly Button _update = Ui.Link(string.Empty, (s, e) => AppInfo.OpenUrl(AppInfo.ReleasesUrl + "/latest"));
        private readonly Button _import;
        private readonly ListBox _list = new ListBox { SelectionMode = SelectionMode.Single }.Styled("TableList");
        private readonly ContentControl _details = new ContentControl { Focusable = false };
        private Grid _pairsPage;
        private List<FolderPair> _pairs = new List<FolderPair>();
        private List<RunResult> _runs = new List<RunResult>();
        private FolderPair _selected;
        private bool _filling;
        private readonly DispatcherTimer _refresh = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        private FileSystemWatcher _runsWatcher;

        public MainWindow()
        {
            Width = 1100;
            Height = 720;
            MinWidth = 900;
            MinHeight = 600;
            Dialogs.Prepare(this, "KUDUSYNC");

            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition());
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var header = Ui.Columns(Ui.Auto, Ui.Star(), Ui.Auto, Ui.Auto, Ui.Auto, Ui.Auto, Ui.Auto);
            header.Margin = new Thickness(16, 10, 12, 10);
            var brand = Ui.Row(LogoTile.Create(26), Ui.Text("KUDUSYNC", 15, bold: true).Margin(10, 0, 0, 0));
            brand.Cursor = System.Windows.Input.Cursors.Hand;
            brand.ToolTip = "Folder pairs";
            brand.MouseLeftButtonUp += (s, e) => { if (!IsBusy) ShowPairs(); };
            header.Children.Add(brand.At(0, 0));
            _update.Visibility = Visibility.Collapsed;
            header.Children.Add(_update.Margin(0, 0, 14, 0).At(0, 2));
            _import = Ui.Button("Import from SyncToy", "IconImport", "GhostButton", (s, e) => ImportFromSyncToy());
            _import.Visibility = SyncToyImport.Available ? Visibility.Visible : Visibility.Collapsed;
            header.Children.Add(_import.At(0, 3));
            header.Children.Add(Ui.Button("History", "IconHistory", "GhostButton", (s, e) => { if (!IsBusy) ShowHistory(); }).At(0, 4));
            header.Children.Add(Ui.Button("Settings", "IconSettings", "GhostButton", (s, e) => new SettingsWindow(this).ShowDialog()).At(0, 5));
            header.Children.Add(Ui.Button("About", "IconInfo", "GhostButton", (s, e) => About.Show(this)).At(0, 6));
            root.Children.Add(new Border { Child = header, BorderThickness = new Thickness(0, 0, 0, 1) }
                .Res(Border.BackgroundProperty, "SurfaceBg").Res(Border.BorderBrushProperty, "BorderBrush").At(0));

            root.Children.Add(_page.At(1));
            root.Children.Add(new Border { Child = _footer, Padding = new Thickness(16, 12, 16, 12), BorderThickness = new Thickness(0, 1, 0, 0) }
                .Res(Border.BackgroundProperty, "SurfaceBg").Res(Border.BorderBrushProperty, "BorderBrush").At(2));
            Content = root;

            _list.SelectionChanged += (s, e) =>
            {
                if (_filling || !(_list.SelectedItem is ListBoxItem item) || !(item.Tag is FolderPair pair)) return;
                _selected = pair;
                Settings.LastPair = pair.Id;
                ShowDetails();
            };

            LoadPairs();
            ShowPairs();
            WatchRuns();
            Loaded += async (s, e) =>
            {
                await CheckForUpdates();
                await Task.Run(() => Schedules.Refresh(_pairs));
            };
            Activated += (s, e) => { if (_page.Content == _pairsPage) RefreshRuns(); };
            Closing += (s, e) =>
            {
                if (IsBusy && !Dialogs.Confirm(this, "A sync is still running. Stop it and close KUDUSYNC?"))
                {
                    e.Cancel = true;
                    return;
                }
                _cancel?.Cancel();
            };
            Closed += (s, e) => _runsWatcher?.Dispose();
        }

        public IReadOnlyList<FolderPair> Pairs => _pairs;
        public FolderPair Selected => _selected;
        public object CurrentPage => _page.Content;
        public string PageName { get; private set; }

        public void BringToFront()
        {
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
            Topmost = true;
            Topmost = false;
            Focus();
        }

        /// <summary>Acts on a command line (from starting KUDUSYNC, or forwarded from a second start).</summary>
        public void HandleCommandLine(string[] args)
        {
            string Value(string name)
            {
                for (var i = 0; i < args.Length - 1; i++)
                    if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
                return null;
            }
            bool Has(string name) => args.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
            if (IsBusy) return;

            var newPair = Value("--new-pair");
            var syncFolder = Value("--sync-folder");
            if (newPair != null) NewPair(Paths.Normalize(newPair));
            else if (syncFolder != null) SyncFolder(Paths.Normalize(syncFolder));
            else if (Has("--history")) ShowHistory();
        }

        // ================================================================== frame

        internal void SetFooter(UIElement left, params Button[] buttons)
        {
            _footer.Children.Clear();
            _footer.ColumnDefinitions.Clear();
            _footer.ColumnDefinitions.Add(new ColumnDefinition());
            if (left != null) _footer.Children.Add(left.At(0, 0));
            for (var i = 0; i < buttons.Length; i++)
            {
                _footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                buttons[i].Margin = new Thickness(8, 0, 0, 0);
                _footer.Children.Add(buttons[i].At(0, i + 1));
            }
        }

        private void SetPage(UIElement page, string name)
        {
            _page.Content = page;
            PageName = name;
        }

        private async Task CheckForUpdates()
        {
            if (!Settings.CheckForUpdates || SelfTest.Running) return;
            try
            {
                var info = await UpdateChecker.CheckAsync();
                if (info == null || !info.IsNewer || Settings.SkippedVersion == info.Latest.ToString(3)) return;
                _update.Content = $"KUDUSYNC {info.Latest.ToString(3)} is available";
                _update.Visibility = Visibility.Visible;
            }
            catch
            {
                // No internet: try again next time.
            }
        }

        // ================================================================== pairs

        private void LoadPairs()
        {
            _pairs = DataStore.LoadPairs();
            _runs = RunLog.All();
            if (_selected != null) _selected = _pairs.FirstOrDefault(p => p.Id == _selected.Id);
            if (_selected == null) _selected = _pairs.FirstOrDefault(p => p.Id == Settings.LastPair) ?? _pairs.FirstOrDefault();
        }

        private void SavePairs()
        {
            DataStore.SavePairs(_pairs);
        }

        private void WatchRuns()
        {
            // Scheduled runs and the tray write to the run list: refresh the pair's "last run" when that happens.
            _refresh.Tick += (s, e) =>
            {
                _refresh.Stop();
                if (_page.Content == _pairsPage && !IsBusy) RefreshRuns();
            };
            try
            {
                Directory.CreateDirectory(DataStore.Folder);
                _runsWatcher = new FileSystemWatcher(DataStore.Folder, "runs.json") { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName };
                FileSystemEventHandler changed = (s, e) => Dispatcher.BeginInvoke(new Action(() => { _refresh.Stop(); _refresh.Start(); }));
                _runsWatcher.Changed += changed;
                _runsWatcher.Created += changed;
                _runsWatcher.EnableRaisingEvents = true;
            }
            catch (Exception ex) when (ex is IOException || ex is ArgumentException) { }
        }

        private void RefreshRuns()
        {
            _runs = RunLog.All();
            FillList();
            ShowDetails();
        }

        public void ShowPairs()
        {
            if (_pairs.Count == 0)
            {
                ShowWelcome();
                return;
            }

            if (_pairsPage == null)
            {
                _pairsPage = new Grid { Margin = new Thickness(16) };
                _pairsPage.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(330) });
                _pairsPage.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
                _pairsPage.ColumnDefinitions.Add(new ColumnDefinition());

                var head = Ui.Columns(Ui.Star(), Ui.Auto);
                head.Children.Add(Ui.Heading("Folder pairs", 15).At(0, 0));
                head.Children.Add(Ui.Small(Ui.Button("New pair", "IconPlus", "SecondaryButton", (s, e) => NewPair(null))).At(0, 1));
                var left = new Grid();
                left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                left.RowDefinitions.Add(new RowDefinition());
                left.Children.Add(head.Margin(0, 0, 0, 10).At(0));
                left.Children.Add(_list.At(1));
                _pairsPage.Children.Add(Ui.Card(left, 14).At(0, 0));

                var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _details, Focusable = false };
                _pairsPage.Children.Add(scroll.At(0, 2));
            }

            _runs = RunLog.All();
            FillList();
            ShowDetails();
            SetPage(_pairsPage, "pairs");
            UpdatePairsFooter();
        }

        private void UpdatePairsFooter()
        {
            var next = _pairs.Select(p => new { Pair = p, When = p.Schedule.NextAfter(DateTime.Now) }).Where(x => x.When != null).OrderBy(x => x.When).FirstOrDefault();
            var text = Format.Count(_pairs.Count, "pair") + (next != null ? $" · next scheduled: {next.Pair.Name}, {When(next.When.Value)}" : string.Empty);
            var runAll = Ui.Button("Sync all", "IconSync", "SecondaryButton", (s, e) => RunAll());
            runAll.IsEnabled = _pairs.Count(p => p.InRunAll) > 0;
            runAll.ToolTip = "Runs every pair that's included in \"Sync all\", one after another";
            SetFooter(Ui.Subtle(text, 12.5, false), runAll);
        }

        private static string When(DateTime t)
        {
            var day = t.Date == DateTime.Today ? "today" : t.Date == DateTime.Today.AddDays(1) ? "tomorrow" : t.ToString("ddd d MMM");
            return day + " at " + t.ToString("HH:mm");
        }

        private void FillList()
        {
            _filling = true;
            _list.Items.Clear();
            foreach (var pair in _pairs)
            {
                var last = RunLog.Latest(_runs, pair.Id);
                var dot = Ui.Dot(StatusBrush(last), 9).Margin(2, 6, 10, 0);
                dot.VerticalAlignment = VerticalAlignment.Top;
                dot.ToolTip = last == null ? "Not run yet" : last.StatusText + " · " + last.Started.ToString("g");
                var lines = Ui.Column(
                    Ui.Text(pair.Name, 13.5, bold: true),
                    Ui.Subtle(FolderPair.ModeName(pair.Mode) + (pair.Schedule.Kind != ScheduleKind.None ? " · " + pair.Schedule.Describe() : string.Empty) + (pair.LiveSync ? " · Live" : string.Empty), 12, false),
                    Ui.Subtle(Short(pair.Left.Path) + (pair.IsOneWay ? "  →  " : "  ⇄  ") + Short(pair.Right.Path), 11.5, false));
                var row = Ui.Columns(Ui.Auto, Ui.Star());
                row.Children.Add(dot.At(0, 0));
                row.Children.Add(lines.At(0, 1));
                var item = new ListBoxItem { Content = row, Tag = pair, Padding = new Thickness(6, 9, 6, 9) };
                item.MouseDoubleClick += (s, e) => EditPair(pair);
                _list.Items.Add(item);
                if (pair == _selected) _list.SelectedItem = item;
            }
            _filling = false;
        }

        private static string Short(string path)
        {
            if (string.IsNullOrEmpty(path)) return "?";
            return path.Length <= 34 ? path : path.Substring(0, 14) + "…" + path.Substring(path.Length - 18);
        }

        private static string StatusBrush(RunResult r)
        {
            if (r == null) return "OffDot";
            switch (r.Status)
            {
                case RunStatus.Ok: return "OkText";
                case RunStatus.Failed: return "BadText";
                case RunStatus.Skipped: return r.Message != null && r.Message.StartsWith("Paused") ? "WarnText" : "OffDot";
                default: return "WarnText";
            }
        }

        private static Tone StatusTone(RunResult r)
        {
            switch (r.Status)
            {
                case RunStatus.Ok: return Tone.Ok;
                case RunStatus.Failed: return Tone.Bad;
                case RunStatus.Skipped: return Tone.Neutral;
                default: return Tone.Warn;
            }
        }

        private void ShowWelcome()
        {
            var content = Ui.Column(
                LogoTile.Create(72),
                Ui.Heading("Keep your folders in step", 22).Margin(0, 20, 0, 0),
                Ui.Subtle("Pick two folders (on this PC, a USB drive or a network share) and how to keep them in step: mirror, update, two-way sync or move. " +
                          "Run them when you like, on a schedule, or as files change.", 13).Margin(0, 8, 0, 0));
            ((FrameworkElement)content.Children[0]).HorizontalAlignment = HorizontalAlignment.Left;
            var buttons = Ui.Row(Ui.Button("Create a folder pair", "IconPlus", "PrimaryButton", (s, e) => NewPair(null)));
            if (SyncToyImport.Available) buttons.Children.Add(Ui.Button("Import from SyncToy", "IconImport", "SecondaryButton", (s, e) => ImportFromSyncToy()).Margin(10, 0, 0, 0));
            content.Children.Add(buttons.Margin(0, 22, 0, 0));
            var card = Ui.Card(content, 32);
            card.MaxWidth = 560;
            card.HorizontalAlignment = HorizontalAlignment.Center;
            card.VerticalAlignment = VerticalAlignment.Center;
            SetPage(card, "welcome");
            SetFooter(Ui.Subtle("No folder pairs yet", 12.5, false));
        }

        // ================================================================== details

        private void ShowDetails()
        {
            var pair = _selected;
            if (pair == null)
            {
                _details.Content = null;
                return;
            }
            var last = RunLog.Latest(_runs, pair.Id);
            var leftNow = Locations.Resolve(pair.Left);
            var rightNow = Locations.Resolve(pair.Right);

            var panel = new StackPanel();

            var title = Ui.Columns(Ui.Star(), Ui.Auto, Ui.Auto);
            title.Children.Add(Ui.Row(Ui.Heading(pair.Name, 20), Ui.Chip(FolderPair.ModeName(pair.Mode), Tone.Accent, pair.IsOneWay ? "IconArrowRight" : "IconTwoWay").Margin(12, 0, 0, 0)).At(0, 0));
            title.Children.Add(Ui.Small(Ui.Button("Edit", "IconEdit", "SecondaryButton", (s, e) => EditPair(pair))).At(0, 1));
            var more = Ui.Small(Ui.Button(string.Empty, "IconMore", "GhostButton"));
            more.ToolTip = "More";
            more.Click += (s, e) => MoreMenu(pair, more);
            title.Children.Add(more.Margin(6, 0, 0, 0).At(0, 2));
            panel.Children.Add(title);
            panel.Children.Add(Ui.Subtle(FolderPair.ModeDescription(pair.Mode), 12.5).Margin(0, 6, 0, 0));

            var sides = Ui.Columns(Ui.Star(), Ui.Auto, Ui.Star());
            sides.Margin = new Thickness(0, 16, 0, 0);
            sides.Children.Add(SideCard(pair.LeftTitle, pair.Left, leftNow).At(0, 0));
            var arrow = Ui.Icon(pair.IsOneWay ? "IconArrowRight" : "IconTwoWay", 22).Margin(12, 0, 12, 0);
            arrow.Res(TextElement.ForegroundProperty, "Accent");
            sides.Children.Add(arrow.At(0, 1));
            sides.Children.Add(SideCard(pair.RightTitle, pair.Right, rightNow).At(0, 2));
            panel.Children.Add(sides);

            var rows = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };
            void Row(string label, UIElement value)
            {
                var g = Ui.Columns(Ui.Px(150), Ui.Star());
                g.Children.Add(Ui.Subtle(label, 12.5, false).At(0, 0));
                g.Children.Add(value.At(0, 1));
                rows.Children.Add(new Border { BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 8, 0, 8), Child = g }.Res(Border.BorderBrushProperty, "BorderBrush"));
            }

            if (last != null)
            {
                var lastPanel = Ui.Column(
                    Ui.Row(Ui.Chip(last.StatusText, StatusTone(last)), Ui.Subtle($"{last.Started:g} · started by {last.Trigger.ToLowerInvariant()}", 12, false).Margin(8, 0, 0, 0)),
                    Ui.Text(last.Summary, 12.5, wrap: true).Margin(0, 4, 0, 0));
                foreach (var p in last.Problems.Take(3)) lastPanel.Children.Add(Ui.Subtle("• " + p, 12).Margin(0, 2, 0, 0));
                if (!string.IsNullOrEmpty(last.LogFile) && File.Exists(last.LogFile))
                    lastPanel.Children.Add(Ui.Link("Open the log", (s, e) => AppInfo.OpenFile(last.LogFile)).Margin(0, 4, 0, 0));
                Row("Last run", lastPanel);
            }
            else Row("Last run", Ui.Subtle("Not run yet", 12.5, false));

            var schedule = pair.Schedule.Describe();
            var next = pair.Schedule.NextAfter(DateTime.Now);
            if (next != null) schedule += " · next " + When(next.Value);
            Row("Runs", Ui.Text(schedule, 12.5, wrap: true));
            if (pair.LiveSync)
                Row("Live sync", Ui.Text($"On: syncs {Delay(pair.LiveDelaySeconds)} after files change" + (ShellIntegration.StartsWithWindows || Settings.TrayAtStartup ? string.Empty : " (needs KUDUSYNC in the notification area: see Settings)"), 12.5, wrap: true));
            var drive = new[] { pair.Left, pair.Right }.FirstOrDefault(l => !string.IsNullOrEmpty(l.VolumeSerial) && !IsSystemDrive(l));
            if (drive != null && pair.NotifyOnPlug)
                Row("Drive plugged in", Ui.Text($"Shows a notification when {(string.IsNullOrEmpty(drive.VolumeLabel) ? "the drive" : drive.VolumeLabel)} is plugged in", 12.5, wrap: true));
            Row("Files", Ui.Text(FilterSummary(pair.Filters), 12.5, wrap: true));
            var keep = Ui.Row(Ui.Text(pair.KeepText, 12.5));
            if (pair.KeepDays >= 0)
            {
                var keptOn = pair.Mode == SyncMode.TwoWay ? new[] { Side.Left, Side.Right } : new[] { Side.Right };
                foreach (var side in keptOn)
                {
                    var now = side == Side.Left ? leftNow : rightNow;
                    if (!now.Ok) continue;
                    var folder = History.BaseFolder(pair, now.Root, side);
                    if (!Directory.Exists(Paths.Long(folder))) continue;
                    var label = keptOn.Length == 1 ? "Open" : "Open (" + (side == Side.Left ? pair.LeftTitle : pair.RightTitle) + ")";
                    keep.Children.Add(Ui.Link(label, (s, e) => AppInfo.OpenFolder(folder)).Margin(10, 0, 0, 0));
                }
            }
            Row("Replaced and deleted files", keep);
            var checks = new List<string>();
            if (pair.Verify) checks.Add("every copy is checked against the original");
            if (pair.DetectRenames) checks.Add("renamed files are renamed, not copied again");
            if (!pair.InRunAll) checks.Add("not included in \"Sync all\"");
            if (checks.Count > 0) Row("Also", Ui.Text(char.ToUpper(checks[0][0]) + string.Join("; ", checks).Substring(1), 12.5, wrap: true));
            panel.Children.Add(rows);

            var actions = Ui.Row(
                Ui.Button("Sync now", "IconSync", "PrimaryButton", (s, e) => RunPairs(new List<FolderPair> { pair }, false)),
                Ui.Button("Preview", "IconEye", "SecondaryButton", (s, e) => RunPairs(new List<FolderPair> { pair }, true)).Margin(8, 0, 0, 0));
            if (PairLock.IsRunning(pair.Id))
            {
                foreach (Button b in actions.Children) b.IsEnabled = false;
                actions.Children.Add(Ui.Subtle("Running elsewhere right now (on a schedule or in the notification area)…", 12, false).Margin(12, 0, 0, 0));
            }
            panel.Children.Add(actions.Margin(0, 18, 0, 0));

            _details.Content = Ui.Card(panel, 22);
        }

        private static bool IsSystemDrive(Location l) =>
            string.Equals(Paths.DriveOf(l.Path), Paths.DriveOf(Environment.GetFolderPath(Environment.SpecialFolder.Windows)), StringComparison.OrdinalIgnoreCase);

        public static string Delay(int seconds) => seconds < 60 ? $"{seconds} seconds" : seconds == 60 ? "a minute" : $"{seconds / 60} minutes";

        public static string FilterSummary(Filters f)
        {
            var parts = new List<string>();
            var include = FileFilter.Split(f.Include);
            parts.Add(include.Count == 0 || (include.Count == 1 && include[0] == "*") ? "All files" : "Only " + string.Join(", ", include));
            var exclude = FileFilter.Split(f.Exclude);
            if (exclude.Count > 0) parts.Add("except " + string.Join(", ", exclude));
            var folders = FileFilter.Split(f.ExcludeFolders);
            if (folders.Count > 0) parts.Add("leaving out the folders " + string.Join(", ", folders));
            if (f.SkipHidden) parts.Add("skipping hidden files");
            if (f.SkipSystem) parts.Add("skipping system files");
            return string.Join("; ", parts);
        }

        private UIElement SideCard(string title, Location location, ResolvedLocation now)
        {
            var icon = location.IsNetwork ? "IconServer" : now.Volume?.Type == DriveType.Removable ? "IconUsb" : "IconDrive";
            var head = Ui.Row(Ui.Icon(icon, 16), Ui.Text(title.ToUpperInvariant(), 11.5, "SubtleText", bold: true).Margin(8, 0, 0, 0));
            var path = Ui.Text(Paths.Display(now.Ok ? now.Root : location.Path), 13, bold: true, wrap: true).Margin(0, 8, 0, 0);
            var status = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
            if (!now.Ok)
            {
                status.Children.Add(Ui.Chip(now.NotConnected ? "Not connected" : "Problem", Tone.Warn, "IconWarn"));
                status.Children.Add(Ui.Subtle(now.Problem, 12).Margin(0, 4, 0, 0));
            }
            else
            {
                var details = Locations.Describe(location, now);
                if (!location.IsNetwork)
                {
                    var free = Locations.FreeSpace(now.Root.Length >= 3 ? now.Root.Substring(0, 3) : now.Root);
                    if (free >= 0) details += " · " + Format.Size(free) + " free";
                }
                else details += NetworkShares.HasLogin(location.Path) ? " · signed in as " + NetworkShares.LoadLogin(location.Path).User : string.Empty;
                status.Children.Add(Ui.Subtle(details, 12));
                if (!Directory.Exists(Paths.Long(now.Root)))
                    status.Children.Add(Ui.Subtle("The folder will be created when the pair runs.", 12).Margin(0, 2, 0, 0));
                else
                    status.Children.Add(Ui.Link("Open", (s, e) => AppInfo.OpenFolder(now.Root)).Margin(0, 4, 0, 0));
            }
            return new Border
            {
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(14, 12, 14, 12),
                Child = Ui.Column(head, path, status)
            }.Res(Border.BorderBrushProperty, "BorderBrush").Res(Border.BackgroundProperty, "SurfaceAltBg");
        }

        private void MoreMenu(FolderPair pair, Button anchor)
        {
            var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom };
            void Item(string text, Action action, bool enabled = true)
            {
                var item = new MenuItem { Header = text, IsEnabled = enabled };
                item.Click += (s, e) => action();
                menu.Items.Add(item);
            }
            Item("Duplicate", () => DuplicatePair(pair));
            Item("Forget what was synced (start afresh)", () => ForgetRecord(pair), File.Exists(DataStore.RecordFile(pair.Id)));
            menu.Items.Add(new Separator());
            Item("Delete pair", () => DeletePair(pair));
            menu.IsOpen = true;
        }

        // ================================================================== editing pairs

        public void NewPair(string source)
        {
            var pair = new FolderPair();
            if (!string.IsNullOrEmpty(source))
            {
                pair.Left = Locations.FromPath(source);
                pair.Name = Path.GetFileName(source.TrimEnd('\\')) is string n && n.Length > 0 ? n + " backup" : "Drive " + Paths.DriveOf(source) + " backup";
            }
            var editor = new PairEditor(this, pair, _pairs, true);
            if (editor.ShowDialog() != true) return;
            _pairs.Add(editor.Result);
            _selected = editor.Result;
            AfterSave(editor.Result);
        }

        public void EditPair(FolderPair pair)
        {
            if (IsBusy) return;
            var editor = new PairEditor(this, pair.Clone(), _pairs, false);
            if (editor.ShowDialog() != true) return;
            var index = _pairs.FindIndex(p => p.Id == pair.Id);
            if (index < 0) return;
            _pairs[index] = editor.Result;
            _selected = editor.Result;
            AfterSave(editor.Result);
        }

        /// <summary>Saves the pairs, sets up the pair's schedule and starts the tray if the pair needs it.</summary>
        internal void AfterSave(FolderPair pair)
        {
            SavePairs();
            var problem = Schedules.Apply(pair);
            if (problem != null) Dialogs.Error(this, "The pair was saved, but its schedule couldn't be set up.\n\n" + problem);
            if ((pair.LiveSync || pair.NotifyOnPlug && new[] { pair.Left, pair.Right }.Any(l => !string.IsNullOrEmpty(l.VolumeSerial) && !IsSystemDrive(l))) && Settings.TrayAtStartup)
                Tray.StartRunning();
            ShowPairs();
        }

        private void DuplicatePair(FolderPair pair)
        {
            var copy = pair.Clone();
            copy.Id = Guid.NewGuid().ToString("N");
            copy.Name = pair.Name + " (copy)";
            copy.Created = DateTime.UtcNow;
            copy.Schedule.Kind = ScheduleKind.None;
            copy.LiveSync = false;
            _pairs.Insert(_pairs.IndexOf(pair) + 1, copy);
            _selected = copy;
            SavePairs();
            ShowPairs();
        }

        private void ForgetRecord(FolderPair pair)
        {
            if (!Dialogs.Confirm(this, $"Forget what \"{pair.Name}\" last synced?\n\nThe next run is then treated as the first one: nothing is deleted, files that differ are kept on both sides, and renamed files are copied again.")) return;
            DataStore.DeleteRecord(pair.Id);
            ShowDetails();
        }

        public void DeletePair(FolderPair pair, bool ask = true)
        {
            if (ask && !Dialogs.Confirm(this, $"Delete the pair \"{pair.Name}\"?\n\nNo files are deleted: both folders stay as they are.")) return;
            _pairs.Remove(pair);
            SavePairs();
            Schedules.Remove(pair.Id);
            DataStore.DeleteRecord(pair.Id);
            if (pair.Left.IsNetwork && !_pairs.Any(p => SameShare(p, pair.Left.Path))) NetworkShares.DeleteLogin(pair.Left.Path);
            if (pair.Right.IsNetwork && !_pairs.Any(p => SameShare(p, pair.Right.Path))) NetworkShares.DeleteLogin(pair.Right.Path);
            if (_selected == pair) _selected = _pairs.FirstOrDefault();
            ShowPairs();
        }

        private static bool SameShare(FolderPair p, string path)
        {
            var share = Paths.ShareRoot(path);
            return string.Equals(Paths.ShareRoot(p.Left.Path), share, StringComparison.OrdinalIgnoreCase) || string.Equals(Paths.ShareRoot(p.Right.Path), share, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Right-click "Sync with KUDUSYNC": runs the pairs using the folder, or offers to create one.</summary>
        public void SyncFolder(string folder)
        {
            var using_ = _pairs.Where(p => new[] { p.Left, p.Right }.Any(l =>
            {
                var r = Locations.Resolve(l);
                return r.Ok && string.Equals(Paths.Normalize(r.Root), folder, StringComparison.OrdinalIgnoreCase);
            })).ToList();
            if (using_.Count == 0)
            {
                if (SelfTest.Running || Dialogs.Confirm(this, $"No folder pair uses {folder} yet.\n\nCreate one to back it up?")) NewPair(folder);
                return;
            }
            RunPairs(using_, false, Core.Triggers.RightClick);
        }

        private void ImportFromSyncToy()
        {
            if (IsBusy) return;
            var window = new ImportWindow(this, _pairs);
            if (window.ShowDialog() != true || window.Imported.Count == 0) return;
            _pairs.AddRange(window.Imported);
            _selected = window.Imported[0];
            SavePairs();
            ShowPairs();
            Dialogs.Info(this, $"Imported {Format.Count(window.Imported.Count, "pair")}. Check each one before running it: SyncToy's last-sync information can't be imported, so the first run of a two-way pair keeps both copies of files that differ.");
        }
    }
}
