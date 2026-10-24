using System;
using Microsoft.Win32;

namespace Kudusync.Core
{
    /// <summary>
    /// "Sync with KUDUSYNC" and "Back up this folder with KUDUSYNC…" on the right-click menu of folders and drives in
    /// File Explorer, and starting the tray part with Windows. Both are for the current user only (HKEY_CURRENT_USER),
    /// so no administrator rights are needed. On Windows 11 the menu items are under "Show more options".
    /// </summary>
    public static class ShellIntegration
    {
        private static readonly string[] Targets = { @"Software\Classes\Directory\shell", @"Software\Classes\Drive\shell" };
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        public static void Register()
        {
            try
            {
                foreach (var target in Targets)
                {
                    Verb(target + @"\KUDUSYNC.Sync", "Sync with KUDUSYNC", "--sync-folder");
                    Verb(target + @"\KUDUSYNC.Backup", "Back up this folder with KUDUSYNC…", "--new-pair");
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException || ex is System.Security.SecurityException) { }
        }

        private static void Verb(string path, string text, string option)
        {
            using (var key = Registry.CurrentUser.CreateSubKey(path))
            {
                key.SetValue(string.Empty, text);
                key.SetValue("Icon", "\"" + AppInfo.ExePath + "\",0");
                using (var command = key.CreateSubKey("command"))
                    command.SetValue(string.Empty, $"\"{AppInfo.ExePath}\" {option} \"%1\"");
            }
        }

        public static void Remove()
        {
            foreach (var target in Targets)
            {
                try
                {
                    Registry.CurrentUser.DeleteSubKeyTree(target + @"\KUDUSYNC.Sync", false);
                    Registry.CurrentUser.DeleteSubKeyTree(target + @"\KUDUSYNC.Backup", false);
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException || ex is System.Security.SecurityException) { }
            }
        }

        public static bool IsRegistered
        {
            get
            {
                try { using (var key = Registry.CurrentUser.OpenSubKey(Targets[0] + @"\KUDUSYNC.Sync\command")) return key != null; }
                catch { return false; }
            }
        }

        /// <summary>The command the menu runs, for checking.</summary>
        public static string CommandFor(string verb, bool drive = false)
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(Targets[drive ? 1 : 0] + @"\KUDUSYNC." + verb + @"\command"))
                    return key?.GetValue(string.Empty) as string;
            }
            catch
            {
                return null;
            }
        }

        // ------------------------------------------------------------------ starting with Windows

        public static bool StartsWithWindows
        {
            get
            {
                try { using (var key = Registry.CurrentUser.OpenSubKey(RunKey)) return key?.GetValue("KUDUSYNC") != null; }
                catch { return false; }
            }
        }

        public static void SetStartWithWindows(bool on)
        {
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (on) key.SetValue("KUDUSYNC", $"\"{AppInfo.ExePath}\" --tray");
                    else if (key.GetValue("KUDUSYNC") != null) key.DeleteValue("KUDUSYNC", false);
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException || ex is System.Security.SecurityException) { }
        }

        /// <summary>Keeps the menu and the start-up entry in step with the settings, and pointing at this copy of KUDUSYNC.</summary>
        public static void Refresh()
        {
            if (Settings.ShellMenu) Register();
            else if (IsRegistered) Remove();
            if (Settings.TrayAtStartup) SetStartWithWindows(true);
            else if (StartsWithWindows) SetStartWithWindows(false);
        }
    }
}
