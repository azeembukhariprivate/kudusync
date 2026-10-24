using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Kudusync.Controls;
using Kudusync.Core;

namespace Kudusync.Views
{
    /// <summary>Running pairs from the window: comparing, the preview, progress and the results; and the run history.</summary>
    public sealed partial class MainWindow
    {
        private const int PreviewRows = 1000;

        private CancellationTokenSource _cancel;
        private TaskCompletionSource<bool> _previewChoice;

        public bool IsBusy => _cancel != null;
        public List<RunResult> LastResults { get; private set; } = new List<RunResult>();
        public Plan CurrentPlan { get; private set; }

        /// <summary>For the self-test: answer the preview without clicking.</summary>
        public void AnswerPreview(bool go) => _previewChoice?.TrySetResult(go);
        public void Stop() => _cancel?.Cancel();

        private void RunAll() => RunPairs(_pairs.Where(p => p.InRunAll).ToList(), false);

        public async void RunPairs(List<FolderPair> pairs, bool preview, string trigger = Core.Triggers.Manual) => await RunPairsAsync(pairs, preview, trigger);

        public async Task RunPairsAsync(List<FolderPair> pairs, bool preview, string trigger = Core.Triggers.Manual)
        {
            if (IsBusy || pairs.Count == 0) return;
            _cancel = new CancellationTokenSource();
            var token = _cancel.Token;
            var results = new List<RunResult>();
            try
            {
                for (var i = 0; i < pairs.Count && !token.IsCancellationRequested; i++)
                {
                    var pair = pairs[i];
                    var counter = pairs.Count > 1 ? $"{i + 1} of {pairs.Count}" : null;
                    var started = DateTime.Now;
                    var scanText = ShowScanning(pair, counter);
                    SyncSession session = null;
                    try
                    {
                        session = await Task.Run(() => SyncSession.Open(pair, trigger, token), token);
                        var lastShown = DateTime.MinValue;
                        await Task.Run(() => session.Scan(n =>
                        {
                            if ((DateTime.UtcNow - lastShown).TotalMilliseconds < 150) return;
                            lastShown = DateTime.UtcNow;
                            Dispatcher.BeginInvoke(new Action(() => scanText.Text = Format.Count(n, "item") + " looked at"));
                        }, token), token);
                        var plan = await Task.Run(() => session.BuildPlan(), token);
                        CurrentPlan = plan;
                        var issues = await Task.Run(() => session.SafetyCheck(true), token);

                        if (preview)
                        {
                            if (!await ShowPreview(session, issues, counter))
                            {
                                session.Dispose();
                                session = null;
                                if (plan.IsEmpty) continue;
                                break; // "Back": nothing more
                            }
                        }
                        else if (issues.Any(x => x.NeedsConfirmation))
                        {
                            var message = $"\"{pair.Name}\" needs a look before it runs:\n\n" + string.Join("\n\n", issues.Where(x => x.NeedsConfirmation).Select(x => x.Message)) +
                                          "\n\nGo ahead anyway? (Choose No to see the preview instead.)";
                            if (!Dialogs.Confirm(this, message))
                            {
                                if (!await ShowPreview(session, issues, counter)) break;
                            }
                        }

                        var progress = ShowRunning(pair, counter, session.Plan);
                        var result = await Task.Run(() => session.Execute(p => Dispatcher.BeginInvoke(new Action(() => progress(p))), token));
                        results.Add(result);
                    }
                    catch (SyncProblem problem)
                    {
                        results.Add(Failed(pair, trigger, started, problem.Message, problem.NotConnected ? RunStatus.Skipped : RunStatus.Failed));
                    }
                    catch (OperationCanceledException)
                    {
                        results.Add(Failed(pair, trigger, started, "Stopped before anything was changed.", RunStatus.Stopped));
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                    {
                        results.Add(Failed(pair, trigger, started, ex.Message, RunStatus.Failed));
                    }
                    finally
                    {
                        session?.Dispose();
                    }
                }
            }
            finally
            {
                _cancel = null;
                _previewChoice = null;
            }
            LastResults = results;
            _runs = RunLog.All();
            if (results.Count == 0) ShowPairs();
            else ShowResults(results);
        }

        private static RunResult Failed(FolderPair pair, string trigger, DateTime started, string message, RunStatus status)
        {
            var result = new RunResult { PairId = pair.Id, PairName = pair.Name, Mode = pair.Mode, Trigger = trigger, Started = started, Finished = DateTime.Now, Status = status, Message = message };
            result.LogFile = RunLog.WriteText(result, pair.Left.Path, pair.Right.Path, pair, new[] { message }, Enumerable.Empty<string>());
            RunLog.Add(result);
            return result;
        }

        // ------------------------------------------------------------------ comparing

        private TextBlock ShowScanning(FolderPair pair, string counter)
        {
            var count = Ui.Subtle("Starting…", 12.5, false);
            var bar = new ProgressBar { IsIndeterminate = true, Height = 6, Margin = new Thickness(0, 16, 0, 8) };
            var content = Ui.Column(
                Ui.Subtle(counter == null ? "Comparing" : "Comparing · pair " + counter, 12.5, false),
                Ui.Heading(pair.Name, 20).Margin(0, 4, 0, 0),
                Ui.Subtle(Paths.Display(pair.Left.Path) + (pair.IsOneWay ? "  →  " : "  ⇄  ") + Paths.Display(pair.Right.Path), 12.5).Margin(0, 6, 0, 0),
                bar, count);
            var card = Ui.Card(content, 28);
            card.MaxWidth = 640;
            card.VerticalAlignment = VerticalAlignment.Center;
            card.HorizontalAlignment = HorizontalAlignment.Stretch;
            SetPage(new Grid { Margin = new Thickness(24), Children = { card } }, "scanning");
            SetFooter(Ui.Subtle("Looking at both folders. Nothing is changed yet.", 12.5, false), Ui.Button("Stop", "IconStop", "SecondaryButton", (s, e) => Stop()));
            return count;
        }

        // ------------------------------------------------------------------ preview

        private Task<bool> ShowPreview(SyncSession session, List<SafetyIssue> issues, string counter)
        {
            var plan = session.Plan;
            var pair = session.Pair;
            _previewChoice = new TaskCompletionSource<bool>();

            var root = new Grid { Margin = new Thickness(16) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition());

            var head = Ui.Column(
                Ui.Subtle(counter == null ? "Preview" : "Preview · pair " + counter, 12.5, false),
                Ui.Row(Ui.Heading(pair.Name, 20), Ui.Chip(FolderPair.ModeName(pair.Mode), Tone.Accent).Margin(12, 0, 0, 0)).Margin(0, 2, 0, 0),
                Ui.Subtle(Paths.Display(session.LeftRoot) + (pair.IsOneWay ? "  →  " : "  ⇄  ") + Paths.Display(session.RightRoot), 12.5).Margin(0, 4, 0, 0));
            var stats = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
            void Stat(int n, string label, string brush = "Text")
            {
                if (n == 0) return;
                stats.Children.Add(Ui.Stat(n.ToString("N0"), label, brush).Margin(0, 0, 28, 6));
            }
            Stat(plan.NewFiles, pair.Mode == SyncMode.Move ? "to move" : "new");
            Stat(plan.ChangedFiles, "changed");
            Stat(plan.Renamed, "renamed");
            Stat(plan.DeletedFiles, pair.KeepDays >= 0 ? "deleted (kept as old versions)" : "deleted", "BadText");
            Stat(plan.Conflicts, "changed on both sides", "WarnText");
            Stat(plan.Active.Count(a => a.Kind == ActionKind.Delete && a.Permanent), "already copied (removed from the source)");
            if (plan.BytesToCopy > 0) stats.Children.Add(Ui.Stat(Format.Size(plan.BytesToCopy), "to copy").Margin(0, 0, 28, 6));
            head.Children.Add(stats);
            root.Children.Add(head.At(0));

            var banners = new StackPanel();
            foreach (var issue in issues)
                banners.Children.Add(Ui.Banner("IconWarn", Ui.Text(issue.Message, 12.5, wrap: true), issue.NeedsConfirmation ? "BadBg" : "WarnBg", issue.NeedsConfirmation ? "BadText" : "WarnText").Margin(0, 8, 0, 0));
            foreach (var note in session.Notes.Take(8))
                banners.Children.Add(Ui.Banner("IconInfo", Ui.Text(note, 12.5, wrap: true)).Margin(0, 8, 0, 0));
            root.Children.Add(banners.At(1));

            var list = new ListBox().Styled("TableList");
            VirtualizingPanel.SetIsVirtualizing(list, true);
            var shownNote = Ui.Subtle(string.Empty, 12, false);
            var filters = new WrapPanel { Margin = new Thickness(0, 12, 0, 8) };
            void Filter(string label, Func<SyncAction, bool> match, bool first = false)
            {
                var n = plan.Actions.Count(match);
                if (n == 0 && !first) return;
                var chip = new RadioButton { Content = $"{label} ({n:N0})", GroupName = "previewfilter", IsChecked = first }.Styled("ChipRadio");
                chip.Checked += (s, e) => FillPreview(list, plan, pair, match, shownNote);
                filters.Children.Add(chip);
            }
            Filter("All", a => true, true);
            Filter("New", a => a.Kind == ActionKind.Copy && !a.Conflict);
            Filter("Changed", a => a.Kind == ActionKind.Update);
            Filter("Renamed", a => a.Kind == ActionKind.Rename && !a.Conflict);
            Filter("Deleted", a => a.Kind == ActionKind.Delete);
            Filter("Both changed", a => a.Conflict);
            Filter("Folders", a => a.Kind == ActionKind.CreateFolder || a.Kind == ActionKind.DeleteFolder);
            filters.Children.Add(shownNote.Margin(10, 0, 0, 0));
            root.Children.Add(filters.At(2));

            if (plan.Actions.Count == 0)
            {
                var done = Ui.Column(Ui.Icon("IconCheck", 28).Margin(0, 0, 0, 8), Ui.Heading("Already in step", 16), Ui.Subtle("Both folders match. There's nothing to do.", 12.5).Margin(0, 4, 0, 0));
                ((FrameworkElement)done.Children[0]).HorizontalAlignment = HorizontalAlignment.Left;
                ((FrameworkElement)done.Children[0]).SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "OkText");
                root.Children.Add(Ui.Card(done, 24).At(3));
                SetFooter(null, Ui.Button("Done", null, "PrimaryButton", (s, e) => _previewChoice?.TrySetResult(false)));
            }
            else
            {
                root.Children.Add(Ui.Card(list, 4).At(3));
                FillPreview(list, plan, pair, a => true, shownNote);
                var go = Ui.Button(pair.Mode == SyncMode.Move ? "Move now" : "Sync now", "IconSync", "PrimaryButton", (s, e) => _previewChoice?.TrySetResult(true));
                SetFooter(Ui.Subtle("Untick anything you don't want done this time. It'll be offered again next time.", 12.5, false),
                    Ui.Button("Back", "IconBack", "SecondaryButton", (s, e) => _previewChoice?.TrySetResult(false)), go);
            }
            SetPage(root, "preview");
            return _previewChoice.Task;
        }

        private static void FillPreview(ListBox list, Plan plan, FolderPair pair, Func<SyncAction, bool> match, TextBlock note)
        {
            list.Items.Clear();
            var matching = plan.Actions.Where(match).ToList();
            foreach (var a in matching.Take(PreviewRows)) list.Items.Add(PreviewRow(a, pair));
            note.Text = matching.Count > PreviewRows ? $"Showing the first {PreviewRows:N0}; all {matching.Count:N0} will be done." : string.Empty;
        }

        private static ListBoxItem PreviewRow(SyncAction a, FolderPair pair)
        {
            var check = new CheckBox { IsChecked = a.Enabled, VerticalAlignment = VerticalAlignment.Center, ToolTip = "Untick to skip this time" };
            check.Click += (s, e) => a.Enabled = check.IsChecked == true;

            var (text, tone) = KindChip(a, pair);
            var where = a.Kind == ActionKind.Rename ? $"{a.From}  →  {Paths.Name(a.Path)}" : a.Path;
            var side = a.Target == Side.Left ? pair.LeftTitle : pair.RightTitle;
            var info = Ui.Column(Ui.Text(where, 12.5), Ui.Subtle((a.Reason ?? string.Empty) + " · in " + side.ToLowerInvariant(), 11.5, false));

            var grid = Ui.Columns(Ui.Px(34), Ui.Px(150), Ui.Star(), Ui.Px(90));
            grid.Children.Add(check.At(0, 0));
            grid.Children.Add(Ui.Chip(text, tone).At(0, 1));
            grid.Children.Add(info.At(0, 2));
            var size = Ui.Subtle(a.IsCopy || a.Kind == ActionKind.Delete ? Format.Size(a.Size) : string.Empty, 12, false);
            size.HorizontalAlignment = HorizontalAlignment.Right;
            grid.Children.Add(size.At(0, 3));
            return new ListBoxItem { Content = grid, Padding = new Thickness(8, 6, 12, 6), Tag = a };
        }

        public static (string Text, Tone Tone) KindChip(SyncAction a, FolderPair pair)
        {
            if (a.Conflict) return (a.Kind == ActionKind.Rename ? "Keep both" : "Copy (both kept)", Tone.Warn);
            switch (a.Kind)
            {
                case ActionKind.Copy: return (a.RemoveSource ? "Move" : "New", Tone.Accent);
                case ActionKind.Update: return (a.RemoveSource ? "Move (replace)" : "Changed", Tone.Accent);
                case ActionKind.Delete: return a.Permanent ? ("Already copied", Tone.Neutral) : ("Delete", Tone.Bad);
                case ActionKind.Rename: return ("Rename", Tone.Neutral);
                case ActionKind.CreateFolder: return ("New folder", Tone.Neutral);
                default: return ("Remove folder", Tone.Bad);
            }
        }

        // ------------------------------------------------------------------ running

        private Action<RunProgress> ShowRunning(FolderPair pair, string counter, Plan plan)
        {
            var bar = new ProgressBar { Minimum = 0, Maximum = 1000, Height = 8, Margin = new Thickness(0, 16, 0, 10) };
            var current = Ui.Text("Starting…", 12.5);
            var numbers = Ui.Subtle(string.Empty, 12.5, false);
            var content = Ui.Column(
                Ui.Subtle(counter == null ? (pair.Mode == SyncMode.Move ? "Moving" : "Syncing") : "Syncing · pair " + counter, 12.5, false),
                Ui.Heading(pair.Name, 20).Margin(0, 4, 0, 0),
                bar, current, numbers.Margin(0, 4, 0, 0));
            var card = Ui.Card(content, 28);
            card.MaxWidth = 640;
            card.VerticalAlignment = VerticalAlignment.Center;
            SetPage(new Grid { Margin = new Thickness(24), Children = { card } }, "running");
            SetFooter(Ui.Subtle("You can stop at any time: whatever is half-copied is cleaned up, and the rest happens next time.", 12.5, false),
                Ui.Button("Stop", "IconStop", "SecondaryButton", (s, e) => Stop()));

            var clock = Stopwatch.StartNew();
            return p =>
            {
                var fraction = p.Total == 0 ? 1 : p.BytesTotal > 0
                    ? 0.85 * Math.Min(1, (double)p.BytesDone / p.BytesTotal) + 0.15 * p.Done / p.Total
                    : (double)p.Done / p.Total;
                bar.Value = fraction * 1000;
                current.Text = string.IsNullOrEmpty(p.Path) ? string.Empty : p.Path;
                var text = $"{p.Done:N0} of {Format.Count(p.Total, "step")}";
                if (p.BytesTotal > 0)
                {
                    text += $" · {Format.Size(p.BytesDone)} of {Format.Size(p.BytesTotal)}";
                    var seconds = clock.Elapsed.TotalSeconds;
                    if (seconds > 2 && p.BytesDone > 0)
                    {
                        var speed = p.BytesDone / seconds;
                        text += " · " + Format.Speed(speed);
                        var left = TimeSpan.FromSeconds((p.BytesTotal - p.BytesDone) / Math.Max(1, speed));
                        if (left.TotalSeconds >= 5) text += " · about " + Format.Duration(left) + " left";
                    }
                }
                if (p.Failed > 0) text += $" · {p.Failed:N0} couldn't be done";
                numbers.Text = text;
            };
        }

        // ------------------------------------------------------------------ results

        private void ShowResults(List<RunResult> results)
        {
            var panel = new StackPanel();
            foreach (var r in results)
            {
                var pair = _pairs.FirstOrDefault(p => p.Id == r.PairId);
                var body = Ui.Column(
                    Ui.Row(Ui.Chip(r.StatusText, StatusTone(r)), Ui.Heading(r.PairName, 16).Margin(10, 0, 0, 0)),
                    Ui.Text(r.Summary, 13, wrap: true).Margin(0, 8, 0, 0));
                foreach (var p in r.Problems.Take(10)) body.Children.Add(Ui.Subtle("• " + p, 12).Margin(0, 3, 0, 0));
                if (r.Problems.Count > 10 || r.Failed > r.Problems.Count) body.Children.Add(Ui.Subtle("…the log has the full list.", 12).Margin(0, 3, 0, 0));
                var links = Ui.Row();
                if (!string.IsNullOrEmpty(r.LogFile) && File.Exists(r.LogFile)) links.Children.Add(Ui.Link("Open the log", (s, e) => AppInfo.OpenFile(r.LogFile)).Margin(0, 0, 16, 0));
                if (pair != null)
                {
                    var right = Locations.Resolve(pair.Right);
                    if (right.Ok && Directory.Exists(Paths.Long(right.Root)))
                        links.Children.Add(Ui.Link("Open " + pair.RightTitle.ToLowerInvariant(), (s, e) => AppInfo.OpenFolder(right.Root)).Margin(0, 0, 16, 0));
                }
                if (links.Children.Count > 0) body.Children.Add(links.Margin(0, 10, 0, 0));
                panel.Children.Add(Ui.Card(body, 20).Margin(0, 0, 0, 12));
            }
            var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = panel, Margin = new Thickness(16), Focusable = false };
            SetPage(scroll, "results");
            var ok = results.All(r => r.Status == RunStatus.Ok);
            SetFooter(Ui.Subtle(ok ? "All done." : "Done. Anything that couldn't be done will be tried again next time.", 12.5, false),
                Ui.Button("Done", null, "PrimaryButton", (s, e) => ShowPairs()));
        }

        // ------------------------------------------------------------------ history

        public void ShowHistory()
        {
            _runs = RunLog.All();
            var list = new ListBox().Styled("TableList");
            foreach (var r in _runs.OrderByDescending(r => r.Started).Take(300))
            {
                var grid = Ui.Columns(Ui.Px(150), Ui.Px(200), Ui.Px(150), Ui.Star(), Ui.Auto);
                grid.Children.Add(Ui.Column(Ui.Text(r.Started.ToString("g"), 12.5), Ui.Subtle(r.Trigger, 11.5, false)).At(0, 0));
                grid.Children.Add(Ui.Text(r.PairName, 12.5, bold: true).Margin(0, 0, 10, 0).At(0, 1));
                grid.Children.Add(Ui.Chip(r.StatusText, StatusTone(r)).At(0, 2));
                grid.Children.Add(Ui.Text(r.Summary, 12.5, wrap: true).Margin(0, 0, 10, 0).At(0, 3));
                if (!string.IsNullOrEmpty(r.LogFile) && File.Exists(r.LogFile)) grid.Children.Add(Ui.Link("Log", (s, e) => AppInfo.OpenFile(r.LogFile)).At(0, 4));
                list.Items.Add(new ListBoxItem { Content = grid, Padding = new Thickness(12, 8, 12, 8) });
            }
            UIElement body = list;
            if (list.Items.Count == 0) body = Ui.Subtle("Nothing has run yet.", 13).Margin(16, 16, 16, 16);

            var root = new Grid { Margin = new Thickness(16) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition());
            root.Children.Add(Ui.Column(Ui.Heading("History", 20), Ui.Subtle("Every run of every pair: from this window, on a schedule, by live sync or from a notification.", 12.5).Margin(0, 4, 0, 12)).At(0));
            root.Children.Add(Ui.Card(body, 4).At(1));
            SetPage(root, "history");
            SetFooter(Ui.Link("Open the logs folder", (s, e) =>
                {
                    Directory.CreateDirectory(DataStore.LogsFolder);
                    AppInfo.OpenFolder(DataStore.LogsFolder);
                }),
                Ui.Button("Back", "IconBack", "SecondaryButton", (s, e) => ShowPairs()));
        }
    }
}
