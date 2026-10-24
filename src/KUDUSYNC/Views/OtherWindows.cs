using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using Kudusync.Controls;
using Kudusync.Core;

namespace Kudusync.Views
{
    public sealed class SettingsWindow : Window
    {
        public SettingsWindow(Window owner)
        {
            Width = 580;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Dialogs.Prepare(this, "Settings · KUDUSYNC", owner);

            var root = new StackPanel { Margin = new Thickness(26, 22, 26, 22) };
            root.Children.Add(Ui.Heading("Settings", 20).Margin(0, 0, 0, 14));

            root.Children.Add(Ui.Heading("Appearance", 14).Margin(0, 0, 0, 8));
            var themes = new WrapPanel();
            foreach (var mode in new[] { ThemeMode.System, ThemeMode.Light, ThemeMode.Dark })
            {
                var chip = new RadioButton { Content = mode == ThemeMode.System ? "Same as Windows" : mode.ToString(), GroupName = "theme", IsChecked = Settings.Theme == mode }
                    .Styled("ChipRadio");
                chip.Checked += (s, e) =>
                {
                    Settings.Theme = mode;
                    ThemeManager.Apply(mode);
                };
                themes.Children.Add(chip);
            }
            root.Children.Add(themes);

            root.Children.Add(Ui.Heading("In the background", 14).Margin(0, 16, 0, 4));
            var tray = new CheckBox { Content = "Run KUDUSYNC in the notification area when Windows starts", IsChecked = Settings.TrayAtStartup };
            tray.Click += (s, e) =>
            {
                Settings.TrayAtStartup = tray.IsChecked == true;
                ShellIntegration.SetStartWithWindows(Settings.TrayAtStartup);
                if (Settings.TrayAtStartup) Tray.StartRunning();
                else Tray.StopRunning();
            };
            root.Children.Add(tray);
            root.Children.Add(Ui.Subtle("Needed for live sync and for notifications when a pair's drive is plugged in. Schedules work without it.", 12).Margin(27, 0, 0, 0));

            root.Children.Add(Ui.Text("After a scheduled or automatic sync, notify me", 13).Margin(0, 12, 0, 6));
            var notify = new WrapPanel();
            foreach (var (mode, text) in new[] { (NotifyMode.Always, "Always"), (NotifyMode.Problems, "Only about problems"), (NotifyMode.Never, "Never") })
            {
                var chip = new RadioButton { Content = text, GroupName = "notify", IsChecked = Settings.Notify == mode }.Styled("ChipRadio");
                chip.Checked += (s, e) => Settings.Notify = mode;
                notify.Children.Add(chip);
            }
            root.Children.Add(notify);
            root.Children.Add(Ui.Subtle("Live sync only speaks up about problems.", 12).Margin(0, 2, 0, 0));

            var pause = new CheckBox { Content = "Pause a scheduled or automatic sync that would delete more than half of the files", IsChecked = Settings.PauseMassDeletes, Margin = new Thickness(0, 12, 0, 0) };
            pause.Click += (s, e) => Settings.PauseMassDeletes = pause.IsChecked == true;
            root.Children.Add(pause);
            root.Children.Add(Ui.Subtle("A sync that would empty a folder completely (for example when the source drive is blank) always waits for you.", 12).Margin(27, 0, 0, 0));

            root.Children.Add(Ui.Heading("File Explorer", 14).Margin(0, 16, 0, 4));
            var menu = new CheckBox { Content = "Add \"Sync with KUDUSYNC\" and \"Back up this folder\" to folders' right-click menu", IsChecked = Settings.ShellMenu };
            menu.Click += (s, e) =>
            {
                Settings.ShellMenu = menu.IsChecked == true;
                ShellIntegration.Refresh();
            };
            root.Children.Add(menu);
            root.Children.Add(Ui.Subtle("For your Windows account only. On Windows 11 it's under \"Show more options\".", 12).Margin(27, 0, 0, 0));

            root.Children.Add(Ui.Heading("Updates", 14).Margin(0, 16, 0, 4));
            var updates = new CheckBox { Content = "Check for a new KUDUSYNC version when it starts", IsChecked = Settings.CheckForUpdates };
            updates.Click += (s, e) => Settings.CheckForUpdates = updates.IsChecked == true;
            root.Children.Add(updates);
            root.Children.Add(Ui.Subtle("Only asks GitHub for the latest version number. Nothing is downloaded without asking.", 12).Margin(27, 0, 0, 0));

            root.Children.Add(Ui.Divider().Margin(0, 18, 0, 0));
            var bottom = Ui.Columns(Ui.Star(), Ui.Auto);
            bottom.Children.Add(Ui.Link("About KUDUSYNC", (s, e) => About.Show(this)).At(0, 0));
            var close = Ui.Button("Close", null, "PrimaryButton");
            close.IsCancel = true;
            close.Click += (s, e) => Close();
            bottom.Children.Add(close.At(0, 1));
            root.Children.Add(bottom.Margin(0, 14, 0, 0));
            Content = root;
        }
    }

    public static class About
    {
        public static Window Build(Window owner)
        {
            var header = Ui.Row(LogoTile.Create(64), Ui.Column(Ui.Text("KUDUSYNC", 20, bold: true), Ui.Subtle("Version " + AppInfo.VersionText + " · Keep folders in step", 12.5))
                .Margin(16, 0, 0, 0));
            ((FrameworkElement)header.Children[1]).VerticalAlignment = VerticalAlignment.Center;

            var rows = new StackPanel { Margin = new Thickness(0, 18, 0, 0) };
            void Row(string label, UIElement value)
            {
                var g = Ui.Columns(Ui.Px(120), Ui.Star());
                g.Children.Add(Ui.Subtle(label, 12.5, false).At(0, 0));
                g.Children.Add(value.At(0, 1));
                rows.Children.Add(new Border { BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 7, 0, 7), Child = g }.Res(Border.BorderBrushProperty, "BorderBrush"));
            }
            var madeBy = Ui.Text("Azeem, with Claude and ", 12.5);
            var heart = new Run("♥");
            heart.SetResourceReference(TextElement.ForegroundProperty, "Accent");
            madeBy.Inlines.Add(heart);
            Row("Made by", madeBy);
            Row("Licence", Ui.Row(Ui.Text("MIT · free and open source  ", 12.5), Ui.Link("View", (s, e) => OpenLocal("LICENSE.txt"))));
            Row("Source code", Ui.Link("github.com/" + AppInfo.RepoOwner + "/" + AppInfo.RepoName, (s, e) => AppInfo.OpenUrl(AppInfo.RepoUrl)));
            Row("Your data", Ui.Text(DataStore.IsPortable ? "Kept next to the program (portable)" : "Pairs and logs are kept on this PC only", 12.5));
            Row("Third-party", Ui.Link("Third-party notices", (s, e) => OpenLocal("THIRD-PARTY-NOTICES.txt")));
            rows.Children.Add(Ui.Divider());

            var content = Ui.Column(header, rows,
                Ui.Subtle("KUDUSYNC never deletes a file without keeping a copy first, unless you tell a pair not to. Replaced and deleted files go to a " +
                          "\"_KUDUSYNC History\" folder and are cleaned up after the number of days you choose.", 11.5).Margin(0, 14, 0, 0));

            Window window = null;
            var updates = Ui.Button("Check for updates");
            var close = Ui.Button("Close", null, "PrimaryButton");
            close.IsCancel = true;
            close.IsDefault = true;
            window = Dialogs.Dialog(owner, "About KUDUSYNC", content, 560, updates, close);
            close.Click += (s, e) => window.Close();
            updates.Click += async (s, e) =>
            {
                updates.IsEnabled = false;
                try
                {
                    var info = await UpdateChecker.CheckAsync();
                    if (info == null) Dialogs.Info(window, "Couldn't find any published version yet.");
                    else if (info.IsNewer)
                    {
                        if (Dialogs.Confirm(window, $"KUDUSYNC {info.Latest.ToString(3)} is available. Open the download page?")) AppInfo.OpenUrl(info.Url);
                    }
                    else Dialogs.Info(window, "You have the latest version.");
                }
                catch
                {
                    Dialogs.Error(window, "Couldn't check for updates. Check your internet connection and try again.");
                }
                finally
                {
                    updates.IsEnabled = true;
                }
            };
            return window;
        }

        public static void Show(Window owner) => Build(owner).ShowDialog();

        private static void OpenLocal(string name)
        {
            var path = Path.Combine(Path.GetDirectoryName(AppInfo.ExePath) ?? string.Empty, name);
            if (File.Exists(path)) AppInfo.OpenFile(path);
            else AppInfo.OpenUrl(AppInfo.RepoUrl);
        }
    }

    /// <summary>Picking which of SyncToy's folder pairs to bring across.</summary>
    public sealed class ImportWindow : Window
    {
        public List<FolderPair> Imported { get; } = new List<FolderPair>();

        public ImportWindow(Window owner, List<FolderPair> existing, string file = null)
        {
            Width = 680;
            SizeToContent = SizeToContent.Height;
            MaxHeight = 720;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Dialogs.Prepare(this, "Import from SyncToy · KUDUSYNC", owner);

            var root = new StackPanel { Margin = new Thickness(26, 22, 26, 22) };
            root.Children.Add(Ui.Heading("Import from SyncToy", 20));
            root.Children.Add(Ui.Subtle("These folder pairs were found in SyncToy's settings. Echo becomes Mirror, Contribute becomes Update, and Synchronize becomes Two-way sync.", 12.5).Margin(0, 6, 0, 12));

            List<SyncToyPair> found;
            string problem = null;
            try { found = SyncToyImport.Read(file ?? SyncToyImport.DefaultFile); }
            catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException)
            {
                found = new List<SyncToyPair>();
                problem = ex.Message;
            }

            var boxes = new List<(CheckBox Box, SyncToyPair Pair)>();
            var list = new StackPanel();
            foreach (var p in found)
            {
                var already = existing.Any(e => string.Equals(Paths.Normalize(e.Left.Path), Paths.Normalize(p.Left), StringComparison.OrdinalIgnoreCase) &&
                                                string.Equals(Paths.Normalize(e.Right.Path), Paths.Normalize(p.Right), StringComparison.OrdinalIgnoreCase));
                var box = new CheckBox
                {
                    IsChecked = !already,
                    Content = Ui.Column(
                        Ui.Row(Ui.Text(p.Name, 13, bold: true), Ui.Chip(p.SyncToyModeName + " → " + FolderPair.ModeName(p.Mode), Tone.Neutral).Margin(8, 0, 0, 0),
                            already ? Ui.Chip("Already in KUDUSYNC", Tone.Ok).Margin(6, 0, 0, 0) : (UIElement)new Border()),
                        Ui.Subtle(p.Left + "  →  " + p.Right, 12, false).Margin(0, 2, 0, 0)),
                    Margin = new Thickness(0, 0, 0, 10)
                };
                boxes.Add((box, p));
                list.Children.Add(box);
            }
            if (found.Count == 0) list.Children.Add(Ui.Subtle(problem != null ? "SyncToy's settings couldn't be read: " + problem : "No folder pairs were found in SyncToy's settings.", 12.5));
            root.Children.Add(new ScrollViewer { Content = list, MaxHeight = 420, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

            var import = Ui.Button("Import", null, "PrimaryButton");
            import.IsDefault = true;
            import.IsEnabled = found.Count > 0;
            var cancel = Ui.Button("Cancel");
            cancel.IsCancel = true;
            import.Click += (s, e) =>
            {
                var names = new HashSet<string>(existing.Select(x => x.Name), StringComparer.OrdinalIgnoreCase);
                foreach (var (box, p) in boxes.Where(b => b.Box.IsChecked == true))
                {
                    var pair = SyncToyImport.ToPair(p);
                    var name = pair.Name;
                    for (var n = 2; names.Contains(name); n++) name = pair.Name + " " + n;
                    pair.Name = name;
                    names.Add(name);
                    Imported.Add(pair);
                }
                DialogResult = true;
            };
            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
            row.Children.Add(cancel);
            row.Children.Add(import.Margin(8, 0, 0, 0));
            root.Children.Add(row);
            Content = root;
        }
    }

    /// <summary>A user name and password for a network share, tested and then saved in Windows Credential Manager.</summary>
    public static class LoginWindow
    {
        public static bool Ask(Window owner, string path)
        {
            var share = Paths.ShareRoot(Paths.Normalize(path));
            if (share == null)
            {
                Dialogs.Error(owner, "Enter a network path like \\\\server\\share first.");
                return false;
            }
            var existing = NetworkShares.LoadLogin(share);
            var user = new TextBox { MinHeight = 34, Text = existing?.User ?? string.Empty };
            var password = new PasswordBox { MinHeight = 34, Padding = new Thickness(8, 6, 8, 6) };
            var status = Ui.Subtle(string.Empty, 12);
            var content = Ui.Column(
                Ui.Heading("Sign in to " + share, 17),
                Ui.Subtle("Saved in Windows Credential Manager for your account, so scheduled syncs can sign in too. KUDUSYNC never stores it itself.", 12.5).Margin(0, 6, 0, 0),
                Ui.Text("User name (like NAS\\azeem or azeem@example.com)", 12.5, "SubtleText").Margin(0, 14, 0, 5), user,
                Ui.Text("Password", 12.5, "SubtleText").Margin(0, 12, 0, 5), password,
                status.Margin(0, 8, 0, 0));
            var save = Ui.Button("Test and save", null, "PrimaryButton");
            var cancel = Ui.Button("Cancel");
            var window = Dialogs.Dialog(owner, "Sign in · KUDUSYNC", content, 480, cancel, save);
            save.IsDefault = true;
            cancel.IsCancel = true;
            var ok = false;
            save.Click += (s, e) =>
            {
                if (string.IsNullOrWhiteSpace(user.Text)) return;
                status.Text = "Checking…";
                window.Cursor = System.Windows.Input.Cursors.Wait;
                var problem = NetworkShares.TestLogin(share, user.Text.Trim(), password.Password);
                window.Cursor = null;
                if (problem != null)
                {
                    status.Text = "Couldn't sign in: " + problem;
                    return;
                }
                NetworkShares.SaveLogin(share, user.Text.Trim(), password.Password);
                ok = true;
                window.DialogResult = true;
            };
            window.Loaded += (s, e) => (string.IsNullOrEmpty(user.Text) ? (Control)user : password).Focus();
            window.ShowDialog();
            return ok;
        }
    }
}
