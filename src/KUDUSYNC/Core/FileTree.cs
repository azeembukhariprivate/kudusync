using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace Kudusync.Core
{
    /// <summary>A file or folder inside one side of a pair.</summary>
    public sealed class Entry
    {
        /// <summary>Relative path with its real capitalisation, "\"-separated.</summary>
        public string Path { get; set; }
        public bool IsDir { get; set; }
        public long Size { get; set; }
        /// <summary>Last modified, UTC ticks.</summary>
        public long Mtime { get; set; }
        /// <summary>Created, UTC ticks.</summary>
        public long Ctime { get; set; }
        public FileAttributes Attributes { get; set; }
        /// <summary>The file system's ID for the file (0 if unknown). On NTFS it stays the same through renames and moves.</summary>
        public long FileId { get; set; }
        /// <summary>For reparse points (links, cloud files): what kind it is.</summary>
        public uint ReparseTag { get; set; }

        public Entry Clone() => (Entry)MemberwiseClone();

        public override string ToString() => Path + (IsDir ? "\\" : $" ({Size} bytes)");
    }

    /// <summary>Everything found on one side of a pair.</summary>
    public sealed class Tree
    {
        public Dictionary<string, Entry> Items { get; } = new Dictionary<string, Entry>(Paths.Comparer);

        /// <summary>Folders that couldn't be read. Nothing inside them is touched, on either side.</summary>
        public List<string> Unreadable { get; } = new List<string>();
        public List<string> Problems { get; } = new List<string>();
        public int SkippedLinks { get; set; }
        /// <summary>Unfinished copies left over from an interrupted run; removed at the start of the next one.</summary>
        public List<string> LeftoverTemps { get; } = new List<string>();
        public bool StableFileIds { get; set; }

        public Entry Get(string path) => path != null && Items.TryGetValue(path, out var e) ? e : null;

        public int FileCount => Items.Values.Count(e => !e.IsDir);
        public long TotalBytes => Items.Values.Where(e => !e.IsDir).Sum(e => e.Size);

        public bool IsUnderUnreadable(string path) =>
            Unreadable.Any(u => u.Length == 0 || path.Equals(u, Paths.Comparison) || path.StartsWith(u + "\\", Paths.Comparison));

        public void Add(Entry entry) => Items[entry.Path] = entry;

        public void Remove(string path) => Items.Remove(path);
    }

    /// <summary>Decides which files and folders a pair includes.</summary>
    public sealed class FileFilter
    {
        private readonly Regex[] _include;
        private readonly Regex[] _exclude;
        private readonly Regex[] _excludeFolderNames;
        private readonly string[] _excludeFolderPaths;
        private readonly bool _skipHidden;
        private readonly bool _skipSystem;
        private readonly HashSet<string> _alwaysSkip = new HashSet<string>(Paths.Comparer);

        public FileFilter(Filters filters, IEnumerable<string> alwaysSkipFolders = null)
        {
            filters = filters ?? new Filters();
            _include = Patterns(filters.Include);
            if (_include.Length == 0) _include = new[] { Wildcard("*") };
            _exclude = Patterns(filters.Exclude);
            var folders = Split(filters.ExcludeFolders).Select(f => f.Trim('\\', '/').Replace('/', '\\')).Where(f => f.Length > 0).ToList();
            _excludeFolderNames = folders.Where(f => !f.Contains('\\')).Select(Wildcard).ToArray();
            _excludeFolderPaths = folders.Where(f => f.Contains('\\')).ToArray();
            _skipHidden = filters.SkipHidden;
            _skipSystem = filters.SkipSystem;
            _alwaysSkip.Add(Paths.HistoryName);
            _alwaysSkip.Add("$RECYCLE.BIN");
            _alwaysSkip.Add("System Volume Information");
            if (alwaysSkipFolders != null)
                foreach (var f in alwaysSkipFolders) _alwaysSkip.Add(f);
        }

        public bool IncludeFolder(string path, string name, FileAttributes attributes)
        {
            if (_alwaysSkip.Contains(path)) return false;
            if (_skipHidden && (attributes & FileAttributes.Hidden) != 0) return false;
            if (_skipSystem && (attributes & FileAttributes.System) != 0 && (attributes & FileAttributes.Hidden) != 0) return false;
            if (_excludeFolderNames.Any(r => r.IsMatch(name))) return false;
            if (_excludeFolderPaths.Any(p => path.Equals(p, Paths.Comparison) || Wildcard(p).IsMatch(path))) return false;
            return true;
        }

        public bool IncludeFile(string path, string name, FileAttributes attributes)
        {
            if (name.EndsWith(Paths.TempSuffix, Paths.Comparison)) return false;
            if (_skipHidden && (attributes & FileAttributes.Hidden) != 0) return false;
            if (_skipSystem && (attributes & FileAttributes.System) != 0) return false;
            if (!_include.Any(r => Matches(r, path, name))) return false;
            if (_exclude.Any(r => Matches(r, path, name))) return false;
            return true;
        }

        private static bool Matches(Regex pattern, string path, string name) =>
            pattern.ToString().Contains(@"\\") ? pattern.IsMatch(path) : pattern.IsMatch(name);

        public static List<string> Split(string text) =>
            (text ?? string.Empty).Split(new[] { ';', ',', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim().Trim('"')).Where(s => s.Length > 0).ToList();

        private static Regex[] Patterns(string text) => Split(text).Select(p => Wildcard(p.Replace('/', '\\'))).ToArray();

        /// <summary>"*.jpg" style pattern, ignoring case. "*" matches anything, "?" one character.</summary>
        public static Regex Wildcard(string pattern) =>
            new Regex("^" + Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline);
    }

    /// <summary>Lists everything in a folder, recursively.</summary>
    public static class Scanner
    {
        public static Tree Scan(string root, FileFilter filter, Action<int> progress = null, CancellationToken token = default, bool stableIds = false)
        {
            var tree = new Tree { StableFileIds = stableIds };
            var useWin32 = Paths.IsWindows;
            var stack = new Stack<string>();
            stack.Push(string.Empty);
            var count = 0;
            while (stack.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                var folder = stack.Pop();
                List<Entry> children;
                try
                {
                    children = useWin32 ? ListWin32(root, folder) : null;
                    if (useWin32 && children == null) useWin32 = false; // e.g. a network share that doesn't support it
                    if (children == null) children = ListManaged(root, folder);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException)
                {
                    tree.Unreadable.Add(folder);
                    tree.Problems.Add((folder.Length == 0 ? "The folder itself" : folder) + ": " + ex.Message);
                    continue;
                }

                foreach (var e in children)
                {
                    var name = Paths.Name(e.Path);
                    if (e.IsDir)
                    {
                        if ((e.Attributes & FileAttributes.ReparsePoint) != 0 && IsLink(e))
                        {
                            tree.SkippedLinks++;
                            continue;
                        }
                        if (!filter.IncludeFolder(e.Path, name, e.Attributes)) continue;
                        tree.Add(e);
                        stack.Push(e.Path);
                    }
                    else
                    {
                        if (name.EndsWith(Paths.TempSuffix, Paths.Comparison))
                        {
                            tree.LeftoverTemps.Add(e.Path);
                            continue;
                        }
                        if ((e.Attributes & FileAttributes.ReparsePoint) != 0 && IsLink(e))
                        {
                            tree.SkippedLinks++;
                            continue;
                        }
                        if (!filter.IncludeFile(e.Path, name, e.Attributes)) continue;
                        tree.Add(e);
                    }
                    if (++count % 500 == 0) progress?.Invoke(count);
                }
            }
            progress?.Invoke(count);
            return tree;
        }

        // Directory symbolic links and junctions are skipped (they point elsewhere, and can loop). Other reparse points,
        // like OneDrive's online-only files, are ordinary files as far as syncing goes.
        private static bool IsLink(Entry e) => e.ReparseTag == Native.IO_REPARSE_TAG_SYMLINK || e.ReparseTag == Native.IO_REPARSE_TAG_MOUNT_POINT;

        private static List<Entry> ListManaged(string root, string folder)
        {
            var list = new List<Entry>();
            var dir = new DirectoryInfo(Paths.Full(root, folder));
            if (!dir.Exists) throw new DirectoryNotFoundException("Folder not found: " + Paths.Display(dir.FullName));
            foreach (var info in dir.EnumerateFileSystemInfos())
            {
                var isDir = (info.Attributes & FileAttributes.Directory) != 0;
                var e = new Entry
                {
                    Path = Paths.Join(folder, info.Name),
                    IsDir = isDir,
                    Size = isDir ? 0 : ((FileInfo)info).Length,
                    Mtime = info.LastWriteTimeUtc.Ticks,
                    Ctime = info.CreationTimeUtc.Ticks,
                    Attributes = info.Attributes
                };
                // Without the reparse tag, a folder that is a reparse point is treated as a link (the safe choice).
                if (isDir && (info.Attributes & FileAttributes.ReparsePoint) != 0) e.ReparseTag = Native.IO_REPARSE_TAG_SYMLINK;
                list.Add(e);
            }
            return list;
        }

        /// <summary>
        /// Lists a folder with GetFileInformationByHandleEx, which also gives each file's ID (to recognise renames)
        /// in the same pass. Returns null if the file system doesn't support it.
        /// </summary>
        private static List<Entry> ListWin32(string root, string folder)
        {
            var path = Paths.Full(root, folder);
            using (var handle = Native.CreateFile(path, Native.FILE_LIST_DIRECTORY, Native.FILE_SHARE_ALL, IntPtr.Zero,
                       Native.OPEN_EXISTING, Native.FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero))
            {
                if (handle.IsInvalid)
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error == 5) throw new UnauthorizedAccessException("Access is denied.");
                    if (error == Native.ERROR_FILE_NOT_FOUND || error == 3) throw new DirectoryNotFoundException("Folder not found: " + Paths.Display(path));
                    throw new IOException(new System.ComponentModel.Win32Exception(error).Message);
                }

                var list = new List<Entry>();
                var buffer = new byte[64 * 1024];
                while (true)
                {
                    if (!Native.GetFileInformationByHandleEx(handle, Native.FileIdBothDirectoryInfo, buffer, buffer.Length))
                    {
                        var error = Marshal.GetLastWin32Error();
                        if (error == Native.ERROR_NO_MORE_FILES) break;
                        if (error == 87 || error == 50 || error == 1 /* invalid parameter, not supported, invalid function */)
                            return null;
                        if (error == 5) throw new UnauthorizedAccessException("Access is denied.");
                        throw new IOException(new System.ComponentModel.Win32Exception(error).Message);
                    }

                    var offset = 0;
                    while (true)
                    {
                        var next = BitConverter.ToInt32(buffer, offset);
                        var nameLength = BitConverter.ToInt32(buffer, offset + 60);
                        var name = Encoding.Unicode.GetString(buffer, offset + 104, nameLength);
                        if (name != "." && name != "..")
                        {
                            var attributes = (FileAttributes)BitConverter.ToUInt32(buffer, offset + 56);
                            var isDir = (attributes & FileAttributes.Directory) != 0;
                            var e = new Entry
                            {
                                Path = Paths.Join(folder, name),
                                IsDir = isDir,
                                Ctime = FromFileTime(BitConverter.ToInt64(buffer, offset + 8)),
                                Mtime = FromFileTime(BitConverter.ToInt64(buffer, offset + 24)),
                                Size = isDir ? 0 : BitConverter.ToInt64(buffer, offset + 40),
                                Attributes = attributes,
                                FileId = BitConverter.ToInt64(buffer, offset + 96)
                            };
                            // For reparse points the EaSize field holds the reparse tag.
                            if ((attributes & FileAttributes.ReparsePoint) != 0) e.ReparseTag = BitConverter.ToUInt32(buffer, offset + 64);
                            list.Add(e);
                        }
                        if (next == 0) break;
                        offset += next;
                    }
                }
                return list;
            }
        }

        private static long FromFileTime(long fileTime)
        {
            try { return DateTime.FromFileTimeUtc(fileTime).Ticks; }
            catch { return 0; }
        }

        /// <summary>The current details of one file, or null if it isn't there.</summary>
        public static Entry Stat(string root, string relative)
        {
            var info = new FileInfo(Paths.Full(root, relative));
            if (!info.Exists) return null;
            return new Entry
            {
                Path = relative,
                Size = info.Length,
                Mtime = info.LastWriteTimeUtc.Ticks,
                Ctime = info.CreationTimeUtc.Ticks,
                Attributes = info.Attributes
            };
        }
    }
}
