using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Text;

namespace Kudusync.Core
{
    public enum RunStatus { Ok, Warnings, Failed, Stopped, Skipped }

    /// <summary>What one run of a pair did.</summary>
    [DataContract]
    public sealed class RunResult
    {
        [DataMember] public string PairId { get; set; }
        [DataMember] public string PairName { get; set; }
        [DataMember] public SyncMode Mode { get; set; }
        [DataMember] public string Trigger { get; set; }
        [DataMember] public DateTime Started { get; set; }
        [DataMember] public DateTime Finished { get; set; }
        [DataMember] public RunStatus Status { get; set; }
        /// <summary>Why it was skipped or failed, if it was.</summary>
        [DataMember] public string Message { get; set; }
        [DataMember] public int Copied { get; set; }
        [DataMember] public int Updated { get; set; }
        [DataMember] public int Deleted { get; set; }
        [DataMember] public int Renamed { get; set; }
        [DataMember] public int Conflicts { get; set; }
        [DataMember] public int Moved { get; set; }
        [DataMember] public int FoldersCreated { get; set; }
        [DataMember] public int FoldersDeleted { get; set; }
        [DataMember] public int Failed { get; set; }
        [DataMember] public long Bytes { get; set; }
        [DataMember] public string LogFile { get; set; }
        /// <summary>The first few problems, for showing without opening the log.</summary>
        [DataMember] public List<string> Problems { get; set; } = new List<string>();

        public int Changes => Copied + Updated + Deleted + Renamed + Conflicts + Moved;

        public string Summary
        {
            get
            {
                // Shown in windows, notifications and logs: each piece in the language in use.
                if (Status == RunStatus.Skipped || (Status == RunStatus.Failed && Changes == 0)) return Lang.T(Message) ?? Lang.T("Didn't run");
                var parts = new List<string>();
                if (Moved > 0) parts.Add(Lang.T("{0} moved", Moved));
                if (Copied > 0) parts.Add(Lang.T("{0} copied", Copied));
                if (Updated > 0) parts.Add(Lang.T("{0} updated", Updated));
                if (Renamed > 0) parts.Add(Lang.T("{0} renamed", Renamed));
                if (Deleted > 0) parts.Add(Lang.T("{0} deleted", Deleted));
                if (Conflicts > 0) parts.Add(Lang.T("{0} (both kept)", Format.Count(Conflicts, "conflict")));
                if (Failed > 0) parts.Add(Lang.T("{0} couldn't be done", Failed));
                var text = parts.Count == 0 ? Lang.T("Already in step, nothing to do") : string.Join(", ", parts);
                if (Bytes > 0) text += " · " + Format.Size(Bytes);
                if (Status == RunStatus.Stopped) text = Lang.T("Stopped: {0}", text);
                return text;
            }
        }

        public string StatusText
        {
            get
            {
                switch (Status)
                {
                    case RunStatus.Ok: return Lang.T("Done");
                    case RunStatus.Warnings: return Lang.T("Done, with problems");
                    case RunStatus.Failed: return Lang.T("Failed");
                    case RunStatus.Stopped: return Lang.T("Stopped");
                    default: return Lang.T("Skipped");
                }
            }
        }
    }

    /// <summary>The list of recent runs (shared by the app, scheduled runs and the tray) and each run's text log.</summary>
    public static class RunLog
    {
        private const int Keep = 500;

        public static List<RunResult> All()
        {
            using (DataStore.Lock("runs"))
                return DataStore.ReadJson<List<RunResult>>(DataStore.RunsFile) ?? new List<RunResult>();
        }

        public static void Add(RunResult result)
        {
            using (DataStore.Lock("runs"))
            {
                var list = DataStore.ReadJson<List<RunResult>>(DataStore.RunsFile) ?? new List<RunResult>();
                list.Add(result);
                if (list.Count > Keep) list = list.Skip(list.Count - Keep).ToList();
                DataStore.WriteJson(DataStore.RunsFile, list);
            }
        }

        public static RunResult Latest(IEnumerable<RunResult> runs, string pairId) =>
            runs.Where(r => r.PairId == pairId).OrderByDescending(r => r.Started).FirstOrDefault();

        public static void Forget(string pairId)
        {
            using (DataStore.Lock("runs"))
            {
                var list = DataStore.ReadJson<List<RunResult>>(DataStore.RunsFile) ?? new List<RunResult>();
                if (list.RemoveAll(r => r.PairId == pairId) > 0) DataStore.WriteJson(DataStore.RunsFile, list);
            }
        }

        /// <summary>Writes a run's full log, one line per change, and returns its path.</summary>
        public static string WriteText(RunResult result, string leftRoot, string rightRoot, FolderPair pair, IEnumerable<string> warnings, IEnumerable<string> lines)
        {
            try
            {
                var folder = Path.Combine(DataStore.LogsFolder, result.Started.ToString("yyyy-MM"));
                Directory.CreateDirectory(folder);
                var file = Path.Combine(folder, result.Started.ToString("yyyy-MM-dd HHmmss") + " " + Paths.SafeName(result.PairName) + ".txt");
                var sb = new StringBuilder();
                sb.AppendLine("KUDUSYNC " + AppInfo.VersionText + " · " + result.PairName);
                sb.AppendLine(new string('=', 60));
                sb.AppendLine("Mode:        " + FolderPair.ModeName(result.Mode));
                sb.AppendLine(((pair?.LeftTitle ?? "Source") + ":").PadRight(13) + Paths.Display(leftRoot));
                sb.AppendLine(((pair?.RightTitle ?? "Destination") + ":").PadRight(13) + Paths.Display(rightRoot));
                sb.AppendLine("Started by:  " + result.Trigger);
                sb.AppendLine("Started:     " + result.Started.ToString("yyyy-MM-dd HH:mm:ss"));
                sb.AppendLine("Finished:    " + result.Finished.ToString("yyyy-MM-dd HH:mm:ss"));
                sb.AppendLine("Result:      " + result.StatusText + " · " + result.Summary);
                var w = warnings?.ToList() ?? new List<string>();
                if (w.Count > 0)
                {
                    sb.AppendLine();
                    foreach (var line in w) sb.AppendLine("Note: " + line);
                }
                sb.AppendLine();
                foreach (var line in lines ?? Enumerable.Empty<string>()) sb.AppendLine(line);
                File.WriteAllText(file, sb.ToString(), Encoding.UTF8);
                return file;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return null;
            }
        }
    }
}
