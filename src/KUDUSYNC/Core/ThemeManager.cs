using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Markup;
using Microsoft.Win32;

namespace Kudusync.Core
{
    /// <summary>
    /// Loads the colour themes and styles (embedded XAML files) and switches between light and dark.
    /// </summary>
    public static class ThemeManager
    {
        public static bool IsDark { get; private set; }

        public static ResourceDictionary LoadDictionary(string name)
        {
            var resource = "Kudusync.Themes." + name + ".xaml";
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource))
            {
                if (stream == null) throw new FileNotFoundException("Missing theme resource " + resource);
                return (ResourceDictionary)XamlReader.Load(stream);
            }
        }

        /// <summary>Sets up the application's resources: index 0 is the colour theme, index 1 the styles.</summary>
        public static void Init(ThemeMode mode)
        {
            var dictionaries = Application.Current.Resources.MergedDictionaries;
            dictionaries.Clear();
            dictionaries.Add(LoadDictionary("Light"));
            dictionaries.Add(LoadDictionary("Styles"));
            Apply(mode);
        }

        public static void Apply(ThemeMode mode)
        {
            IsDark = mode == ThemeMode.Dark || (mode == ThemeMode.System && SystemPrefersDark());
            var dictionaries = Application.Current.Resources.MergedDictionaries;
            var theme = LoadDictionary(IsDark ? "Dark" : "Light");
            if (dictionaries.Count > 0) dictionaries[0] = theme;
            else dictionaries.Add(theme);

            foreach (Window window in Application.Current.Windows) ApplyTitleBar(window);
        }

        /// <summary>Follow Windows when the user switches between light and dark mode.</summary>
        public static void WatchSystem()
        {
            SystemEvents.UserPreferenceChanged += (s, e) =>
            {
                if (e.Category != UserPreferenceCategory.General || Settings.Theme != ThemeMode.System) return;
                Application.Current?.Dispatcher.BeginInvoke(new Action(() => Apply(ThemeMode.System)));
            };
        }

        /// <summary>Call from a window's constructor so its title bar matches the theme.</summary>
        public static void Attach(Window window)
        {
            window.SourceInitialized += (s, e) => ApplyTitleBar(window);
        }

        public static bool SystemPrefersDark()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
                }
            }
            catch
            {
                return false;
            }
        }

        private static void ApplyTitleBar(Window window)
        {
            try
            {
                var handle = new WindowInteropHelper(window).Handle;
                if (handle == IntPtr.Zero) return;
                var dark = IsDark ? 1 : 0;
                // 20 = DWMWA_USE_IMMERSIVE_DARK_MODE (Windows 10 20H1+ and 11), 19 on older builds.
                if (DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int)) != 0)
                    DwmSetWindowAttribute(handle, 19, ref dark, sizeof(int));
            }
            catch
            {
                // Older Windows: keep the default title bar.
            }
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    }
}
