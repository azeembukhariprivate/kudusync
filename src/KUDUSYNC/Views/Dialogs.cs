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
        public static void Info(Window owner, string message, string title = "KUDUSYNC") =>
            Show(owner, message, title, MessageBoxButton.OK, MessageBoxImage.Information);

        public static void Error(Window owner, string message, string title = "KUDUSYNC") =>
            Show(owner, message, title, MessageBoxButton.OK, MessageBoxImage.Warning);

        public static bool Confirm(Window owner, string message, string title = "KUDUSYNC") =>
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
                    using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Kudusync.Assets.KUDUSYNC.ico"))
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
            Assembly.GetExecutingAssembly().GetManifestResourceStream("Kudusync.Assets.KUDUSYNC.ico");

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
        }

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
