using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;

namespace Kudusync.Core
{
    public sealed class RunProgress
    {
        public string Path { get; set; }
        public int Done { get; set; }
        public int Total { get; set; }
        public long BytesDone { get; set; }
        public long BytesTotal { get; set; }
        public int Failed { get; set; }
    }

    /// <summary>
    /// Carries out a plan. Copies go to a ".kudusync-tmp" file first and get their real name only when complete, so an
    /// interrupted run never leaves a half-written file under the real name. Replaced and deleted files go to the
    /// history folder. As each step succeeds, the in-memory trees are updated so the record can be rebuilt afterwards.
    /// </summary>
    public sealed class Executor
    {
        private const int BufferSize = 1024 * 1024;
        private const FileOptions WriteThrough = (FileOptions)unchecked((int)0x80000000);

        private readonly FolderPair _pair;
        private readonly string _leftRoot;
        private readonly string _rightRoot;
        private readonly Tree _left;
        private readonly Tree _right;
        private readonly History _history;
        private readonly byte[] _buffer = new byte[BufferSize];
        private readonly HashSet<Side> _full = new HashSet<Side>();
        private readonly HashSet<string> _emptiedSourceFolders = new HashSet<string>(Paths.Comparer);
        private RunProgress _progress;
        private DateTime _lastReport;

        public Executor(FolderPair pair, string leftRoot, string rightRoot, Tree left, Tree right, DateTime started)
        {
            _pair = pair;
            _leftRoot = leftRoot;
            _rightRoot = rightRoot;
            _left = left;
            _right = right;
            _history = new History(pair, leftRoot, rightRoot, started);
        }

        public event Action<RunProgress> Progress;

        public List<string> Log { get; } = new List<string>();

        public History History => _history;

        private string Root(Side side) => side == Side.Left ? _leftRoot : _rightRoot;
        private Tree TreeOf(Side side) => side == Side.Left ? _left : _right;
        private string SideTitle(Side side) => side == Side.Left ? _pair.LeftTitle : _pair.RightTitle;

        /// <summary>Runs the plan's ticked actions. Returns false if it was stopped.</summary>
        public bool Run(Plan plan, CancellationToken token)
        {
            RemoveLeftovers();
            var actions = plan.Active.ToList();
            _progress = new RunProgress { Total = actions.Count, BytesTotal = actions.Where(a => a.IsCopy).Sum(a => a.Size) };
            foreach (var skipped in plan.Actions.Where(a => !a.Enabled)) skipped.State = ActionState.Skipped;

            try
            {
                foreach (var action in actions)
                {
                    token.ThrowIfCancellationRequested();
                    _progress.Path = action.Path;
                    Report(true);
                    var bytesBefore = _progress.BytesDone;
                    try
                    {
                        Do(action, token);
                        if (action.State == ActionState.Pending) action.State = ActionState.Done;
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException ||
                                               ex is CryptographicException || ex is NotSupportedException || ex is ArgumentException)
                    {
                        action.State = ActionState.Failed;
                        action.Error = Explain(ex, action);
                        _progress.Failed++;
                        _progress.BytesDone = bytesBefore + (action.IsCopy ? action.Size : 0);
                    }
                    WriteLog(action);
                    _progress.Done++;
                    Report(false);
                }
                if (plan.CleanSourceFolders) CleanSourceFolders();
                return true;
            }
            catch (OperationCanceledException)
            {
                Log.Add("Stopped.");
                return false;
            }
        }

        private void Report(bool force)
        {
            var now = DateTime.UtcNow;
            if (!force && (now - _lastReport).TotalMilliseconds < 100) return;
            _lastReport = now;
            Progress?.Invoke(_progress);
        }

        private void RemoveLeftovers()
        {
            foreach (var side in new[] { Side.Left, Side.Right })
                foreach (var temp in TreeOf(side).LeftoverTemps)
                {
                    try { History.DeleteFile(Paths.Full(Root(side), temp)); }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
                }
        }

        private void Do(SyncAction a, CancellationToken token)
        {
            switch (a.Kind)
            {
                case ActionKind.CreateFolder:
                    EnsureFolder(a.Target, a.Path);
                    break;

                case ActionKind.DeleteFolder:
                    var folder = Paths.Full(Root(a.Target), a.Path);
                    if (!Directory.Exists(folder))
                    {
                        TreeOf(a.Target).Remove(a.Path);
                        break;
                    }
                    if (Directory.EnumerateFileSystemEntries(folder).Any())
                    {
                        // Something is still in it (an excluded file, or a file that couldn't be removed): leave it.
                        a.State = ActionState.Skipped;
                        a.Error = "Not empty, so it was left in place";
                        break;
                    }
                    Directory.Delete(folder, false);
                    TreeOf(a.Target).Remove(a.Path);
                    break;

                case ActionKind.Delete:
                    var path = Paths.Full(Root(a.Target), a.Path);
                    if (File.Exists(path))
                    {
                        if (a.Permanent) History.DeleteFile(path);
                        else _history.Keep(a.Target, Root(a.Target), a.Path);
                    }
                    TreeOf(a.Target).Remove(a.Path);
                    if (a.Permanent) NoteRemovedFromSource(a.Path);
                    break;

                case ActionKind.Rename:
                    Rename(a);
                    break;

                default:
                    Copy(a, token);
                    break;
            }
        }

        private void Rename(SyncAction a)
        {
            var root = Root(a.Target);
            var from = Paths.Full(root, a.From);
            var to = Paths.Full(root, a.Path);
            if (!File.Exists(from)) throw new FileNotFoundException("It's no longer there.");
            EnsureFolder(a.Target, Paths.Parent(a.Path));
            if (string.Equals(a.From, a.Path, StringComparison.OrdinalIgnoreCase))
            {
                // Only the capitalisation changes: go through a temporary name.
                var temp = from + Paths.TempSuffix;
                File.Move(from, temp);
                File.Move(temp, to);
            }
            else
            {
                if (File.Exists(to)) throw new IOException("A file called \"" + Paths.Name(a.Path) + "\" is already there.");
                File.Move(from, to);
            }
            var tree = TreeOf(a.Target);
            var old = tree.Get(a.From);
            tree.Remove(a.From);
            var moved = old?.Clone() ?? Scanner.Stat(root, a.Path) ?? new Entry();
            moved.Path = a.Path;
            tree.Add(moved);
        }

        private void Copy(SyncAction a, CancellationToken token)
        {
            if (_full.Contains(a.Target)) throw new IOException("Not enough free space in " + SideTitle(a.Target) + ".");

            var sourceSide = a.SourceSide;
            var sourceRoot = Root(sourceSide);
            var targetRoot = Root(a.Target);
            var source = Paths.Full(sourceRoot, a.SourcePath);
            var target = Paths.Full(targetRoot, a.Path);
            var temp = target + Paths.TempSuffix;

            var before = Scanner.Stat(sourceRoot, a.SourcePath) ?? throw new FileNotFoundException("It's no longer there.");
            EnsureFolder(a.Target, Paths.Parent(a.Path));

            long fileId = 0;
            byte[] sourceHash = null;
            var verify = _pair.Verify;
            var start = _progress.BytesDone;
            try
            {
                using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan))
                using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, verify ? WriteThrough : FileOptions.None))
                using (var md5 = verify ? MD5.Create() : null)
                {
                    if (input.Length > 0) output.SetLength(input.Length);
                    int n;
                    long total = 0;
                    while ((n = input.Read(_buffer, 0, _buffer.Length)) > 0)
                    {
                        token.ThrowIfCancellationRequested();
                        output.Write(_buffer, 0, n);
                        md5?.TransformBlock(_buffer, 0, n, null, 0);
                        total += n;
                        _progress.BytesDone = start + total;
                        Report(false);
                    }
                    if (output.Length != total) output.SetLength(total);
                    output.Flush(verify);
                    if (md5 != null)
                    {
                        md5.TransformFinalBlock(_buffer, 0, 0);
                        sourceHash = md5.Hash;
                    }
                    fileId = FileIdOf(output);
                }

                var after = Scanner.Stat(sourceRoot, a.SourcePath);
                if (after == null || after.Size != before.Size || after.Mtime != before.Mtime)
                    throw new IOException("It changed while it was being copied. It'll be copied next time.");

                File.SetCreationTimeUtc(temp, new DateTime(before.Ctime, DateTimeKind.Utc));
                File.SetLastWriteTimeUtc(temp, new DateTime(before.Mtime, DateTimeKind.Utc));

                if (verify && !Checksum(temp, token).SequenceEqual(sourceHash))
                    throw new IOException("The copy didn't match the original when it was read back (the drive may be failing). It'll be tried again next time.");

                if (File.Exists(target)) _history.Keep(a.Target, targetRoot, a.Path);
                File.Move(temp, target);
                var keep = before.Attributes & (FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System | FileAttributes.Archive | FileAttributes.NotContentIndexed);
                if (keep != 0 && Paths.IsWindows) File.SetAttributes(target, keep);
            }
            catch (Exception ex)
            {
                try { if (File.Exists(temp)) File.Delete(temp); }
                catch { /* best effort */ }
                if (ex is IOException io && IsDiskFull(io)) _full.Add(a.Target);
                throw;
            }

            var copied = Scanner.Stat(targetRoot, a.Path) ?? before.Clone();
            copied.Path = a.Path;
            copied.FileId = fileId;
            TreeOf(a.Target).Remove(a.Path);
            TreeOf(a.Target).Add(copied);
            var sourceEntry = before.Clone();
            sourceEntry.Path = a.SourcePath;
            sourceEntry.FileId = TreeOf(sourceSide).Get(a.SourcePath)?.FileId ?? 0;
            TreeOf(sourceSide).Remove(a.SourcePath);
            TreeOf(sourceSide).Add(sourceEntry);

            if (a.RemoveSource)
            {
                try
                {
                    History.DeleteFile(source);
                    TreeOf(sourceSide).Remove(a.SourcePath);
                    NoteRemovedFromSource(a.SourcePath);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    a.Error = "Copied, but couldn't be removed from the source: " + Explain(ex, a);
                }
            }
        }

        private byte[] Checksum(string path, CancellationToken token)
        {
            using (var md5 = MD5.Create())
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan))
            {
                int n;
                while ((n = stream.Read(_buffer, 0, _buffer.Length)) > 0)
                {
                    token.ThrowIfCancellationRequested();
                    md5.TransformBlock(_buffer, 0, n, null, 0);
                }
                md5.TransformFinalBlock(_buffer, 0, 0);
                return md5.Hash;
            }
        }

        private static long FileIdOf(FileStream stream)
        {
            if (!Paths.IsWindows) return 0;
            try
            {
                return Native.GetFileInformationByHandle(stream.SafeFileHandle, out var info)
                    ? ((long)info.FileIndexHigh << 32) | info.FileIndexLow
                    : 0;
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>Creates a folder (and any missing parents) on a side, adding them to that side's tree.</summary>
        private void EnsureFolder(Side side, string relative)
        {
            if (string.IsNullOrEmpty(relative)) return;
            var tree = TreeOf(side);
            if (tree.Get(relative)?.IsDir == true && Directory.Exists(Paths.Full(Root(side), relative))) return;
            EnsureFolder(side, Paths.Parent(relative));
            var full = Paths.Full(Root(side), relative);
            if (File.Exists(full)) throw new IOException($"There's a file called \"{Paths.Name(relative)}\" where a folder is needed.");
            Directory.CreateDirectory(full);
            if (tree.Get(relative) == null) tree.Add(new Entry { Path = relative, IsDir = true, Attributes = FileAttributes.Directory });
        }

        private void NoteRemovedFromSource(string relative)
        {
            for (var p = Paths.Parent(relative); p.Length > 0; p = Paths.Parent(p)) _emptiedSourceFolders.Add(p);
        }

        /// <summary>Move mode: folders in the source that were emptied by this run are removed (the source's top folder stays).</summary>
        private void CleanSourceFolders()
        {
            foreach (var folder in _emptiedSourceFolders.OrderByDescending(Paths.Depth))
            {
                var full = Paths.Full(_leftRoot, folder);
                try
                {
                    if (!Directory.Exists(full) || Directory.EnumerateFileSystemEntries(full).Any()) continue;
                    Directory.Delete(full, false);
                    _left.Remove(folder);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
            }
        }

        public static bool IsDiskFull(IOException ex)
        {
            var code = Marshal.GetHRForException(ex) & 0xFFFF;
            return code == Native.ERROR_DISK_FULL || code == Native.ERROR_HANDLE_DISK_FULL || ex.Message.IndexOf("not enough space", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static bool IsInUse(Exception ex)
        {
            if (!(ex is IOException)) return false;
            var code = Marshal.GetHRForException(ex) & 0xFFFF;
            return code == Native.ERROR_SHARING_VIOLATION || code == Native.ERROR_LOCK_VIOLATION;
        }

        private string Explain(Exception ex, SyncAction a)
        {
            if (IsInUse(ex)) return "In use by another program. It'll be tried again next time.";
            if (ex is UnauthorizedAccessException) return "Access denied (it may be read-only, or need permissions you don't have).";
            if (ex is IOException io && IsDiskFull(io)) return "Not enough free space in " + SideTitle(a.Target) + ".";
            if (ex is FileNotFoundException || ex is DirectoryNotFoundException) return "It's no longer there.";
            if (ex is PathTooLongException) return "The path is too long for that drive.";
            return ex.Message;
        }

        private void WriteLog(SyncAction a)
        {
            string what;
            switch (a.Kind)
            {
                case ActionKind.Copy: what = "Copied"; break;
                case ActionKind.Update: what = "Updated"; break;
                case ActionKind.Delete: what = a.Permanent ? "Removed from the source" : _history.Enabled ? "Deleted (kept as an old version)" : "Deleted"; break;
                case ActionKind.Rename: what = "Renamed"; break;
                case ActionKind.CreateFolder: what = "Created folder"; break;
                default: what = "Removed folder"; break;
            }
            var where = a.Kind == ActionKind.Rename ? $"{a.From} → {a.Path}" : a.Path;
            var line = a.State == ActionState.Failed
                ? $"FAILED  {what.ToLowerInvariant()} in {SideTitle(a.Target)}: {where} — {a.Error}"
                : a.State == ActionState.Skipped
                    ? $"Left    {where} in {SideTitle(a.Target)} — {a.Error}"
                    : $"{what} in {SideTitle(a.Target)}: {where}" + (a.Error != null ? " — " + a.Error : string.Empty);
            Log.Add(line);
        }
    }
}
