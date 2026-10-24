using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using Kudusync.Core;

namespace Kudusync.Views
{
    /// <summary>Windows notifications (shown as toasts on Windows 10 and 11) for runs that happen without a window.</summary>
    public static class Notifier
    {
        public static Icon AppIcon(int size = 16)
        {
            using (var stream = Dialogs.AppIconStream())
                return new Icon(stream, new System.Drawing.Size(size, size));
        }

        public static string Title(RunResult r)
        {
            switch (r.Status)
            {
                case RunStatus.Ok: return r.Conflicts > 0 ? $"{r.PairName}: done, both copies kept of {Format.Count(r.Conflicts, "file")}" : $"{r.PairName}: done";
                case RunStatus.Warnings: return $"{r.PairName}: done, with problems";
                case RunStatus.Failed: return $"{r.PairName}: didn't sync";
                case RunStatus.Stopped: return $"{r.PairName}: stopped";
                default: return $"{r.PairName}: paused";
            }
        }

        public static ToolTipIcon IconFor(RunResult r) =>
            r.Status == RunStatus.Ok ? ToolTipIcon.Info : r.Status == RunStatus.Failed ? ToolTipIcon.Error : ToolTipIcon.Warning;

        /// <summary>Shows a notification for a scheduled run and waits a little for a click (which opens the run history).</summary>
        public static void ShowAndWait(RunResult result)
        {
            if (SelfTest.Running) return;
            using (var icon = new NotifyIcon { Icon = AppIcon(), Text = "KUDUSYNC", Visible = true })
            using (var context = new ApplicationContext())
            using (var timer = new Timer { Interval = 12000 })
            {
                icon.BalloonTipClicked += (s, e) =>
                {
                    OpenHistory();
                    context.ExitThread();
                };
                icon.BalloonTipClosed += (s, e) => context.ExitThread();
                timer.Tick += (s, e) => context.ExitThread();
                timer.Start();
                icon.ShowBalloonTip(10000, Title(result), Trim(result.Summary), IconFor(result));
                System.Windows.Forms.Application.Run(context);
                icon.Visible = false;
            }
        }

        public static string Trim(string text) => string.IsNullOrEmpty(text) ? " " : text.Length > 250 ? text.Substring(0, 247) + "…" : text;

        public static void OpenHistory()
        {
            try { Process.Start(new ProcessStartInfo(AppInfo.ExePath, "--history" + DataArg()) { UseShellExecute = false }); }
            catch { /* ignore */ }
        }

        public static void OpenApp(string args = "")
        {
            try { Process.Start(new ProcessStartInfo(AppInfo.ExePath, (args + DataArg()).Trim()) { UseShellExecute = false }); }
            catch { /* ignore */ }
        }

        private static string DataArg() => string.IsNullOrEmpty(DataStore.OverrideFolder) ? string.Empty : " --data " + Args.Quote(DataStore.OverrideFolder);
    }
}
