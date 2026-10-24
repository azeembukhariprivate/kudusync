using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security;
using System.Text;

namespace Kudusync.Core
{
    /// <summary>
    /// Schedules are Windows Task Scheduler tasks (one per pair), so they run even when KUDUSYNC is closed, and missed
    /// runs (the PC was off) happen as soon as possible afterwards. Tasks run as you, without administrator rights.
    /// </summary>
    public static class Schedules
    {
        public const string Prefix = "KUDUSYNC - ";

        public static string TaskName(FolderPair pair) => Prefix + Paths.SafeName(pair.Name).Replace("\\", "_") + " (" + pair.Id.Substring(0, Math.Min(8, pair.Id.Length)) + ")";

        /// <summary>The task definition, in Task Scheduler's XML format.</summary>
        public static string TaskXml(FolderPair pair, string exe, string user, DateTime today)
        {
            var s = pair.Schedule;
            var start = today.Date.Add(s.TimeOfDay).ToString("yyyy-MM-ddTHH:mm:ss");
            string trigger;
            switch (s.Kind)
            {
                case ScheduleKind.Daily:
                    trigger = $"<CalendarTrigger><StartBoundary>{start}</StartBoundary><Enabled>true</Enabled><ScheduleByDay><DaysInterval>1</DaysInterval></ScheduleByDay></CalendarTrigger>";
                    break;
                case ScheduleKind.Weekly:
                    var days = new StringBuilder();
                    string[] names = { "Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday" };
                    for (var i = 0; i < 7; i++)
                        if ((s.Days & (1 << i)) != 0) days.Append("<" + names[i] + " />");
                    if (days.Length == 0) days.Append("<Monday />");
                    trigger = $"<CalendarTrigger><StartBoundary>{start}</StartBoundary><Enabled>true</Enabled><ScheduleByWeek><DaysOfWeek>{days}</DaysOfWeek><WeeksInterval>1</WeeksInterval></ScheduleByWeek></CalendarTrigger>";
                    break;
                case ScheduleKind.EveryHours:
                    var hours = Math.Max(1, Math.Min(23, s.Hours));
                    var midnight = today.Date.ToString("yyyy-MM-ddTHH:mm:ss");
                    trigger = $"<CalendarTrigger><Repetition><Interval>PT{hours}H</Interval><Duration>P1D</Duration><StopAtDurationEnd>false</StopAtDurationEnd></Repetition>" +
                              $"<StartBoundary>{midnight}</StartBoundary><Enabled>true</Enabled><ScheduleByDay><DaysInterval>1</DaysInterval></ScheduleByDay></CalendarTrigger>";
                    break;
                case ScheduleKind.AtLogon:
                    trigger = $"<LogonTrigger><Enabled>true</Enabled><UserId>{Escape(user)}</UserId><Delay>PT1M</Delay></LogonTrigger>";
                    break;
                default:
                    throw new ArgumentException("This pair has no schedule.");
            }

            return "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n" +
                   "<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">" +
                   "<RegistrationInfo><Author>KUDUSYNC</Author><Description>" + Escape($"Runs the KUDUSYNC pair \"{pair.Name}\" ({s.Describe().ToLowerInvariant()}).") + "</Description></RegistrationInfo>" +
                   "<Triggers>" + trigger + "</Triggers>" +
                   "<Principals><Principal id=\"Author\"><UserId>" + Escape(user) + "</UserId><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal></Principals>" +
                   "<Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>" +
                   "<StopIfGoingOnBatteries>false</StopIfGoingOnBatteries><AllowHardTerminate>true</AllowHardTerminate><StartWhenAvailable>true</StartWhenAvailable>" +
                   "<RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable><IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings>" +
                   "<AllowStartOnDemand>true</AllowStartOnDemand><Enabled>true</Enabled><Hidden>false</Hidden><RunOnlyIfIdle>false</RunOnlyIfIdle><WakeToRun>false</WakeToRun>" +
                   "<ExecutionTimeLimit>PT72H</ExecutionTimeLimit><Priority>7</Priority></Settings>" +
                   "<Actions Context=\"Author\"><Exec><Command>" + Escape(exe) + "</Command><Arguments>" + Escape("--run-scheduled " + pair.Id) + "</Arguments></Exec></Actions>" +
                   "</Task>";
        }

        private static string Escape(string text) => SecurityElement.Escape(text ?? string.Empty);

        private static string CurrentUser()
        {
            try { return System.Security.Principal.WindowsIdentity.GetCurrent().Name; }
            catch { return Environment.UserDomainName + "\\" + Environment.UserName; }
        }

        /// <summary>Creates or replaces the pair's task, or removes it if the pair has no schedule. Returns null, or what went wrong.</summary>
        public static string Apply(FolderPair pair)
        {
            if (pair.Schedule.Kind == ScheduleKind.None)
            {
                Remove(pair.Id);
                return null;
            }
            var file = Path.Combine(Path.GetTempPath(), "kudusync-task-" + Guid.NewGuid().ToString("N") + ".xml");
            try
            {
                File.WriteAllText(file, TaskXml(pair, AppInfo.ExePath, CurrentUser(), DateTime.Today), Encoding.Unicode);
                RemoveOthers(pair); // a renamed pair has a task under its old name
                var (code, output) = SchTasks("/Create", "/TN", TaskName(pair), "/XML", file, "/F");
                return code == 0 ? null : "Windows Task Scheduler said: " + output.Trim();
            }
            finally
            {
                try { File.Delete(file); }
                catch { /* ignore */ }
            }
        }

        public static void Remove(string pairId)
        {
            foreach (var name in TaskNames().Where(n => n.EndsWith("(" + pairId.Substring(0, Math.Min(8, pairId.Length)) + ")", StringComparison.OrdinalIgnoreCase)))
                SchTasks("/Delete", "/TN", name, "/F");
        }

        private static void RemoveOthers(FolderPair pair)
        {
            var mine = TaskName(pair);
            var id = "(" + pair.Id.Substring(0, Math.Min(8, pair.Id.Length)) + ")";
            foreach (var name in TaskNames().Where(n => n.EndsWith(id, StringComparison.OrdinalIgnoreCase) && !n.Equals(mine, StringComparison.OrdinalIgnoreCase)))
                SchTasks("/Delete", "/TN", name, "/F");
        }

        /// <summary>Removes every KUDUSYNC task (used when uninstalling).</summary>
        public static void RemoveAll()
        {
            foreach (var name in TaskNames()) SchTasks("/Delete", "/TN", name, "/F");
        }

        public static bool Exists(FolderPair pair) => TaskNames().Any(n => n.Equals(TaskName(pair), StringComparison.OrdinalIgnoreCase));

        /// <summary>Starts the pair's task now (as Windows would on schedule).</summary>
        public static bool RunNow(FolderPair pair) => SchTasks("/Run", "/TN", TaskName(pair)).Code == 0;

        /// <summary>Sets up every pair's task again when KUDUSYNC has moved (e.g. the portable version was copied elsewhere).</summary>
        public static void Refresh(IEnumerable<FolderPair> pairs)
        {
            if (string.Equals(Settings.ScheduledExe, AppInfo.ExePath, StringComparison.OrdinalIgnoreCase)) return;
            var ok = true;
            foreach (var pair in pairs.Where(p => p.Schedule.Kind != ScheduleKind.None))
                ok &= Apply(pair) == null;
            if (ok) Settings.ScheduledExe = AppInfo.ExePath;
        }

        public static List<string> TaskNames()
        {
            var (code, output) = SchTasks("/Query", "/FO", "CSV", "/NH");
            var names = new List<string>();
            if (code != 0) return names;
            foreach (var line in output.Split('\n'))
            {
                if (!line.StartsWith("\"")) continue;
                var end = line.IndexOf('"', 1);
                if (end < 0) continue;
                var name = line.Substring(1, end - 1).TrimStart('\\');
                if (name.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) && !names.Contains(name)) names.Add(name);
            }
            return names;
        }

        private static (int Code, string Output) SchTasks(params string[] args)
        {
            if (!Paths.IsWindows) return (1, "Task Scheduler is only on Windows.");
            try
            {
                var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"), Args.Join(args))
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using (var p = Process.Start(info))
                {
                    var stdout = p.StandardOutput.ReadToEndAsync();
                    var stderr = p.StandardError.ReadToEndAsync();
                    if (!p.WaitForExit(30000))
                    {
                        try { p.Kill(); }
                        catch { /* ignore */ }
                        return (1, "Task Scheduler didn't respond.");
                    }
                    return (p.ExitCode, stdout.Result + stderr.Result);
                }
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception || ex is InvalidOperationException || ex is IOException)
            {
                return (1, ex.Message);
            }
        }
    }
}
