using System;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Kudusync.Core
{
    /// <summary>The Windows functions KUDUSYNC calls directly.</summary>
    public static class Native
    {
        public const uint FILE_LIST_DIRECTORY = 0x0001;
        public const uint FILE_SHARE_ALL = 0x00000007; // read, write and delete
        public const uint OPEN_EXISTING = 3;
        public const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
        public const int FileIdBothDirectoryInfo = 10;
        public const int ERROR_NO_MORE_FILES = 18;
        public const int ERROR_FILE_NOT_FOUND = 2;

        public const int ERROR_SHARING_VIOLATION = 32;
        public const int ERROR_LOCK_VIOLATION = 33;
        public const int ERROR_HANDLE_DISK_FULL = 39;
        public const int ERROR_DISK_FULL = 112;

        public const uint IO_REPARSE_TAG_MOUNT_POINT = 0xA0000003;
        public const uint IO_REPARSE_TAG_SYMLINK = 0xA000000C;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int infoClass, byte[] buffer, int size);

        [StructLayout(LayoutKind.Sequential)]
        public struct BY_HANDLE_FILE_INFORMATION
        {
            public uint FileAttributes;
            public long CreationTime;
            public long LastAccessTime;
            public long LastWriteTime;
            public uint VolumeSerialNumber;
            public uint FileSizeHigh;
            public uint FileSizeLow;
            public uint NumberOfLinks;
            public uint FileIndexHigh;
            public uint FileIndexLow;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetFileInformationByHandle(SafeFileHandle handle, out BY_HANDLE_FILE_INFORMATION info);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetVolumeInformation(string root, StringBuilder name, int nameSize, out uint serial,
            out uint maxComponent, out uint flags, StringBuilder fileSystem, int fileSystemSize);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetDiskFreeSpaceEx(string directory, out ulong freeForUser, out ulong total, out ulong totalFree);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern int GetLongPathName(string shortPath, StringBuilder longPath, int size);

        // ---------------------------------------------------------------- network shares

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public class NETRESOURCE
        {
            public int dwScope;
            public int dwType = 1; // RESOURCETYPE_DISK
            public int dwDisplayType;
            public int dwUsage;
            public string lpLocalName;
            public string lpRemoteName;
            public string lpComment;
            public string lpProvider;
        }

        [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
        public static extern int WNetAddConnection2(NETRESOURCE resource, string password, string user, int flags);

        [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
        public static extern int WNetCancelConnection2(string name, int flags, [MarshalAs(UnmanagedType.Bool)] bool force);

        // ---------------------------------------------------------------- Credential Manager

        public const int CRED_TYPE_GENERIC = 1;
        public const int CRED_PERSIST_LOCAL_MACHINE = 2;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct CREDENTIAL
        {
            public int Flags;
            public int Type;
            public string TargetName;
            public string Comment;
            public long LastWritten;
            public int CredentialBlobSize;
            public IntPtr CredentialBlob;
            public int Persist;
            public int AttributeCount;
            public IntPtr Attributes;
            public string TargetAlias;
            public string UserName;
        }

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CredWrite(ref CREDENTIAL credential, int flags);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CredRead(string target, int type, int flags, out IntPtr credential);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CredDelete(string target, int type, int flags);

        [DllImport("advapi32.dll")]
        public static extern void CredFree(IntPtr buffer);
    }
}
