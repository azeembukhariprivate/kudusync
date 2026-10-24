using System;
using Microsoft.Win32;

namespace Kudusync.Core
{
    public enum ThemeMode { System, Light, Dark }

    /// <summary>Settings are stored under HKEY_CURRENT_USER, so no administrator rights are needed.</summary>
    public static class Settings
    {
        private const string KeyPath = @"Software\KUDUSYNC";

        private static string Get(string name, string fallback)
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(KeyPath))
                {
                    return key?.GetValue(name) as string ?? fallback;
                }
            }
            catch
            {
                return fallback;
            }
        }

        private static void Set(string name, string value)
        {
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(KeyPath))
                {
                    key?.SetValue(name, value ?? string.Empty);
                }
            }
            catch
            {
                // Settings are best-effort.
            }
        }

        private static bool GetBool(string name, bool fallback) => Get(name, fallback ? "1" : "0") == "1";
        private static void SetBool(string name, bool value) => Set(name, value ? "1" : "0");

        private static T GetEnum<T>(string name, T fallback) where T : struct =>
            Enum.TryParse(Get(name, fallback.ToString()), out T v) ? v : fallback;

        public static ThemeMode Theme
        {
            get => GetEnum("Theme", ThemeMode.System);
            set => Set("Theme", value.ToString());
        }

        public static bool CheckForUpdates
        {
            get => GetBool("CheckForUpdates", true);
            set => SetBool("CheckForUpdates", value);
        }

        public static string SkippedVersion
        {
            get => Get("SkippedVersion", string.Empty);
            set => Set("SkippedVersion", value);
        }

        /// <summary>The pair selected last time.</summary>
        public static string LastPair
        {
            get => Get("LastPair", string.Empty);
            set => Set("LastPair", value);
        }

        /// <summary>"Sync with KUDUSYNC" and "Back up this folder" on folders' right-click menu. Turned on by the installer when chosen.</summary>
        public static bool ShellMenu
        {
            get => GetBool("ShellMenu", false);
            set => SetBool("ShellMenu", value);
        }

        /// <summary>Run in the notification area when Windows starts (for live sync and drive notifications).</summary>
        public static bool TrayAtStartup
        {
            get => GetBool("TrayAtStartup", false);
            set => SetBool("TrayAtStartup", value);
        }

        public static NotifyMode Notify
        {
            get => GetEnum("Notify", NotifyMode.Always);
            set => Set("Notify", value.ToString());
        }

        /// <summary>Stop a scheduled or automatic sync that would delete more than half of the files on a side.</summary>
        public static bool PauseMassDeletes
        {
            get => GetBool("PauseMassDeletes", true);
            set => SetBool("PauseMassDeletes", value);
        }

        /// <summary>The program the scheduled tasks were last set up for (they're set up again if it moves).</summary>
        public static string ScheduledExe
        {
            get => Get("ScheduledExe", string.Empty);
            set => Set("ScheduledExe", value);
        }
    }

    /// <summary>When to show a notification after a scheduled or automatic sync.</summary>
    public enum NotifyMode { Always, Problems, Never }
}
