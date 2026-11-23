// Shared KUDU code: this file comes from kudu-common (https://github.com/azeembukhariprivate/kudu-common).
// Change it there and run its sync script; the build fails when this copy differs from kudu-common.
// The app's dialogs: messages, questions, choosing files and folders, and windows of its own built the same way every
// time.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Kudusync.Controls;
using Kudusync.Core;

namespace Kudusync.Views
{
    public static class Dialogs
    {
        /// <summary>A message with an OK button.</summary>
        public static void Info(Window owner, string message, string title = AppInfo.Name) =>
            Show(owner, message, title, MessageBoxButton.OK, MessageBoxImage.Information);

        /// <summary>A warning with an OK button.</summary>
        public static void Error(Window owner, string message, string title = AppInfo.Name) =>
            Show(owner, message, title, MessageBoxButton.OK, MessageBoxImage.Warning);

        /// <summary>A yes / no question. True when the answer is yes.</summary>
        public static bool Confirm(Window owner, string message, string title = AppInfo.Name) =>
            Show(owner, message, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

        /// <summary>
        /// Shows a message box over the window, in the chosen language (during the self-test, it's only written to the
        /// log).
        /// </summary>
        private static MessageBoxResult Show(Window owner, string message, string title, MessageBoxButton buttons, MessageBoxImage image)
        {
            if (SelfTest.Running)
            {
                // Never block the automated self-test with a message box.
                if (image == MessageBoxImage.Warning) SelfTest.Errors.Add("Error message shown: " + message);
                return buttons == MessageBoxButton.OK ? MessageBoxResult.OK : MessageBoxResult.No;
            }
            message = Lang.T(message);
            title = Lang.T(title);
            var options = Lang.RightToLeft ? MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign : MessageBoxOptions.None;
            return owner != null && owner.IsVisible
                ? MessageBox.Show(owner, message, title, buttons, image, MessageBoxResult.None, options)
                : MessageBox.Show(message, title, buttons, image, MessageBoxResult.None, options);
        }

        /// <summary>Asks for a folder. Returns its path, or null when nothing was chosen.</summary>
        public static string PickFolder(Window owner, string description, string start = null)
        {
            using (var dialog = new System.Windows.Forms.FolderBrowserDialog
                   {
                       Description = Lang.T(description),
                       ShowNewFolderButton = true,
                       SelectedPath = start != null && System.IO.Directory.Exists(start) ? start : string.Empty
                   })
            {
                return dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK ? dialog.SelectedPath : null;
            }
        }

        /// <summary>Asks for one or more files. Returns their paths (empty when nothing was chosen).</summary>
        public static List<string> PickFiles(Window owner, string title)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Title = Lang.T(title), Filter = Filter("All files|*.*"), Multiselect = true };
            return Show(dialog, owner) ? dialog.FileNames.ToList() : new List<string>();
        }

        /// <summary>Asks for one file, of the kinds in `filter`. Returns its path, or null.</summary>
        public static string PickFile(Window owner, string title, string filter)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Title = Lang.T(title), Filter = Filter(filter), CheckFileExists = true };
            return Show(dialog, owner) ? dialog.FileName : null;
        }

        /// <summary>Asks where to save a file. Returns the path, or null.</summary>
        public static string SaveFile(Window owner, string title, string fileName, string filter)
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = Lang.T(title),
                FileName = fileName,
                Filter = Filter(filter),
                OverwritePrompt = true,
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
            };
            return Show(dialog, owner) ? dialog.FileName : null;
        }

        /// <summary>The names in a file dialog's list of kinds ("Text files|*.txt|All files|*.*") in the language in use.</summary>
        private static string Filter(string filter)
        {
            if (Lang.IsEnglish || string.IsNullOrEmpty(filter)) return filter;
            var parts = filter.Split('|');
            for (var i = 0; i < parts.Length; i += 2) parts[i] = Lang.T(parts[i]);
            return string.Join("|", parts);
        }

        /// <summary>
        /// Shows a file dialog over the window when it's open, on its own otherwise. True when something was chosen.
        /// </summary>
        private static bool Show(Microsoft.Win32.CommonDialog dialog, Window owner) =>
            (owner != null && owner.IsVisible ? dialog.ShowDialog(owner) : dialog.ShowDialog()) == true;

        /// <summary>The app's icon, read once.</summary>
        private static ImageSource _icon;

        public static ImageSource AppIcon
        {
            get
            {
                if (_icon != null) return _icon;
                try
                {
                    using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Kudusync.Assets." + AppInfo.Name + ".ico"))
                    {
                        var decoder = new IconBitmapDecoder(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                        _icon = decoder.Frames.OrderByDescending(f => f.PixelWidth).First();
                    }
                }
                catch
                {
                    _icon = null;
                }
                return _icon;
            }
        }

        /// <summary>The app's icon file, as it is kept inside the app.</summary>
        public static Stream AppIconStream() =>
            Assembly.GetExecutingAssembly().GetManifestResourceStream("Kudusync.Assets." + AppInfo.Name + ".ico");

        /// <summary>Common window setup: theme colours, font, icon and title bar.</summary>
        public static void Prepare(Window window, string title, Window owner = null)
        {
            window.Title = title;
            window.Res(Control.BackgroundProperty, "WindowBg");
            window.Res(Control.ForegroundProperty, "Text");
            window.Res(Control.FontFamilyProperty, "UiFont");
            window.FontSize = 13;
            window.Icon = AppIcon;
            window.UseLayoutRounding = true;
            TextOptions.SetTextFormattingMode(window, TextFormattingMode.Display);
            ThemeManager.Attach(window);
            if (owner != null && owner.IsVisible)
            {
                window.Owner = owner;
                window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            }
            else
            {
                window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            }
            FitToScreen(window);
        }

        /// <summary>
        /// Keeps a window on its screen: never bigger than the screen (less the taskbar), and never with its title bar
        /// above the top, where it couldn't be moved or closed. On a small or low-resolution screen a big window would
        /// otherwise open partly off the screen.
        /// </summary>
        public static void FitToScreen(Window window)
        {
            var primary = SystemParameters.WorkArea;
            if (!double.IsNaN(window.Width) && window.Width > primary.Width) window.Width = Math.Max(window.MinWidth, primary.Width);
            if (!double.IsNaN(window.Height) && window.Height > primary.Height) window.Height = Math.Max(window.MinHeight, primary.Height);
            window.Loaded += (s, e) =>
            {
                // Windows the self-test puts out of sight on purpose stay there.
                if (window.WindowState != WindowState.Normal || window.Left < -10000) return;
                var area = ScreenArea(window) ?? primary;
                if (window.ActualWidth > area.Width) window.Width = Math.Max(window.MinWidth, area.Width);
                if (window.ActualHeight > area.Height) window.Height = Math.Max(window.MinHeight, area.Height);
                var w = double.IsNaN(window.Width) ? window.ActualWidth : window.Width;
                var h = double.IsNaN(window.Height) ? window.ActualHeight : window.Height;
                if (window.Left + w > area.Right) window.Left = area.Right - w;
                if (window.Top + h > area.Bottom) window.Top = area.Bottom - h;
                if (window.Left < area.Left) window.Left = area.Left;
                if (window.Top < area.Top) window.Top = area.Top;
            };
        }

        /// <summary>The work area (without the taskbar) of the screen the window is on, in WPF units.</summary>
        private static Rect? ScreenArea(Window window)
        {
            try
            {
                var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
                var source = PresentationSource.FromVisual(window);
                if (handle == IntPtr.Zero || source?.CompositionTarget == null) return null;
                var monitor = MonitorFromWindow(handle, 2 /* nearest */);
                var info = new MonitorInfo { Size = System.Runtime.InteropServices.Marshal.SizeOf(typeof(MonitorInfo)) };
                if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info)) return null;
                var m = source.CompositionTarget.TransformFromDevice;
                var topLeft = m.Transform(new Point(info.Work.Left, info.Work.Top));
                var bottomRight = m.Transform(new Point(info.Work.Right, info.Work.Bottom));
                return new Rect(topLeft, bottomRight);
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception || ex is InvalidOperationException || ex is DllNotFoundException || ex is EntryPointNotFoundException)
            {
                return null;
            }
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct Bounds
        {
            /// <summary>
            /// A rectangle on the screen, as Windows gives it (left, top, right and bottom edges in pixels).
            /// </summary>
            public int Left, Top, Right, Bottom;
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct MonitorInfo
        {
            /// <summary>The size of this structure, which Windows needs filled in.</summary>
            public int Size;
            /// <summary>The whole screen.</summary>
            public Bounds Monitor;
            /// <summary>The part of the screen not covered by the taskbar.</summary>
            public Bounds Work;
            /// <summary>Whether this is the main screen.</summary>
            public int Flags;
        }

        /// <summary>Windows' call that finds the screen a window is on.</summary>
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr handle, int flags);

        /// <summary>Windows' call that gives a screen's size and the part not covered by the taskbar.</summary>
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

        /// <summary>A fixed-size dialog with content and a right-aligned row of buttons.</summary>
        public static Window Dialog(Window owner, string title, UIElement content, double width, params Button[] buttons)
        {
            var window = new Window
            {
                Width = width,
                SizeToContent = SizeToContent.Height,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = owner == null
            };
            Prepare(window, title, owner);

            var root = new StackPanel { Margin = new Thickness(26, 24, 26, 22) };
            root.Children.Add(content);
            if (buttons.Length > 0)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
                foreach (var b in buttons)
                {
                    b.Margin = new Thickness(8, 0, 0, 0);
                    row.Children.Add(b);
                }
                root.Children.Add(row);
            }
            window.Content = root;
            return window;
        }

        /// <summary>Asks for a short piece of text, e.g. a preset name. Returns null if cancelled.</summary>
        public static string Ask(Window owner, string title, string label, string initial)
        {
            var box = new TextBox { Text = initial ?? string.Empty, MinHeight = 36, Margin = new Thickness(0, 8, 0, 0) };
            var content = Ui.Column(Ui.Heading(title, 17), Ui.Subtle(label).Margin(0, 6, 0, 0), box);
            var ok = Ui.Button("Save", null, "PrimaryButton");
            var cancel = Ui.Button("Cancel");
            var window = Dialog(owner, title, content, 420, cancel, ok);
            ok.IsDefault = true;
            cancel.IsCancel = true;
            string result = null;
            ok.Click += (s, e) =>
            {
                if (string.IsNullOrWhiteSpace(box.Text)) return;
                result = box.Text.Trim();
                window.DialogResult = true;
            };
            window.Loaded += (s, e) =>
            {
                box.Focus();
                box.SelectAll();
            };
            window.ShowDialog();
            return result;
        }
    }
}
