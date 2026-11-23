// Backups that need looking at: a scheduled run that failed or had problems, or a pair that hasn't backed up for two
// scheduled times; and what was already seen or notified.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace Kudusync.Core
{
    public enum AlertLevel
    {
        /// <summary>A scheduled backup ran, but some files couldn't be copied (orange).</summary>
        Problems,
        /// <summary>A scheduled backup didn't work, or was paused for safety (red).</summary>
        Failed,
        /// <summary>A scheduled backup hasn't run for twice its gap (red).</summary>
        Missed
    }

    /// <summary>Something about a scheduled backup that needs looking at.</summary>
    public sealed class Alert
    {
        /// <summary>The pair's id.</summary>
        public string PairId { get; set; }
        /// <summary>The pair's name.</summary>
        public string PairName { get; set; }
        /// <summary>How serious it is.</summary>
        public AlertLevel Level { get; set; }
        /// <summary>When it happened: the run's end, or the scheduled time that was missed.</summary>
        public DateTime When { get; set; }
        /// <summary>The run it's about (null for a missed backup with no run at all).</summary>
        public RunResult Run { get; set; }
        /// <summary>The last time the pair really backed up (missed backups), if ever.</summary>
        public DateTime? LastDone { get; set; }
        /// <summary>When the pair is meant to run, in words.</summary>
        public string ScheduleText { get; set; }

        /// <summary>True for a red alert (failed, paused or missed), not only problems.</summary>
        public bool IsRed => Level != AlertLevel.Problems;

        /// <summary>The same alert always has the same key, so it's only notified once.</summary>
        public string Key => PairId + "|" + Level + "|" + When.ToUniversalTime().Ticks;

        public string Title
        {
            get
            {
                switch (Level)
                {
                    case AlertLevel.Failed: return Lang.T("\"{0}\" didn't back up", PairName);
                    case AlertLevel.Missed: return Lang.T("\"{0}\" hasn't backed up for a while", PairName);
                    default: return Lang.T("\"{0}\" backed up, with problems", PairName);
                }
            }
        }

        public string Text
        {
            get
            {
                var when = When.ToString("ddd d MMM, HH:mm");
                switch (Level)
                {
                    case AlertLevel.Failed:
                        return Lang.T("The scheduled backup on {0} didn't work: {1}", when, Reason(Run));
                    case AlertLevel.Missed:
                        return LastDone.HasValue
                            ? Lang.T("Last backed up {0}. It should run: {1}.", LastDone.Value.ToString("ddd d MMM, HH:mm"), ScheduleText) + Missing(Run)
                            : Lang.T("It hasn't backed up since it was scheduled. It should run: {0}.", ScheduleText) + Missing(Run);
                    default:
                        var skipped = Run == null ? 0 : Math.Max(Run.Failed, 1);
                        return Lang.T("The scheduled backup on {0} ran, but {1} couldn't be copied.", when, Format.Count(skipped, "file")) +
                               (Run?.Problems?.Count > 0 ? " " + Lang.T("For example: {0}", Run.Problems[0]) : string.Empty);
                }
            }
        }

        /// <summary>Why a run counts as failed, in words.</summary>
        private static string Reason(RunResult run)
        {
            if (run == null || string.IsNullOrWhiteSpace(run.Message)) return Lang.T("see its history for what went wrong.");
            return Lang.T(run.Message);
        }

        /// <summary>For a missed backup: why its last try didn't back up, when there was one.</summary>
        private static string Missing(RunResult run) =>
            run != null && !string.IsNullOrWhiteSpace(run.Message) ? " " + Lang.T("Its last try said: {0}", Lang.T(run.Message)) : string.Empty;
    }

    /// <summary>What KUDUSYNC remembers about alerts: when each pair was first watched, what was looked at, what was notified.</summary>
    [DataContract]
    public sealed class AlertState
    {
        /// <summary>When each pair's schedule was first seen (a backup can't be missed before it was scheduled).</summary>
        [DataMember] public Dictionary<string, DateTime> Watched { get; set; } = new Dictionary<string, DateTime>();
        /// <summary>Alerts about each pair up to this time have been looked at.</summary>
        [DataMember] public Dictionary<string, DateTime> Seen { get; set; } = new Dictionary<string, DateTime>();
        /// <summary>Alerts already notified, by key.</summary>
        [DataMember] public List<string> Notified { get; set; } = new List<string>();

        /// <summary>Fills in what's missing after reading an older file.</summary>
        [OnDeserialized]
        private void Fix(StreamingContext c)
        {
            Watched = Watched ?? new Dictionary<string, DateTime>();
            Seen = Seen ?? new Dictionary<string, DateTime>();
            Notified = Notified ?? new List<string>();
        }
    }

    /// <summary>
    /// Finds scheduled backups that need looking at: the last scheduled run failed (or was paused for safety), ran with
    /// problems, or the pair hasn't really backed up for twice its usual gap (two scheduled times have gone by, for
    /// example because the PC was off or the drive wasn't plugged in). An alert stays until it's looked at or a later
    /// run goes well.
    /// </summary>
    public static class Alerts
    {
        /// <summary>A little time for a backup to start and finish before its time counts as missed.</summary>
        public static readonly TimeSpan Grace = TimeSpan.FromMinutes(30);

        /// <summary>Where what was seen and notified is kept.</summary>
        public static string StateFile => Path.Combine(DataStore.Folder, "alerts.json");

        /// <summary>The pairs that need looking at, from their schedules and runs, at this time.</summary>
        public static List<Alert> Find(IEnumerable<FolderPair> pairs, IList<RunResult> runs, DateTime now, AlertState state)
        {
            var alerts = new List<Alert>();
            foreach (var pair in pairs)
            {
                var schedule = pair.Schedule;
                if (schedule == null || schedule.Kind == ScheduleKind.None)
                {
                    state.Watched.Remove(pair.Id);
                    continue;
                }
                if (!state.Watched.ContainsKey(pair.Id)) state.Watched[pair.Id] = now;
                var mine = runs.Where(r => r.PairId == pair.Id).OrderBy(r => r.Finished).ToList();
                var latest = mine.LastOrDefault();
                var seen = state.Seen.TryGetValue(pair.Id, out var s) ? s : DateTime.MinValue;

                // The last run, if it was a scheduled one that went wrong (a later good run of any kind clears it).
                if (latest != null && latest.Trigger == Triggers.Scheduled && latest.Finished > seen)
                {
                    var paused = latest.Status == RunStatus.Skipped && latest.Message != null && latest.Message.StartsWith("Paused", StringComparison.Ordinal);
                    if (latest.Status == RunStatus.Failed || paused)
                        alerts.Add(new Alert { PairId = pair.Id, PairName = pair.Name, Level = AlertLevel.Failed, When = latest.Finished, Run = latest });
                    else if (latest.Status == RunStatus.Warnings)
                        alerts.Add(new Alert { PairId = pair.Id, PairName = pair.Name, Level = AlertLevel.Problems, When = latest.Finished, Run = latest });
                }

                // Not really backed up for two scheduled times: missed.
                var done = mine.LastOrDefault(r => r.Status == RunStatus.Ok || r.Status == RunStatus.Warnings);
                // From the last real backup, or from when the schedule was first seen if that's later (a pair given a
                // schedule today hasn't missed anything yet, however long ago it last ran).
                var since = done == null || done.Finished < state.Watched[pair.Id] ? state.Watched[pair.Id] : done.Finished;
                var missedAt = MissedSince(schedule, since, now);
                if (missedAt.HasValue && missedAt.Value > seen && !alerts.Any(a => a.PairId == pair.Id && a.Level == AlertLevel.Failed))
                    alerts.Add(new Alert
                    {
                        PairId = pair.Id, PairName = pair.Name, Level = AlertLevel.Missed, When = missedAt.Value,
                        Run = latest != null && latest.Finished > since ? latest : null, LastDone = done?.Finished, ScheduleText = schedule.Describe()
                    });
            }
            foreach (var id in state.Watched.Keys.Where(k => pairs.All(p => p.Id != k)).ToList()) state.Watched.Remove(id);
            return alerts.OrderByDescending(a => a.IsRed).ThenByDescending(a => a.When).ToList();
        }

        /// <summary>
        /// When a backup counts as missed: the second scheduled time after <paramref name="since"/> (plus a little grace) has
        /// gone by without it. Returns that scheduled time, or null. Backups at sign-in can't be missed this way.
        /// </summary>
        public static DateTime? MissedSince(Schedule schedule, DateTime since, DateTime now)
        {
            var first = schedule.NextAfter(since);
            if (first == null) return null;
            var second = schedule.NextAfter(first.Value);
            if (second == null || second.Value + Grace > now) return null;
            // The last scheduled time that has gone by: what the alert is about.
            var last = second.Value;
            for (var next = schedule.NextAfter(last); next.HasValue && next.Value + Grace <= now; next = schedule.NextAfter(next.Value)) last = next.Value;
            return last;
        }

        // ------------------------------------------------------------------ remembering

        /// <summary>Reads what was seen and notified (empty when there's nothing yet).</summary>
        public static AlertState Load()
        {
            try
            {
                if (!File.Exists(StateFile)) return new AlertState();
                using (var s = File.OpenRead(StateFile))
                    return (AlertState)new DataContractJsonSerializer(typeof(AlertState), new DataContractJsonSerializerSettings { UseSimpleDictionaryFormat = true }).ReadObject(s) ?? new AlertState();
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is SerializationException || ex is InvalidCastException)
            {
                return new AlertState();
            }
        }

        /// <summary>Keeps what was seen and notified.</summary>
        public static void Save(AlertState state)
        {
            try
            {
                Directory.CreateDirectory(DataStore.Folder);
                if (state.Notified.Count > 500) state.Notified = state.Notified.Skip(state.Notified.Count - 500).ToList();
                var temp = StateFile + ".tmp";
                using (var s = File.Create(temp))
                    new DataContractJsonSerializer(typeof(AlertState), new DataContractJsonSerializerSettings { UseSimpleDictionaryFormat = true }).WriteObject(s, state);
                if (File.Exists(StateFile)) File.Delete(StateFile);
                File.Move(temp, StateFile);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { /* tried again next time */ }
        }

        /// <summary>The alerts now, from the pairs and runs on disk (and the watched times saved).</summary>
        public static List<Alert> Current(DateTime now)
        {
            var state = Load();
            var alerts = Find(DataStore.LoadPairs(), RunLog.All(), now, state);
            Save(state);
            return alerts;
        }

        /// <summary>The alerts about a pair (or every pair, with null) have been looked at.</summary>
        public static void MarkSeen(string pairId, DateTime now)
        {
            var state = Load();
            foreach (var p in DataStore.LoadPairs().Where(p => pairId == null || p.Id == pairId)) state.Seen[p.Id] = now;
            Save(state);
        }

        /// <summary>The alerts not notified yet; they're remembered as notified.</summary>
        public static List<Alert> TakeNew(DateTime now)
        {
            var state = Load();
            var alerts = Find(DataStore.LoadPairs(), RunLog.All(), now, state).Where(a => !state.Notified.Contains(a.Key)).ToList();
            state.Notified.AddRange(alerts.Select(a => a.Key));
            Save(state);
            return alerts;
        }
    }
}
