// The folder pairs and what each keeps: its two places, the way it syncs, the filters, the schedule and the options.
using System;
using System.Runtime.Serialization;

namespace Kudusync.Core
{
    public enum SyncMode
    {
        /// <summary>One-way: the destination becomes an exact copy of the source.</summary>
        Mirror,
        /// <summary>One-way: new and changed files are copied; nothing is deleted in the destination.</summary>
        Update,
        /// <summary>Changes on either side are copied to the other, deletions included.</summary>
        TwoWay,
        /// <summary>Files are copied to the destination, then removed from the source.</summary>
        Move
    }

    /// <summary>When a pair runs by itself: never, daily, weekly, every few hours, or at sign-in.</summary>
    public enum ScheduleKind { None, Daily, Weekly, EveryHours, AtLogon }

    /// <summary>A folder on a drive, a USB drive or a network share.</summary>
    [DataContract]
    public sealed class Location
    {
        /// <summary>The folder as it was chosen: "E:\Backup" or "\\nas\share\Photos".</summary>
        [DataMember] public string Path { get; set; }

        /// <summary>
        /// The serial number of the drive it's on (local drives only). It lets KUDUSYNC find a USB drive after Windows
        /// gives it another letter, and stops it from syncing into a different drive that got the same letter.
        /// </summary>
        [DataMember] public string VolumeSerial { get; set; }

        [DataMember] public string VolumeLabel { get; set; }

        /// <summary>True for a network folder.</summary>
        public bool IsNetwork => Paths.IsUnc(Path);

        /// <summary>A copy of the place.</summary>
        public Location Clone() => (Location)MemberwiseClone();

        /// <summary>Its path.</summary>
        public override string ToString() => Path ?? string.Empty;
    }

    [DataContract]
    public sealed class Filters
    {
        /// <summary>File name patterns to include, separated by semicolons. "*" means everything.</summary>
        [DataMember] public string Include { get; set; }
        [DataMember] public string Exclude { get; set; }
        /// <summary>Folder names (anywhere) or paths inside the pair ("Projects\bin") to leave out.</summary>
        [DataMember] public string ExcludeFolders { get; set; }
        [DataMember] public bool SkipHidden { get; set; }
        [DataMember] public bool SkipSystem { get; set; }

        /// <summary>What's left out at first: Office's temporary files, *.tmp and Thumbs.db.</summary>
        public const string DefaultExclude = "~$*; *.tmp; Thumbs.db";

        /// <summary>Filters with the usual settings.</summary>
        public Filters() => SetDefaults(default);

        /// <summary>Puts the usual settings in (also after reading an older file).</summary>
        [OnDeserializing]
        private void SetDefaults(StreamingContext context)
        {
            Include = "*";
            Exclude = DefaultExclude;
            ExcludeFolders = string.Empty;
            SkipHidden = false;
            SkipSystem = true;
        }

        /// <summary>A copy of the filters.</summary>
        public Filters Clone() => (Filters)MemberwiseClone();
    }

    [DataContract]
    public sealed class Schedule
    {
        [DataMember] public ScheduleKind Kind { get; set; }
        /// <summary>"HH:mm", local time.</summary>
        [DataMember] public string Time { get; set; }
        /// <summary>Weekly: bit 0 = Sunday … bit 6 = Saturday.</summary>
        [DataMember] public int Days { get; set; }
        [DataMember] public int Hours { get; set; }

        /// <summary>A schedule with the usual settings.</summary>
        public Schedule() => SetDefaults(default);

        /// <summary>Puts the usual settings in (also after reading an older file).</summary>
        [OnDeserializing]
        private void SetDefaults(StreamingContext context)
        {
            Kind = ScheduleKind.None;
            Time = "21:00";
            Days = 0b0111110; // Monday to Friday
            Hours = 4;
        }

        /// <summary>The time of day it runs (9:00 when the time can't be read).</summary>
        public TimeSpan TimeOfDay => TimeSpan.TryParse(Time, out var t) && t >= TimeSpan.Zero && t < TimeSpan.FromDays(1) ? t : new TimeSpan(21, 0, 0);

        /// <summary>A copy of the schedule.</summary>
        public Schedule Clone() => (Schedule)MemberwiseClone();

        /// <summary>The schedule in words, like "Every day at 21:00".</summary>
        public string Describe()
        {
            var time = DateTime.Today.Add(TimeOfDay).ToString("HH:mm");
            switch (Kind)
            {
                // Shown in many places put together with other texts, so each is in the language in use here.
                case ScheduleKind.Daily: return Lang.T("Every day at {0}", time);
                case ScheduleKind.Weekly: return Lang.T("{0} at {1}", DayNames(), time);
                case ScheduleKind.EveryHours: return Hours == 1 ? Lang.T("Every hour") : Lang.T("Every {0} hours", Hours);
                case ScheduleKind.AtLogon: return Lang.T("When you sign in to Windows");
                default: return Lang.T("Only when you run it");
            }
        }

        /// <summary>When the schedule next runs after <paramref name="now"/> (null when it isn't time-based).</summary>
        public DateTime? NextAfter(DateTime now)
        {
            switch (Kind)
            {
                case ScheduleKind.Daily:
                {
                    var t = now.Date.Add(TimeOfDay);
                    return t > now ? t : t.AddDays(1);
                }
                case ScheduleKind.Weekly:
                    if ((Days & 0x7F) == 0) return null;
                    for (var i = 0; i <= 7; i++)
                    {
                        var t = now.Date.AddDays(i).Add(TimeOfDay);
                        if (t > now && (Days & (1 << (int)t.DayOfWeek)) != 0) return t;
                    }
                    return null;
                case ScheduleKind.EveryHours:
                {
                    var step = Math.Max(1, Math.Min(23, Hours));
                    for (var h = 0; h <= 24; h += step)
                    {
                        var t = now.Date.AddHours(h);
                        if (t > now) return t;
                    }
                    return now.Date.AddDays(1);
                }
                default:
                    return null;
            }
        }

        /// <summary>Short names of the days.</summary>
        public static readonly string[] ShortDays = { "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat" };

        /// <summary>The days chosen, in words.</summary>
        public string DayNames()
        {
            if ((Days & 0x7F) == 0x7F) return Lang.T("Every day");
            if ((Days & 0x7F) == 0b0111110) return Lang.T("Weekdays");
            if ((Days & 0x7F) == 0b1000001) return Lang.T("Weekends");
            var names = new System.Collections.Generic.List<string>();
            // Monday first, Sunday last.
            for (var i = 1; i <= 7; i++)
                if ((Days & (1 << (i % 7))) != 0) names.Add(Lang.T(ShortDays[i % 7]));
            return names.Count == 0 ? Lang.T("No days") : string.Join(", ", names);
        }
    }

    /// <summary>Two folders kept in step, and how.</summary>
    [DataContract]
    public sealed class FolderPair
    {
        [DataMember] public string Id { get; set; }
        [DataMember] public string Name { get; set; }
        /// <summary>The source in the one-way modes.</summary>
        [DataMember] public Location Left { get; set; }
        /// <summary>The destination in the one-way modes.</summary>
        [DataMember] public Location Right { get; set; }
        [DataMember] public SyncMode Mode { get; set; }
        [DataMember] public Filters Filters { get; set; }

        /// <summary>Read each copy back and compare its checksum with the original.</summary>
        [DataMember] public bool Verify { get; set; }
        /// <summary>
        /// Also compare the contents of files whose size and modified time match, to catch changes those don't show
        /// (SyncToy's "Check file contents"). Slower: every such file is read on both sides.
        /// </summary>
        [DataMember] public bool CompareContents { get; set; }
        /// <summary>Rename files in the destination when they were renamed or moved in the source, instead of copying them again.</summary>
        [DataMember] public bool DetectRenames { get; set; }

        /// <summary>Days to keep replaced and deleted files: 0 = forever, -1 = don't keep them (delete permanently).</summary>
        [DataMember] public int KeepDays { get; set; }
        /// <summary>Where to keep them. Empty: a "_KUDUSYNC History" folder at the top of the pair's folder on that drive.</summary>
        [DataMember] public string HistoryFolder { get; set; }

        [DataMember] public Schedule Schedule { get; set; }
        /// <summary>Sync a short while after files change (while KUDUSYNC runs in the notification area).</summary>
        [DataMember] public bool LiveSync { get; set; }
        [DataMember] public int LiveDelaySeconds { get; set; }
        /// <summary>Show a notification when one of the pair's drives is plugged in.</summary>
        [DataMember] public bool NotifyOnPlug { get; set; }
        [DataMember] public bool InRunAll { get; set; }
        [DataMember] public DateTime Created { get; set; }

        /// <summary>A pair with the usual settings.</summary>
        public FolderPair() => SetDefaults(default);

        /// <summary>Puts the usual settings in (also after reading an older file).</summary>
        [OnDeserializing]
        private void SetDefaults(StreamingContext context)
        {
            Id = Guid.NewGuid().ToString("N");
            Name = string.Empty;
            Left = new Location();
            Right = new Location();
            Mode = SyncMode.Mirror;
            Filters = new Filters();
            Verify = false;
            CompareContents = false;
            DetectRenames = true;
            KeepDays = 30;
            HistoryFolder = string.Empty;
            Schedule = new Schedule();
            LiveSync = false;
            LiveDelaySeconds = 30;
            NotifyOnPlug = true;
            InRunAll = true;
            Created = DateTime.UtcNow;
        }

        /// <summary>Fills in what's missing after reading an older file.</summary>
        [OnDeserialized]
        private void Fix(StreamingContext context)
        {
            Left = Left ?? new Location();
            Right = Right ?? new Location();
            Filters = Filters ?? new Filters();
            Schedule = Schedule ?? new Schedule();
            if (LiveDelaySeconds < 1) LiveDelaySeconds = 30;
        }

        /// <summary>True when files go one way only (not two-way).</summary>
        public bool IsOneWay => Mode != SyncMode.TwoWay;

        /// <summary>The first side's name.</summary>
        public string LeftTitle => IsOneWay ? "Source" : "Folder 1";
        /// <summary>The second side's name.</summary>
        public string RightTitle => IsOneWay ? "Destination" : "Folder 2";

        /// <summary>A copy of the pair.</summary>
        public FolderPair Clone()
        {
            var copy = (FolderPair)MemberwiseClone();
            copy.Left = Left.Clone();
            copy.Right = Right.Clone();
            copy.Filters = Filters.Clone();
            copy.Schedule = Schedule.Clone();
            return copy;
        }

        /// <summary>A way of syncing's name.</summary>
        public static string ModeName(SyncMode mode)
        {
            switch (mode)
            {
                case SyncMode.Mirror: return Lang.T("Mirror");
                case SyncMode.Update: return Lang.T("Update");
                case SyncMode.TwoWay: return Lang.T("Two-way sync");
                default: return Lang.T("Move");
            }
        }

        /// <summary>What a way of syncing does, in a sentence.</summary>
        public static string ModeDescription(SyncMode mode)
        {
            switch (mode)
            {
                case SyncMode.Mirror: return "The destination becomes an exact copy of the source. Files removed from the source are removed from the destination too.";
                case SyncMode.Update: return "New and changed files are copied to the destination. Nothing is ever deleted there.";
                case SyncMode.TwoWay: return "Changes on either side are copied to the other, deletions included. If a file changed on both sides, both copies are kept.";
                default: return "Files are copied to the destination and then removed from the source.";
            }
        }

        public string KeepText
        {
            get
            {
                if (KeepDays < 0) return Lang.T("Not kept (deleted permanently)");
                if (KeepDays == 0) return Lang.T("Kept forever");
                return KeepDays == 1 ? Lang.T("Kept for 1 day") : Lang.T("Kept for {0} days", KeepDays);
            }
        }
    }
}
