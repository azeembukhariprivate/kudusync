using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Kudusync.Core
{
    /// <summary>What a file looked like on both sides when they were last in step.</summary>
    public sealed class RecordEntry
    {
        public string Path { get; set; }
        public bool IsDir { get; set; }
        public long Size { get; set; }
        public long LeftMtime { get; set; }
        public long RightMtime { get; set; }
        public long LeftId { get; set; }
        public long RightId { get; set; }

        public long Mtime(Side side) => side == Side.Left ? LeftMtime : RightMtime;
        public long Id(Side side) => side == Side.Left ? LeftId : RightId;
    }

    /// <summary>
    /// A pair's record of its last sync, kept on this PC only (nothing is written into the pair's folders). Two-way sync
    /// uses it to tell a file that's new on one side from one that was deleted on the other; all modes use it to spot
    /// renamed files. If it's lost, the next run is treated as a first run, which never deletes anything.
    /// </summary>
    public sealed class SyncRecord
    {
        private const string Magic = "KSR1";

        public Dictionary<string, RecordEntry> Items { get; } = new Dictionary<string, RecordEntry>(Paths.Comparer);

        /// <summary>The two folders this record is for. If either changes, the record no longer applies.</summary>
        public string LeftRoot { get; set; } = string.Empty;
        public string RightRoot { get; set; } = string.Empty;

        public bool IsEmpty => Items.Count == 0;

        public RecordEntry Get(string path) => path != null && Items.TryGetValue(path, out var e) ? e : null;

        public void Save(string file)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file));
            var temp = file + ".saving";
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write))
            using (var zip = new GZipStream(stream, CompressionLevel.Fastest))
            using (var w = new BinaryWriter(zip, Encoding.UTF8))
            {
                w.Write(Magic);
                w.Write(LeftRoot ?? string.Empty);
                w.Write(RightRoot ?? string.Empty);
                w.Write(Items.Count);
                foreach (var e in Items.Values)
                {
                    w.Write(e.Path);
                    w.Write(e.IsDir);
                    w.Write(e.Size);
                    w.Write(e.LeftMtime);
                    w.Write(e.RightMtime);
                    w.Write(e.LeftId);
                    w.Write(e.RightId);
                }
            }
            if (File.Exists(file)) File.Delete(file);
            File.Move(temp, file);
        }

        /// <summary>Loads a record, or returns an empty one if it's missing or damaged.</summary>
        public static SyncRecord Load(string file)
        {
            var record = new SyncRecord();
            if (!File.Exists(file)) return record;
            try
            {
                using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var zip = new GZipStream(stream, CompressionMode.Decompress))
                using (var r = new BinaryReader(zip, Encoding.UTF8))
                {
                    if (r.ReadString() != Magic) return new SyncRecord();
                    record.LeftRoot = r.ReadString();
                    record.RightRoot = r.ReadString();
                    var count = r.ReadInt32();
                    for (var i = 0; i < count; i++)
                    {
                        var e = new RecordEntry
                        {
                            Path = r.ReadString(),
                            IsDir = r.ReadBoolean(),
                            Size = r.ReadInt64(),
                            LeftMtime = r.ReadInt64(),
                            RightMtime = r.ReadInt64(),
                            LeftId = r.ReadInt64(),
                            RightId = r.ReadInt64()
                        };
                        record.Items[e.Path] = e;
                    }
                }
                return record;
            }
            catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is EndOfStreamException || ex is FormatException)
            {
                return new SyncRecord();
            }
        }
    }
}
