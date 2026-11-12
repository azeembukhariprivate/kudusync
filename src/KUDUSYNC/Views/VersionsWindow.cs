using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Kudusync.Controls;
using Kudusync.Core;

namespace Kudusync.Views
{
    /// <summary>
    /// A pair's earlier versions: the files a sync replaced or deleted. A version can be opened to look at it or put back
    /// in its usual place (the file there now is kept first); and the whole backup can be copied, as it was just before a
    /// sync, into a new folder (the backup itself isn't touched).
    /// </summary>
    public sealed class VersionsWindow : Window
    {
        private readonly FolderPair _pair;
        private readonly ResolvedLocation _left, _right;
        private Side _side;
        private Versions _versions;
        private List<string> _deleted = new List<string>();

        private readonly TextBlock _summary = Ui.Subtle(string.Empty, 12.5, true);
        private readonly TextBox _search = new TextBox { MinHeight = 32, VerticalContentAlignment = VerticalAlignment.Center };
        private readonly ListBox _files = new ListBox { SelectionMode = SelectionMode.Single }.Styled("TableList");
        private readonly StackPanel _detail = new StackPanel();
        private readonly ComboBox _when = new ComboBox { MinWidth = 300 };
        private readonly TextBlock _target = Ui.Text("No folder chosen", 12.5);
        private readonly Button _copy;
        private readonly Button _stop;
        private readonly ProgressBar _bar = new ProgressBar { Maximum = 1, Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
        private readonly TextBlock _copyNote = Ui.Subtle(string.Empty, 12, true);
        private string _targetFolder;
        private CancellationTokenSource _copying;

        /// <summary>For the self-test: don't ask before putting a version back.</summary>
        public bool SkipConfirm { get; set; }

        public VersionsWindow(Window owner, FolderPair pair)
        {
            _pair = pair;
            _left = Locations.Resolve(pair.Left);
            _right = Locations.Resolve(pair.Right);
            // One-way pairs keep versions in the destination; two-way pairs on both sides.
            _side = Side.Right;
            Width = 1060;
            Height = 720;
            MinWidth = 820;
            MinHeight = 520;
            Dialogs.Prepare(this, "Earlier versions · " + pair.Name, owner);

            var root = new Grid { Margin = new Thickness(24, 20, 24, 20) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // ---- top
            var top = Ui.Column(Ui.Heading("Earlier versions of " + pair.Name, 20), _summary.Margin(0, 6, 0, 0));
            if (pair.Mode == SyncMode.TwoWay)
            {
                var sides = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
                foreach (var side in new[] { Side.Left, Side.Right })
                {
                    var s0 = side;
                    var chip = new RadioButton { Content = side == Side.Left ? pair.LeftTitle : pair.RightTitle, GroupName = "side", IsChecked = side == _side }.Styled("ChipRadio");
                    chip.Checked += async (s, e) =>
                    {
                        _side = s0;
                        await Load();
                    };
                    sides.Children.Add(chip);
                }
                top.Children.Add(sides);
            }
            root.Children.Add(top.At(0));

            // ---- files and their versions
            var middle = Ui.Columns(Ui.Star(1), Ui.Px(18), Ui.Star(1.15));
            middle.Margin = new Thickness(0, 14, 0, 14);
            System.Windows.Automation.AutomationProperties.SetName(_search, "Find a file");
            _search.TextChanged += (s, e) => FillFiles();
            var filesPanel = new Grid();
            filesPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            filesPanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            filesPanel.Children.Add(Ui.Row(Ui.Icon("IconSearch", 15).Margin(0, 0, 8, 0), _search).At(0));
            _search.Width = 360;
            filesPanel.Children.Add(_files.Margin(0, 8, 0, 0).At(1));
            _files.SelectionChanged += (s, e) => ShowVersions((_files.SelectedItem as ListBoxItem)?.Tag as string);
            middle.Children.Add(filesPanel.At(0, 0));
            middle.Children.Add(Ui.Card(new ScrollViewer { Content = _detail, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }, 16).At(0, 2));
            root.Children.Add(middle.At(1));

            // ---- the backup as it was
            var pick = Ui.Small(Ui.Button("Choose a folder...", "IconFolder", "SecondaryButton", (s, e) =>
            {
                var folder = Dialogs.PickFolder(this, "Choose a new, empty folder for the copy");
                if (folder != null) SetTarget(folder);
            }));
            _copy = Ui.Small(Ui.Button("Make the copy", "IconCopy", "PrimaryButton", async (s, e) => await CopyAsItWas()));
            _stop = Ui.Small(Ui.Button("Stop", "IconStop", "SecondaryButton", (s, e) => _copying?.Cancel()));
            _stop.Visibility = Visibility.Collapsed;
            System.Windows.Automation.AutomationProperties.SetName(_when, "Which sync");
            var asItWas = Ui.Column(
                Ui.Heading("The backup as it was", 14),
                Ui.Subtle("Makes a copy of the whole backup as it was just before a sync, in a new folder. The backup itself isn't changed. "
                          + "Files added since then (and never replaced) are in the copy too, as they are now.", 12, true).Margin(0, 4, 0, 8),
                Ui.Row(_when, pick.Margin(10, 0, 0, 0), _target.Margin(10, 0, 0, 0)),
                Ui.Row(_copy, _stop.Margin(8, 0, 0, 0)).Margin(0, 10, 0, 0),
                _bar,
                _copyNote.Margin(0, 6, 0, 0));
            root.Children.Add(Ui.Card(asItWas, 16).At(2));
            Content = root;

            Loaded += async (s, e) => await Load();
            Closing += (s, e) => _copying?.Cancel();
        }

        // ================================================================ for the self-test

        public Versions Found => _versions;
        public bool IsLoading { get; private set; } = true;
        public string CopyNote => _copyNote.Text;
        public bool IsCopying => _copying != null;
        public string Root => (_side == Side.Left ? _left : _right).Root;

        public void SelectFile(string relative)
        {
            _search.Text = string.Empty;
            FillFiles();
            var item = _files.Items.OfType<ListBoxItem>().FirstOrDefault(i => string.Equals(i.Tag as string, relative, StringComparison.OrdinalIgnoreCase));
            if (item != null) _files.SelectedItem = item;
        }

        public void SetTarget(string folder)
        {
            _targetFolder = folder;
            _target.Text = Paths.Display(folder);
            _target.ToolTip = _target.Text;
        }

        public void ChooseRun(DateTime run)
        {
            var item = _when.Items.OfType<ComboBoxItem>().FirstOrDefault(i => i.Tag is DateTime t && t == run);
            if (item != null) _when.SelectedItem = item;
        }

        // ================================================================ reading

        private ResolvedLocation SideNow => _side == Side.Left ? _left : _right;
        private string HistoryFolder => History.BaseFolder(_pair, SideNow.Root, _side);

        public async Task Load()
        {
            IsLoading = true;
            _files.Items.Clear();
            _detail.Children.Clear();
            _when.Items.Clear();
            var now = SideNow;
            if (!now.Ok)
            {
                _summary.Text = now.Problem + " Earlier versions can be seen when it's there.";
                _copy.IsEnabled = false;
                IsLoading = false;
                return;
            }
            _summary.Text = "Reading the kept versions...";
            var history = HistoryFolder;
            var root = now.Root;
            var (versions, current) = await Task.Run(() => (Versions.Read(history), new HashSet<string>(Versions.CurrentFiles(root), Paths.Comparer)));
            _versions = versions;
            _deleted = versions.Files.Where(f => !current.Contains(f)).ToList();
            if (versions.Count == 0)
            {
                _summary.Text = _pair.KeepDays < 0
                    ? "This pair doesn't keep the files a sync replaces or deletes, so there are no earlier versions. You can change that with Edit."
                    : Lang.T("No earlier versions yet. When a sync replaces or deletes a file, the old one is kept here ({0}).", _pair.KeepText.ToLower(System.Globalization.CultureInfo.CurrentCulture));
            }
            else
            {
                _summary.Text = Lang.T("{0} of {1} ({2}), from {3}", Format.Count(versions.Count, "earlier version"), Format.Count(versions.Files.Count, "file"), Format.Size(versions.Bytes), Format.Count(versions.Runs.Count, "sync"))
                                + (_deleted.Count > 0 ? "; " + Lang.T(_deleted.Count == 1 ? "1 file deleted since" : "{0} files deleted since", _deleted.Count.ToString("N0")) : string.Empty)
                                + " · " + _pair.KeepText;
            }
            foreach (var run in versions.Runs)
                _when.Items.Add(new ComboBoxItem { Content = "Just before the sync on " + run.ToString("d MMM yyyy, HH:mm"), Tag = run });
            if (_when.Items.Count > 0) _when.SelectedIndex = 0;
            _copy.IsEnabled = versions.Count > 0;
            FillFiles();
            if (_files.Items.Count > 0) _files.SelectedIndex = 0;
            else _detail.Children.Add(Ui.Subtle(versions.Count == 0 ? string.Empty : "No file has those words.", 12.5));
            IsLoading = false;
        }

        private void FillFiles()
        {
            if (_versions == null) return;
            var words = _search.Text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var selected = (_files.SelectedItem as ListBoxItem)?.Tag as string;
            _files.Items.Clear();
            foreach (var file in _versions.Files.Where(f => words.All(w => f.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0)).Take(2000))
            {
                var row = Ui.Columns(Ui.Star(), Ui.Auto);
                var name = Path.GetFileName(file);
                var folder = Path.GetDirectoryName(file);
                row.Children.Add(Ui.Column(Ui.Text(name, 13), Ui.Subtle(string.IsNullOrEmpty(folder) ? "(top folder)" : folder, 11.5, false)).At(0, 0));
                var chips = Ui.Row();
                if (_deleted.Contains(file, Paths.Comparer)) chips.Children.Add(Ui.Chip("deleted", Tone.Warn).Margin(0, 0, 6, 0));
                chips.Children.Add(Ui.Chip(Format.Count(_versions.Of(file).Count, "version"), Tone.Neutral));
                row.Children.Add(chips.At(0, 1));
                var item = new ListBoxItem { Content = row, Tag = file, Padding = new Thickness(6, 7, 6, 7) };
                _files.Items.Add(item);
                if (string.Equals(file, selected, StringComparison.OrdinalIgnoreCase)) _files.SelectedItem = item;
            }
        }

        private void ShowVersions(string file)
        {
            _detail.Children.Clear();
            if (file == null || _versions == null) return;
            var deleted = _deleted.Contains(file, Paths.Comparer);
            _detail.Children.Add(Ui.Heading(Path.GetFileName(file), 16));
            _detail.Children.Add(Ui.Subtle(file + (deleted ? " · not in the backup now (deleted)" : string.Empty), 12, true).Margin(0, 2, 0, 12));
            foreach (var v in _versions.Of(file))
            {
                var version = v;
                var g = Ui.Columns(Ui.Star(), Ui.Auto);
                g.Children.Add(Ui.Column(
                    Ui.Text("Until " + v.ReplacedAt.ToString("d MMM yyyy, HH:mm"), 13, bold: true),
                    Ui.Subtle(Format.Size(v.Size) + " · last changed " + v.Modified.ToString("d MMM yyyy, HH:mm"), 11.5, false)).At(0, 0));
                var buttons = Ui.Row(
                    Ui.Small(Ui.Button("Open", "IconEye", "SecondaryButton", (s, e) => OpenCopy(version))).Tip("Open a copy to look at it"),
                    Ui.Small(Ui.Button("Put back", "IconUndo", "SecondaryButton", async (s, e) => await PutBack(version))).Tip("Put this version back in its usual place").Margin(6, 0, 0, 0),
                    Ui.Small(Ui.Button(string.Empty, "IconFolder", "GhostButton", (s, e) => AppInfo.OpenFolder(Path.GetDirectoryName(Paths.Display(version.FullPath)), Paths.Display(version.FullPath)))).Tip("Show in its folder").Margin(4, 0, 0, 0));
                buttons.VerticalAlignment = VerticalAlignment.Center;
                g.Children.Add(buttons.At(0, 1));
                _detail.Children.Add(new Border { Child = g, Padding = new Thickness(0, 8, 0, 8), BorderThickness = new Thickness(0, 1, 0, 0) }.Res(Border.BorderBrushProperty, "BorderBrush"));
            }
        }

        // ================================================================ doing

        /// <summary>Opens a read-only copy of a version (in the temporary folder), so looking at it can't change anything.</summary>
        private static void OpenCopy(SavedVersion v)
        {
            try
            {
                var folder = Path.Combine(Path.GetTempPath(), "KUDUSYNC versions", Guid.NewGuid().ToString("N").Substring(0, 8));
                Directory.CreateDirectory(folder);
                var copy = Path.Combine(folder, Path.GetFileName(v.Relative));
                File.Copy(v.FullPath, copy);
                File.SetAttributes(copy, File.GetAttributes(copy) | FileAttributes.ReadOnly);
                AppInfo.OpenFile(copy);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Dialogs.Error(null, "It couldn't be opened: " + ex.Message);
            }
        }

        /// <summary>Puts a version back in its usual place (the file there now is kept first). True when done.</summary>
        public async Task<bool> PutBack(SavedVersion v)
        {
            if (PairLock.IsRunning(_pair.Id))
            {
                Dialogs.Info(this, "This pair is being synced right now. Try again when it's finished.");
                return false;
            }
            if (!SkipConfirm && !Dialogs.Confirm(this, $"Put back the version of \"{Path.GetFileName(v.Relative)}\" from before {v.ReplacedAt:d MMM yyyy, HH:mm}?\n\n"
                                                         + "The file there now is kept with the earlier versions first, so you can go back to it."))
                return false;
            try
            {
                await Task.Run(() => Versions.PutBack(v, _pair, _side, _left.Root, _right.Root, DateTime.Now));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Dialogs.Error(this, "It couldn't be put back: " + ex.Message);
                return false;
            }
            var file = v.Relative;
            await Load();
            SelectFile(file);
            return true;
        }

        /// <summary>Copies the backup as it was before the chosen sync into the chosen folder. Returns the files copied, or -1.</summary>
        public async Task<int> CopyAsItWas()
        {
            if (_copying != null || _versions == null) return -1;
            if (!(_when.SelectedItem is ComboBoxItem item) || !(item.Tag is DateTime run))
            {
                _copyNote.Text = "Choose which sync first.";
                return -1;
            }
            var problem = Versions.CheckTarget(_targetFolder, SideNow.Root, HistoryFolder);
            if (problem != null)
            {
                _copyNote.Text = problem;
                return -1;
            }
            _copying = new CancellationTokenSource();
            var token = _copying.Token;
            _copy.Visibility = Visibility.Collapsed;
            _stop.Visibility = Visibility.Visible;
            _bar.Visibility = Visibility.Visible;
            _bar.Value = 0;
            _copyNote.Text = "Working out what was there...";
            var problems = new List<string>();
            var root = SideNow.Root;
            var target = _targetFolder;
            var versions = _versions;
            try
            {
                var copied = await Task.Run(() =>
                {
                    var plan = versions.AsItWas(run, Versions.CurrentFiles(root, token), r => Paths.Full(root, r));
                    var last = DateTime.MinValue;
                    return Versions.CopyAsItWas(plan, target, (done, total) =>
                    {
                        if ((DateTime.UtcNow - last).TotalMilliseconds < 150 && done < total) return;
                        last = DateTime.UtcNow;
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            _bar.Value = total == 0 ? 1 : (double)done / total;
                            _copyNote.Text = $"Copying: {done:N0} of {total:N0} files";
                        }));
                    }, problems, token);
                });
                _copyNote.Text = $"Done: {Format.Count(copied, "file")} copied into {Paths.Display(target)}, as the backup was before the sync on {run:d MMM yyyy, HH:mm}."
                                 + (problems.Count > 0 ? $" {Format.Count(problems.Count, "file")} couldn't be copied (first: {problems[0]})." : string.Empty);
                return copied;
            }
            catch (OperationCanceledException)
            {
                _copyNote.Text = "Stopped. The files copied so far are in the folder.";
                return -1;
            }
            finally
            {
                _copying = null;
                _copy.Visibility = Visibility.Visible;
                _stop.Visibility = Visibility.Collapsed;
                _bar.Value = 1;
            }
        }
    }
}
