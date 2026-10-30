// Shared KUDU code: this file comes from kudu-common (https://github.com/azeembukhariprivate/kudu-common).
// Change it there and run its sync script; the build fails when this copy differs from kudu-common.
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
        public static void Info(Window owner, string message, string title = AppInfo.Name) =>
            Show(owner, message, title, MessageBoxButton.OK, MessageBoxImage.Information);

        public static void Error(Window owner, string message, string title = AppInfo.Name) =>
            Show(owner, message, title, MessageBoxButton.OK, MessageBoxImage.Warning);

        public static bool Confirm(Window owner, string message, string title = AppInfo.Name) =>
            Show(owner, message, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

        private static MessageBoxResult Show(Window owner, string message, string title, MessageBoxButton buttons, MessageBoxImage image)
        {
            if (SelfTest.Running)
            {
                // Never block the automated self-test with a message box.
                if (image == MessageBoxImage.Warning) SelfTest.Errors.Add("Error message shown: " + message);
                return buttons == MessageBoxButton.OK ? MessageBoxResult.OK : MessageBoxResult.No;
            }
            return owner != null && owner.IsVisible
                ? MessageBox.Show(owner, message, title, buttons, image)
                : MessageBox.Show(message, title, buttons, image);
        }

        public static string PickFolder(Window owner, string description, string start = null)
        {
            using (var dialog = new System.Windows.Forms.FolderBrowserDialog
                   {
                       Description = description,
                       ShowNewFolderButton = true,
                       SelectedPath = start != null && System.IO.Directory.Exists(start) ? start : string.Empty
                   })
            {
                return dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK ? dialog.SelectedPath : null;
            }
        }

        public static List<string> PickFiles(Window owner, string title)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Title = title, Filter = "All files|*.*", Multiselect = true };
            return Show(dialog, owner) ? dialog.FileNames.ToList() : new List<string>();
        }

        public static string PickFile(Window owner, string title, string filter)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Title = title, Filter = filter, CheckFileExists = true };
            return Show(dialog, owner) ? dialog.FileName : null;
        }

        public static string SaveFile(Window owner, string title, string fileName, string filter)
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = title,
                FileName = fileName,
                Filter = filter,
                OverwritePrompt = true,
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
            };
            return Show(dialog, owner) ? dialog.FileName : null;
        }

        private static bool Show(Microsoft.Win32.CommonDialog dialog, Window owner) =>
            (owner != null && owner.IsVisible ? dialog.ShowDialog(owner) : dialog.ShowDialog()) == true;

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
            public int Left, Top, Right, Bottom;
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct MonitorInfo
        {
            public int Size;
            public Bounds Monitor;
            public Bounds Work;
            public int Flags;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr handle, int flags);

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
