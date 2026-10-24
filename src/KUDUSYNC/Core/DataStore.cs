using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;

namespace Kudusync.Core
{
    /// <summary>
    /// Where KUDUSYNC keeps its pairs, records and logs: %LOCALAPPDATA%\KUDUSYNC, or a "Data" folder next to the program
    /// for the portable version (marked by a "KUDUSYNC.portable" file beside it).
    /// </summary>
    public static class DataStore
    {
        public const string PortableMarker = "KUDUSYNC.portable";

        private static string _folder;

        /// <summary>Used by the tests and the self-test to work in a scratch folder.</summary>
        public static string OverrideFolder { get; set; }

        public static string Folder
        {
            get
            {
                if (!string.IsNullOrEmpty(OverrideFolder)) return OverrideFolder;
                if (_folder != null) return _folder;
                var exeDir = Path.GetDirectoryName(AppInfo.ExePath) ?? string.Empty;
                _folder = File.Exists(Path.Combine(exeDir, PortableMarker))
                    ? Path.Combine(exeDir, "Data")
                    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KUDUSYNC");
                return _folder;
            }
        }

        public static bool IsPortable => string.IsNullOrEmpty(OverrideFolder) && File.Exists(Path.Combine(Path.GetDirectoryName(AppInfo.ExePath) ?? string.Empty, PortableMarker));

        public static string PairsFile => Path.Combine(Folder, "pairs.json");
        public static string RunsFile => Path.Combine(Folder, "runs.json");
        public static string RecordsFolder => Path.Combine(Folder, "Pairs");
        public static string LogsFolder => Path.Combine(Folder, "Logs");

        public static string RecordFile(string pairId) => Path.Combine(RecordsFolder, Paths.SafeName(pairId) + ".db");

        public static List<FolderPair> LoadPairs()
        {
            using (Lock("pairs"))
            {
                var list = ReadJson<List<FolderPair>>(PairsFile) ?? new List<FolderPair>();
                return list.Where(p => p != null && !string.IsNullOrEmpty(p.Id)).ToList();
            }
        }

        public static void SavePairs(IEnumerable<FolderPair> pairs)
        {
            using (Lock("pairs")) WriteJson(PairsFile, pairs.ToList());
        }

        public static void DeleteRecord(string pairId)
        {
            try { File.Delete(RecordFile(pairId)); }
            catch (IOException) { }
        }

        // ------------------------------------------------------------------ helpers

        /// <summary>A lock shared by every KUDUSYNC process of this user (the app, scheduled runs and the tray).</summary>
        public static IDisposable Lock(string name, int timeoutMs = 10000) => new NamedLock("KUDUSYNC-" + name + "-" + Hash(Folder), timeoutMs);

        private static string Hash(string text)
        {
            unchecked
            {
                var h = 23;
                foreach (var c in text.ToLowerInvariant()) h = h * 31 + c;
                return ((uint)h).ToString("x8");
            }
        }

        public static T ReadJson<T>(string file) where T : class
        {
            if (!File.Exists(file)) return null;
            try
            {
                using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    return (T)Serializer<T>().ReadObject(stream);
            }
            catch (Exception ex) when (ex is SerializationException || ex is IOException || ex is InvalidCastException || ex is ArgumentException)
            {
                // A damaged file: keep a copy for reference and start again.
                try { File.Copy(file, file + ".damaged", true); }
                catch { /* ignore */ }
                return null;
            }
        }

        public static void WriteJson<T>(string file, T value)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            var temp = file + ".saving";
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write))
            using (var writer = JsonReaderWriterFactory.CreateJsonWriter(stream, Encoding.UTF8, false, true, "  "))
            {
                Serializer<T>().WriteObject(writer, value);
                writer.Flush();
            }
            if (File.Exists(file)) File.Replace(temp, file, null);
            else File.Move(temp, file);
        }

        private static DataContractJsonSerializer Serializer<T>() =>
            new DataContractJsonSerializer(typeof(T), new DataContractJsonSerializerSettings { DateTimeFormat = new DateTimeFormat("o"), UseSimpleDictionaryFormat = true });

        private sealed class NamedLock : IDisposable
        {
            private readonly Mutex _mutex;
            private readonly bool _owned;

            public NamedLock(string name, int timeoutMs)
            {
                try
                {
                    _mutex = new Mutex(false, name);
                    try { _owned = _mutex.WaitOne(timeoutMs); }
                    catch (AbandonedMutexException) { _owned = true; }
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException || ex is WaitHandleCannotBeOpenedException)
                {
                    _mutex = null;
                }
            }

            public void Dispose()
            {
                if (_mutex == null) return;
                if (_owned) _mutex.ReleaseMutex();
                _mutex.Dispose();
            }
        }
    }
}
