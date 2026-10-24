using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using Kudusync.Controls;
using Kudusync.Core;

namespace Kudusync.Views
{
    /// <summary>Creating or changing a folder pair.</summary>
    public sealed class PairEditor : Window
    {
        private static readonly (int Days, string Text)[] KeepChoices =
        {
            (7, "Keep for 7 days"), (30, "Keep for 30 days"), (90, "Keep for 90 days"), (365, "Keep for a year"), (0, "Keep forever"), (-1, "Don't keep them (delete permanently)")
        };
        private static readonly (ScheduleKind Kind, string Text)[] ScheduleChoices =
        {
            (ScheduleKind.None, "Only when I run it"), (ScheduleKind.Daily, "Every day"), (ScheduleKind.Weekly, "On certain days"),
            (ScheduleKind.EveryHours, "Every few hours"), (ScheduleKind.AtLogon, "When I sign in to Windows")
        };
        private static readonly int[] HourChoices = { 1, 2, 3, 4, 6, 8, 12 };
        private static readonly int[] DelayChoices = { 10, 30, 60, 300 };

        private readonly FolderPair _pair;
        private readonly List<FolderPair> _others;
        private readonly bool _isNew;

        private readonly TextBox _name = new TextBox { MinHeight = 34 };
        private readonly LocationPicker _left;
        private readonly LocationPicker _right;
        private readonly Dictionary<SyncMode, RadioButton> _modes = new Dictionary<SyncMode, RadioButton>();
        private readonly TextBox _include = new TextBox { MinHeight = 32 };
        private readonly TextBox _exclude = new TextBox { MinHeight = 32 };
        private readonly TextBox _excludeFolders = new TextBox { MinHeight = 32 };
        private readonly CheckBox _skipHidden = new CheckBox { Content = "Skip hidden files and folders" };
        private readonly CheckBox _skipSystem = new CheckBox { Content = "Skip Windows system files", Margin = new Thickness(0, 6, 0, 0) };
        private readonly ComboBox _keep = new ComboBox { MinWidth = 260, HorizontalAlignment = HorizontalAlignment.Left };
        private readonly CheckBox _customHistory = new CheckBox { Content = "Keep them in a folder of my choice", Margin = new Thickness(0, 8, 0, 0) };
        private readonly TextBox _historyFolder = new TextBox { MinHeight = 32 };
        private readonly CheckBox _verify = new CheckBox { Content = "Check every copy against the original (slower, catches failing drives)" };
        private readonly CheckBox _renames = new CheckBox { Content = "When a file is renamed or moved, rename it on the other side instead of copying it again", Margin = new Thickness(0, 6, 0, 0) };
        private readonly ComboBox _schedule = new ComboBox { MinWidth = 220 };
        private readonly TextBox _time = new TextBox { MinHeight = 32, Width = 70, HorizontalContentAlignment = HorizontalAlignment.Center };
        private readonly ComboBox _hours = new ComboBox { MinWidth = 120 };
        private readonly ToggleButton[] _days = new ToggleButton[7];
        private readonly StackPanel _timeRow;
        private readonly StackPanel _dayRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        private readonly CheckBox _live = new CheckBox { Content = "Live sync: sync a short while after files change" };
        private readonly ComboBox _delay = new ComboBox { MinWidth = 130, Margin = new Thickness(10, 0, 0, 0) };
        private readonly CheckBox _notify = new CheckBox { Content = "Show a notification when the drive is plugged in", Margin = new Thickness(0, 8, 0, 0) };
        private readonly CheckBox _inRunAll = new CheckBox { Content = "Include in \"Sync all\"", Margin = new Thickness(0, 8, 0, 0) };
        private readonly ContentControl _trayHint = new ContentControl { Focusable = false };

        public FolderPair Result { get; private set; }

        public PairEditor(Window owner, FolderPair pair, List<FolderPair> all, bool isNew)
        {
            _pair = pair;
            _others = all.Where(p => p.Id != pair.Id).ToList();
            _isNew = isNew;
            Width = 780;
            Height = 760;
            MinHeight = 500;
            ShowInTaskbar = false;
            Dialogs.Prepare(this, (isNew ? "New folder pair" : "Edit " + pair.Name) + " · KUDUSYNC", owner);

            _left = new LocationPicker(this, pair.Left);
            _right = new LocationPicker(this, pair.Right);
            _left.Changed += UpdateDriveOptions;
            _right.Changed += UpdateDriveOptions;

            var body = new StackPanel { Margin = new Thickness(26, 22, 26, 22) };
            body.Children.Add(Ui.Heading(isNew ? "New folder pair" : "Edit folder pair", 20));

            body.Children.Add(Label("Name"));
            _name.Text = pair.Name;
            body.Children.Add(_name);

            // ---- folders
            body.Children.Add(Section("Folders"));
            body.Children.Add(_left);
            var swap = Ui.Link("⇅  Swap", (s, e) =>
            {
                var a = _left.Value;
                _left.Value = _right.Value;
                _right.Value = a;
            });
            swap.HorizontalAlignment = HorizontalAlignment.Left;
            body.Children.Add(swap.Margin(0, 6, 0, 6));
            body.Children.Add(_right);

            // ---- mode
            body.Children.Add(Section("What to do"));
            var modes = new UniformGrid { Columns = 2 };
            foreach (var mode in new[] { SyncMode.Mirror, SyncMode.Update, SyncMode.TwoWay, SyncMode.Move })
            {
                var icon = mode == SyncMode.TwoWay ? "IconTwoWay" : mode == SyncMode.Move ? "IconMove" : "IconArrowRight";
                var card = new RadioButton
                {
                    GroupName = "mode" + pair.Id,
                    IsChecked = pair.Mode == mode,
                    Content = Ui.Column(Ui.Row(Ui.Icon(icon, 16), Ui.Text(FolderPair.ModeName(mode), 13.5, bold: true).Margin(8, 0, 0, 0)),
                        Ui.Subtle(FolderPair.ModeDescription(mode), 12).Margin(0, 4, 0, 0)),
                    Margin = new Thickness(0, 0, mode == SyncMode.Mirror || mode == SyncMode.TwoWay ? 8 : 0, 8)
                }.Styled("OptionCard");
                card.Checked += (s, e) => UpdateTitles();
                _modes[mode] = card;
                modes.Children.Add(card);
            }
            body.Children.Add(modes);

            // ---- files
            body.Children.Add(Section("Which files"));
            body.Children.Add(Label("Include files matching (separate with ;)"));
            _include.Text = pair.Filters.Include;
            body.Children.Add(_include);
            body.Children.Add(Label("Leave out files matching"));
            _exclude.Text = pair.Filters.Exclude;
            body.Children.Add(_exclude);
            body.Children.Add(Label("Leave out folders (names like node_modules, or paths inside the pair like Projects\\bin)"));
            _excludeFolders.Text = pair.Filters.ExcludeFolders;
            body.Children.Add(_excludeFolders);
            _skipHidden.IsChecked = pair.Filters.SkipHidden;
            _skipSystem.IsChecked = pair.Filters.SkipSystem;
            body.Children.Add(_skipHidden.Margin(0, 10, 0, 0));
            body.Children.Add(_skipSystem);
            body.Children.Add(Ui.Subtle("Files that are left out are never copied or deleted, on either side.", 12).Margin(0, 6, 0, 0));

            // ---- safety
            body.Children.Add(Section("Replaced and deleted files"));
            body.Children.Add(Ui.Subtle("Files a sync replaces or deletes are moved to a \"_KUDUSYNC History\" folder on that drive first, so a mistake can be undone.", 12).Margin(0, 0, 0, 8));
            foreach (var c in KeepChoices) _keep.Items.Add(c.Text);
            var keepIndex = Array.FindIndex(KeepChoices, c => c.Days == pair.KeepDays);
            if (keepIndex < 0)
            {
                _keep.Items.Insert(0, pair.KeepDays + " days");
                keepIndex = 0;
            }
            _keep.SelectedIndex = keepIndex;
            body.Children.Add(_keep);
            _customHistory.IsChecked = !string.IsNullOrWhiteSpace(pair.HistoryFolder);
            _historyFolder.Text = pair.HistoryFolder;
            body.Children.Add(_customHistory);
            var historyRow = Ui.Columns(Ui.Star(), Ui.Auto);
            historyRow.Children.Add(_historyFolder.At(0, 0));
            historyRow.Children.Add(Ui.Button("Browse…", null, "SecondaryButton", (s, e) =>
            {
                var folder = Dialogs.PickFolder(this, "Choose where to keep replaced and deleted files", _historyFolder.Text);
                if (folder != null) _historyFolder.Text = folder;
            }).Margin(8, 0, 0, 0).At(0, 1));
            historyRow.Margin = new Thickness(27, 6, 0, 0);
            body.Children.Add(historyRow);
            void UpdateHistory()
            {
                var keeping = KeepDays() >= 0;
                _customHistory.IsEnabled = keeping;
                historyRow.IsEnabled = keeping && _customHistory.IsChecked == true;
            }
            _customHistory.Click += (s, e) => UpdateHistory();
            _keep.SelectionChanged += (s, e) => UpdateHistory();
            UpdateHistory();

            body.Children.Add(Section("Checks"));
            _verify.IsChecked = pair.Verify;
            _renames.IsChecked = pair.DetectRenames;
            body.Children.Add(_verify);
            body.Children.Add(_renames);

            // ---- automatic
            body.Children.Add(Section("Running automatically"));
            foreach (var c in ScheduleChoices) _schedule.Items.Add(c.Text);
            _schedule.SelectedIndex = Array.FindIndex(ScheduleChoices, c => c.Kind == pair.Schedule.Kind);
            _time.Text = DateTime.Today.Add(pair.Schedule.TimeOfDay).ToString("HH:mm");
            foreach (var h in HourChoices) _hours.Items.Add(h == 1 ? "Every hour" : $"Every {h} hours");
            _hours.SelectedIndex = Math.Max(0, Array.IndexOf(HourChoices, pair.Schedule.Hours));
            _timeRow = Ui.Row(_schedule, Ui.Subtle("at", 12.5, false).Margin(12, 0, 8, 0), _time, _hours.Margin(12, 0, 0, 0));
            body.Children.Add(_timeRow);
            for (var i = 0; i < 7; i++)
            {
                var day = (i + 1) % 7; // Monday first
                _days[day] = new ToggleButton { Content = Schedule.ShortDays[day], IsChecked = (pair.Schedule.Days & (1 << day)) != 0, MinWidth = 52 }.Styled("ChipToggle");
                _dayRow.Children.Add(_days[day]);
            }
            body.Children.Add(_dayRow);
            _schedule.SelectionChanged += (s, e) => UpdateSchedule();
            body.Children.Add(Ui.Subtle("Schedules use Windows Task Scheduler, so they run even when KUDUSYNC is closed. If the PC was off, the sync runs as soon as it's back on.", 12).Margin(0, 8, 0, 0));

            _live.IsChecked = pair.LiveSync;
            foreach (var d in DelayChoices) _delay.Items.Add(MainWindow.Delay(d) + " later");
            var delayIndex = Array.IndexOf(DelayChoices, pair.LiveDelaySeconds);
            if (delayIndex < 0)
            {
                _delay.Items.Add(MainWindow.Delay(pair.LiveDelaySeconds) + " later");
                delayIndex = _delay.Items.Count - 1;
            }
            _delay.SelectedIndex = delayIndex;
            body.Children.Add(Ui.Row(_live, _delay).Margin(0, 14, 0, 0));
            _notify.IsChecked = pair.NotifyOnPlug;
            body.Children.Add(_notify);
            _live.Click += (s, e) => UpdateTrayHint();
            _notify.Click += (s, e) => UpdateTrayHint();
            body.Children.Add(_trayHint);
            _inRunAll.IsChecked = pair.InRunAll;
            body.Children.Add(_inRunAll);

            var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = body, Focusable = false };
            var save = Ui.Button(isNew ? "Create pair" : "Save", null, "PrimaryButton");
            save.IsDefault = true;
            save.Click += (s, e) => Save();
            var cancel = Ui.Button("Cancel");
            cancel.IsCancel = true;
            var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            footer.Children.Add(cancel);
            footer.Children.Add(save.Margin(8, 0, 0, 0));
            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition());
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.Children.Add(scroll.At(0));
            root.Children.Add(new Border { Child = footer, Padding = new Thickness(16, 12, 16, 12), BorderThickness = new Thickness(0, 1, 0, 0) }
                .Res(Border.BackgroundProperty, "SurfaceBg").Res(Border.BorderBrushProperty, "BorderBrush").At(1));
            Content = root;

            UpdateTitles();
            UpdateSchedule();
            UpdateDriveOptions();
            Loaded += (s, e) =>
            {
                if (string.IsNullOrEmpty(_name.Text)) _name.Focus();
            };
        }

        private static TextBlock Label(string text) => Ui.Text(text, 12.5, "SubtleText").Margin(0, 12, 0, 5);

        private static UIElement Section(string text) => Ui.Heading(text, 15).Margin(0, 26, 0, 8);

        private SyncMode Mode => _modes.First(m => m.Value.IsChecked == true).Key;

        private int KeepDays()
        {
            var text = _keep.SelectedItem as string;
            var choice = KeepChoices.FirstOrDefault(c => c.Text == text);
            return choice.Text != null ? choice.Days : _pair.KeepDays;
        }

        private void UpdateTitles()
        {
            var oneWay = Mode != SyncMode.TwoWay;
            _left.Title = oneWay ? "Source" : "Folder 1";
            _right.Title = oneWay ? "Destination" : "Folder 2";
            _left.Hint = Mode == SyncMode.Move ? "Files are moved out of here." : oneWay ? "Copied from here. Nothing in it is changed." : null;
            _right.Hint = Mode == SyncMode.Mirror ? "Made into an exact copy of the source." : Mode == SyncMode.Update ? "Gets new and changed files. Nothing here is deleted." : Mode == SyncMode.Move ? "Files are moved to here." : null;
        }

        private void UpdateSchedule()
        {
            var kind = ScheduleChoices[Math.Max(0, _schedule.SelectedIndex)].Kind;
            var timed = kind == ScheduleKind.Daily || kind == ScheduleKind.Weekly;
            _time.Visibility = timed ? Visibility.Visible : Visibility.Collapsed;
            _timeRow.Children[1].Visibility = _time.Visibility;
            _hours.Visibility = kind == ScheduleKind.EveryHours ? Visibility.Visible : Visibility.Collapsed;
            _dayRow.Visibility = kind == ScheduleKind.Weekly ? Visibility.Visible : Visibility.Collapsed;
        }

        private bool HasOwnDrive()
        {
            var system = Paths.DriveOf(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
            return new[] { _left.Value, _right.Value }.Any(p => !string.IsNullOrWhiteSpace(p) && !Paths.IsUnc(p) &&
                                                                !string.Equals(Paths.DriveOf(p), system, StringComparison.OrdinalIgnoreCase));
        }

        private void UpdateDriveOptions()
        {
            _notify.Visibility = HasOwnDrive() ? Visibility.Visible : Visibility.Collapsed;
            UpdateTrayHint();
        }

        private void UpdateTrayHint()
        {
            var needs = _live.IsChecked == true || (_notify.IsChecked == true && _notify.Visibility == Visibility.Visible);
            if (!needs || Settings.TrayAtStartup)
            {
                _trayHint.Content = null;
                return;
            }
            var turnOn = Ui.Link("Turn it on", (s, e) =>
            {
                Settings.TrayAtStartup = true;
                ShellIntegration.SetStartWithWindows(true);
                Tray.StartRunning();
                UpdateTrayHint();
            });
            _trayHint.Content = Ui.Banner("IconInfo", Ui.Column(
                Ui.Text("Live sync and drive notifications need KUDUSYNC running in the notification area (it starts with Windows and uses very little).", 12.5, wrap: true),
                turnOn.Margin(0, 4, 0, 0))).Margin(0, 10, 0, 0);
        }

        private void Save()
        {
            var name = _name.Text.Trim();
            if (name.Length == 0)
            {
                Dialogs.Error(this, "Give the pair a name.");
                _name.Focus();
                return;
            }
            if (_others.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                Dialogs.Error(this, $"There's already a pair called \"{name}\".");
                return;
            }
            var leftPath = Paths.Normalize(_left.Value);
            var rightPath = Paths.Normalize(_right.Value);
            if (leftPath.Length == 0 || rightPath.Length == 0)
            {
                Dialogs.Error(this, $"Choose both folders ({_left.Title.ToLowerInvariant()} and {_right.Title.ToLowerInvariant()}).");
                return;
            }
            if (!Paths.IsUnc(leftPath) && Paths.DriveOf(leftPath) == null || !Paths.IsUnc(rightPath) && Paths.DriveOf(rightPath) == null)
            {
                Dialogs.Error(this, "Folders need a full path, like D:\\Photos or \\\\server\\share\\Photos.");
                return;
            }
            if (Paths.IsUnc(leftPath) && Paths.ShareRoot(leftPath) == null || Paths.IsUnc(rightPath) && Paths.ShareRoot(rightPath) == null)
            {
                Dialogs.Error(this, "A network folder needs a server and a share, like \\\\server\\share.");
                return;
            }
            if (Paths.Overlap(leftPath, rightPath))
            {
                Dialogs.Error(this, "The two folders are the same, or one is inside the other. Choose two separate folders.");
                return;
            }
            var mode = Mode;
            if (mode != SyncMode.TwoWay && !Paths.IsUnc(leftPath) && !Directory.Exists(Paths.Long(leftPath)) && Locations.Resolve(Locations.FromPath(leftPath)).Ok)
            {
                Dialogs.Error(this, $"The source folder {leftPath} doesn't exist.");
                return;
            }

            var kind = ScheduleChoices[Math.Max(0, _schedule.SelectedIndex)].Kind;
            var days = 0;
            for (var d = 0; d < 7; d++)
                if (_days[d].IsChecked == true) days |= 1 << d;
            if (kind == ScheduleKind.Weekly && days == 0)
            {
                Dialogs.Error(this, "Choose at least one day for the schedule.");
                return;
            }
            var timeOk = TimeSpan.TryParse(_time.Text.Trim(), out var t) && t >= TimeSpan.Zero && t < TimeSpan.FromDays(1);
            if ((kind == ScheduleKind.Daily || kind == ScheduleKind.Weekly) && !timeOk)
            {
                Dialogs.Error(this, "Enter the time like 21:00.");
                return;
            }
            var keepDays = KeepDays();
            var historyFolder = _customHistory.IsChecked == true && keepDays >= 0 ? Paths.Normalize(_historyFolder.Text) : string.Empty;
            if (_customHistory.IsChecked == true && keepDays >= 0 && historyFolder.Length == 0)
            {
                Dialogs.Error(this, "Choose the folder for replaced and deleted files, or untick that option.");
                return;
            }

            var pair = _pair;
            pair.Name = name;
            pair.Left = SameFolder(pair.Left, leftPath) ? pair.Left : Locations.FromPath(leftPath);
            pair.Right = SameFolder(pair.Right, rightPath) ? pair.Right : Locations.FromPath(rightPath);
            pair.Mode = mode;
            pair.Filters.Include = string.IsNullOrWhiteSpace(_include.Text) ? "*" : _include.Text.Trim();
            pair.Filters.Exclude = _exclude.Text.Trim();
            pair.Filters.ExcludeFolders = _excludeFolders.Text.Trim();
            pair.Filters.SkipHidden = _skipHidden.IsChecked == true;
            pair.Filters.SkipSystem = _skipSystem.IsChecked == true;
            pair.KeepDays = keepDays;
            pair.HistoryFolder = historyFolder;
            pair.Verify = _verify.IsChecked == true;
            pair.DetectRenames = _renames.IsChecked == true;
            pair.Schedule.Kind = kind;
            pair.Schedule.Days = days;
            if (timeOk) pair.Schedule.Time = DateTime.Today.Add(t).ToString("HH:mm");
            pair.Schedule.Hours = HourChoices[Math.Max(0, _hours.SelectedIndex)];
            pair.LiveSync = _live.IsChecked == true;
            pair.LiveDelaySeconds = _delay.SelectedIndex >= 0 && _delay.SelectedIndex < DelayChoices.Length ? DelayChoices[_delay.SelectedIndex] : pair.LiveDelaySeconds;
            pair.NotifyOnPlug = _notify.IsChecked == true;
            pair.InRunAll = _inRunAll.IsChecked == true;

            // A network share that can't be reached yet: offer to sign in.
            foreach (var p in new[] { leftPath, rightPath }.Where(Paths.IsUnc))
            {
                if (SelfTest.Running || NetworkShares.Connect(p, 1) == null) continue;
                if (Dialogs.Confirm(this, $"{Paths.ShareRoot(p)} can't be reached right now. Does it need a user name and password?\n\nChoose Yes to sign in, or No to save the pair anyway (it'll be tried when the pair runs).") &&
                    !LoginWindow.Ask(this, p))
                    return;
            }

            Result = pair;
            try { DialogResult = true; }
            catch (InvalidOperationException) { Close(); } // shown without ShowDialog (the self-test)
        }

        private static bool SameFolder(Location location, string path) => string.Equals(Paths.Normalize(location.Path), path, StringComparison.OrdinalIgnoreCase) && location.Path != null;

        /// <summary>For the self-test: fill in the editor without clicking.</summary>
        public void Fill(string name, string left, string right, SyncMode mode)
        {
            _name.Text = name;
            _left.Value = left;
            _right.Value = right;
            _modes[mode].IsChecked = true;
        }

        public void SaveForTest() => Save();
    }

    /// <summary>A folder box with Browse… and a line saying which drive or share it's on.</summary>
    public sealed class LocationPicker : StackPanel
    {
        private readonly Window _owner;
        private readonly TextBlock _title = Ui.Text(string.Empty, 12.5, "SubtleText", bold: true);
        private readonly TextBlock _hint = Ui.Subtle(string.Empty, 12, false);
        private readonly TextBox _box = new TextBox { MinHeight = 34 };
        private readonly TextBlock _status = Ui.Subtle(string.Empty, 12);
        private readonly Button _signIn;

        public event Action Changed;

        public LocationPicker(Window owner, Location location)
        {
            _owner = owner;
            var head = Ui.Row(_title, _hint.Margin(10, 0, 0, 0));
            Children.Add(head.Margin(0, 0, 0, 5));
            var row = Ui.Columns(Ui.Star(), Ui.Auto);
            row.Children.Add(_box.At(0, 0));
            row.Children.Add(Ui.Button("Browse…", "IconFolder", "SecondaryButton", (s, e) =>
            {
                var folder = Dialogs.PickFolder(_owner, "Choose the " + Title.ToLowerInvariant() + " folder", Directory.Exists(_box.Text) ? _box.Text : null);
                if (folder != null) Value = folder;
            }).Margin(8, 0, 0, 0).At(0, 1));
            Children.Add(row);
            _signIn = Ui.Link("Sign in…", (s, e) =>
            {
                if (LoginWindow.Ask(_owner, _box.Text)) Refresh();
            });
            Children.Add(Ui.Row(_status, _signIn.Margin(10, 0, 0, 0)).Margin(0, 5, 0, 0));
            _box.Text = location?.Path ?? string.Empty;
            _box.LostFocus += (s, e) => Refresh();
            _box.TextChanged += (s, e) => Changed?.Invoke();
            Refresh();
        }

        public string Title
        {
            get => _title.Text;
            set => _title.Text = value.ToUpperInvariant();
        }

        public string Hint
        {
            set => _hint.Text = value ?? string.Empty;
        }

        public string Value
        {
            get => _box.Text.Trim();
            set
            {
                _box.Text = value ?? string.Empty;
                Refresh();
            }
        }

        private void Refresh()
        {
            var path = Paths.Normalize(_box.Text);
            _signIn.Visibility = Paths.IsUnc(path) && Paths.ShareRoot(path) != null ? Visibility.Visible : Visibility.Collapsed;
            if (path.Length == 0)
            {
                _status.Text = "Choose a folder on this PC, a USB drive, or a network share (\\\\server\\share).";
                return;
            }
            if (Paths.IsUnc(path))
            {
                var login = NetworkShares.LoadLogin(path);
                _status.Text = "Network share " + Paths.ShareRoot(path) + (login != null ? " · signed in as " + login.User : " · uses your Windows login");
                _signIn.Content = login != null ? "Change login…" : "Sign in…";
                return;
            }
            var volume = VolumeInfo.Of(path);
            if (volume == null)
            {
                _status.Text = "That drive isn't connected.";
                return;
            }
            var text = volume.Describe() + " · " + volume.KindName + " · " + volume.FileSystem;
            var system = Paths.DriveOf(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
            if (!string.Equals(Paths.DriveOf(path), system, StringComparison.OrdinalIgnoreCase))
                text += " · recognised even if its letter changes";
            if (!Directory.Exists(Paths.Long(path))) text += " · the folder will be created";
            _status.Text = text;
        }
    }
}
