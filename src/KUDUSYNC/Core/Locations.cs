// Finding a pair's folders: a drive by its own serial number (so a USB drive still matches with another letter), a
// network folder, or a plain path.
using System;
using System.IO;
using System.Linq;
using System.Text;

namespace Kudusync.Core
{
    public sealed class VolumeInfo
    {
        /// <summary>The drive's top folder, like "E:\".</summary>
        public string Root { get; set; }        // "E:\"
        /// <summary>The drive's serial number, like "1A2B-3C4D".</summary>
        public string Serial { get; set; }      // "1A2B-3C4D"
        /// <summary>The drive's name.</summary>
        public string Label { get; set; }
        /// <summary>Its file system, like "NTFS" or "exFAT".</summary>
        public string FileSystem { get; set; }  // "NTFS", "exFAT", …
        /// <summary>What kind of drive (fixed, removable, network...).</summary>
        public DriveType Type { get; set; }

        /// <summary>NTFS and ReFS keep a file's ID when it's renamed, so renames can be recognised exactly.</summary>
        public bool StableFileIds => FileSystem == "NTFS" || FileSystem == "ReFS";

        /// <summary>FAT and exFAT store local time, so file times move by an hour when the clocks change.</summary>
        public bool StoresLocalTime => FileSystem != null &&
            (FileSystem.StartsWith("FAT", StringComparison.OrdinalIgnoreCase) || FileSystem.Equals("exFAT", StringComparison.OrdinalIgnoreCase));

        /// <summary>The drive in words: its name (or kind) and letter, like "Backup (E:)".</summary>
        public string Describe()
        {
            var name = string.IsNullOrEmpty(Label) ? KindName : Label;
            return $"{name} ({Root.TrimEnd('\\')})";
        }

        public string KindName
        {
            get
            {
                switch (Type)
                {
                    case DriveType.Removable: return Lang.T("USB drive");
                    case DriveType.Network: return Lang.T("Network drive");
                    case DriveType.CDRom: return Lang.T("Disc");
                    default: return Lang.T("Local disk");
                }
            }
        }

        /// <summary>The drive a path is on, or null.</summary>
        public static VolumeInfo Of(string path)
        {
            var drive = Paths.DriveOf(path);
            if (drive == null || !Paths.IsWindows) return null;
            var root = drive + "\\";
            try
            {
                var label = new StringBuilder(261);
                var fs = new StringBuilder(261);
                if (!Native.GetVolumeInformation(root, label, label.Capacity, out var serial, out _, out _, fs, fs.Capacity)) return null;
                return new VolumeInfo
                {
                    Root = root,
                    Serial = (serial >> 16).ToString("X4") + "-" + (serial & 0xFFFF).ToString("X4"),
                    Label = label.ToString(),
                    FileSystem = fs.ToString(),
                    Type = new DriveInfo(drive).DriveType
                };
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>Where a location is right now.</summary>
    public sealed class ResolvedLocation
    {
        /// <summary>The place as kept in the pair.</summary>
        public Location Location { get; set; }
        /// <summary>The folder's current path, e.g. "F:\Backup" when the drive that was E: is now F:.</summary>
        public string Root { get; set; }
        /// <summary>The drive it was found on.</summary>
        public VolumeInfo Volume { get; set; }
        /// <summary>Why it can't be used right now, or null.</summary>
        public string Problem { get; set; }
        /// <summary>The drive (or share) isn't there at all, as opposed to a folder that's missing or can't be opened.</summary>
        public bool NotConnected { get; set; }

        /// <summary>True when it was found.</summary>
        public bool Ok => Problem == null;
        /// <summary>True when it was found and its folder is there.</summary>
        public bool Exists => Ok && Directory.Exists(Paths.Long(Root));
    }

    public static class Locations
    {
        /// <summary>Describes a folder someone picked, remembering which drive it's on.</summary>
        public static Location FromPath(string path)
        {
            path = Paths.Normalize(path);
            var location = new Location { Path = path };
            if (Paths.IsUnc(path)) return location;
            var volume = VolumeInfo.Of(path);
            if (volume != null && volume.Type != DriveType.Network && volume.Type != DriveType.CDRom)
            {
                location.VolumeSerial = volume.Serial;
                location.VolumeLabel = volume.Label;
            }
            return location;
        }

        /// <summary>
        /// Finds where a location is now. A folder on a drive with a known serial number is looked for on whichever
        /// letter that drive has now, and never used on a different drive that happens to have the old letter.
        /// </summary>
        public static ResolvedLocation Resolve(Location location)
        {
            var result = new ResolvedLocation { Location = location, Root = location?.Path };
            if (location == null || string.IsNullOrWhiteSpace(location.Path))
            {
                result.Problem = "No folder chosen.";
                return result;
            }

            if (location.IsNetwork)
            {
                result.Root = Paths.Normalize(location.Path);
                return result;
            }

            var drive = Paths.DriveOf(location.Path);
            if (string.IsNullOrEmpty(location.VolumeSerial) || drive == null || !Paths.IsWindows)
            {
                result.Root = Paths.Normalize(location.Path);
                result.Volume = VolumeInfo.Of(result.Root);
                if (drive != null && Paths.IsWindows && result.Volume == null)
                {
                    result.Problem = $"Drive {drive} isn't connected.";
                    result.NotConnected = true;
                }
                return result;
            }

            var here = VolumeInfo.Of(drive);
            if (here != null && here.Serial == location.VolumeSerial)
            {
                result.Volume = here;
                result.Root = Paths.Normalize(location.Path);
                return result;
            }

            foreach (var d in SafeDrives())
            {
                var v = VolumeInfo.Of(d);
                if (v == null || v.Serial != location.VolumeSerial) continue;
                result.Volume = v;
                result.Root = v.Root.Substring(0, 2) + location.Path.Substring(2);
                return result;
            }

            var name = string.IsNullOrEmpty(location.VolumeLabel) ? "The drive" : $"\"{location.VolumeLabel}\"";
            result.Problem = here != null
                ? $"{name} (was {drive}) isn't connected. {drive} is now a different drive, so KUDUSYNC won't use it. If you formatted or replaced the drive, edit the pair and choose the folder again."
                : $"{name} ({drive}) isn't connected.";
            result.NotConnected = true;
            return result;
        }

        /// <summary>The drives that can be listed.</summary>
        private static string[] SafeDrives()
        {
            try { return DriveInfo.GetDrives().Where(d => d.DriveType != DriveType.Network && d.DriveType != DriveType.CDRom).Select(d => d.Name).ToArray(); }
            catch { return new string[0]; }
        }

        /// <summary>The drive's description for people: "SANDISK (E:) · USB drive", or the share.</summary>
        public static string Describe(Location location, ResolvedLocation resolved = null)
        {
            if (location == null || string.IsNullOrEmpty(location.Path)) return "Not chosen";
            if (location.IsNetwork) return "Network share " + Paths.ShareRoot(location.Path);
            var v = resolved?.Volume;
            if (v != null) return v.Describe() + " · " + v.KindName + (string.IsNullOrEmpty(v.FileSystem) ? string.Empty : " · " + v.FileSystem);
            return string.IsNullOrEmpty(location.VolumeLabel) ? "Drive " + Paths.DriveOf(location.Path) : location.VolumeLabel;
        }

        /// <summary>Free space on the drive or share holding the folder, or -1 if unknown.</summary>
        public static long FreeSpace(string folder)
        {
            if (!Paths.IsWindows)
            {
                try { return new DriveInfo(Path.GetPathRoot(folder) ?? "/").AvailableFreeSpace; }
                catch { return -1; }
            }
            try
            {
                var dir = folder.EndsWith("\\") ? folder : folder + "\\";
                return Native.GetDiskFreeSpaceEx(dir, out var free, out _, out _) ? (long)free : -1;
            }
            catch
            {
                return -1;
            }
        }

        /// <summary>Turns an old-style short path ("C:\PROGRA~1") into its full form, if it exists.</summary>
        public static string LongName(string path)
        {
            if (!Paths.IsWindows || string.IsNullOrEmpty(path) || !path.Contains("~")) return path;
            var sb = new StringBuilder(1024);
            var n = Native.GetLongPathName(path, sb, sb.Capacity);
            return n > 0 && n < sb.Capacity ? sb.ToString() : path;
        }
    }
}
