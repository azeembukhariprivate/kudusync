using System;
using System.IO;
using System.Linq;
using System.Threading;

namespace Kudusync.Core
{
    public static class Triggers
    {
        public const string Manual = "You";
        public const string Scheduled = "Schedule";
        public const string Live = "Live sync";
        public const string Plugged = "Drive plugged in";
        public const string RightClick = "Right-click menu";
    }

    /// <summary>Runs a pair with nobody watching: scheduled runs, live sync and runs started from a notification.</summary>
    public static class Runner
    {
        public static RunResult RunUnattended(FolderPair pair, string trigger, CancellationToken token = default)
        {
            var result = new RunResult { PairId = pair.Id, PairName = pair.Name, Mode = pair.Mode, Trigger = trigger, Started = DateTime.Now };
            try
            {
                using (var session = SyncSession.Open(pair, trigger, token))
                {
                    session.Scan(null, token);
                    session.BuildPlan();
                    var stop = session.SafetyCheck(Settings.PauseMassDeletes).FirstOrDefault(i => i.NeedsConfirmation);
                    if (stop != null)
                    {
                        result.Status = RunStatus.Skipped;
                        result.Message = "Paused for safety: " + stop.Message + " Open KUDUSYNC and run it yourself if this is right.";
                        return Finish(result, session.LeftRoot, session.RightRoot, pair);
                    }
                    return session.Execute(null, token);
                }
            }
            catch (SyncProblem problem)
            {
                result.Status = problem.NotConnected ? RunStatus.Skipped : RunStatus.Failed;
                result.Message = problem.Message;
            }
            catch (OperationCanceledException)
            {
                result.Status = RunStatus.Stopped;
                result.Message = "Stopped";
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException)
            {
                result.Status = RunStatus.Failed;
                result.Message = ex.Message;
            }
            return Finish(result, pair.Left.Path, pair.Right.Path, pair);
        }

        private static RunResult Finish(RunResult result, string left, string right, FolderPair pair)
        {
            result.Finished = DateTime.Now;
            result.LogFile = RunLog.WriteText(result, left, right, pair, new[] { result.Message }, Enumerable.Empty<string>());
            RunLog.Add(result);
            return result;
        }

        /// <summary>Whether a finished run deserves a notification, given the setting (live sync only speaks up about problems).</summary>
        public static bool ShouldNotify(RunResult result, string trigger)
        {
            var problem = result.Status == RunStatus.Failed || result.Status == RunStatus.Warnings ||
                          (result.Status == RunStatus.Skipped && result.Message != null && result.Message.StartsWith("Paused", StringComparison.Ordinal)) ||
                          result.Conflicts > 0;
            if (Settings.Notify == NotifyMode.Never) return false;
            if (trigger == Triggers.Live) return problem;
            if (result.Status == RunStatus.Skipped && !problem) return false; // e.g. the USB drive wasn't plugged in
            return Settings.Notify == NotifyMode.Always || problem;
        }
    }
}
