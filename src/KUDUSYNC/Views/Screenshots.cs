using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Kudusync.Core;

namespace Kudusync.Views
{
    /// <summary>
    /// Pictures of every window the self-test opens, saved in a "screenshots" folder next to the exe, so the layouts can
    /// be looked at without running the app. The GitHub build keeps them as a download of their own.
    /// </summary>
    public static class Screenshots
    {
        private static int _count;

        public static string Folder => Path.Combine(Path.GetDirectoryName(AppInfo.ExePath) ?? ".", "screenshots");

        /// <summary>Saves every open window, named after the step.</summary>
        public static void All(string step)
        {
            if (Application.Current == null) return;
            foreach (var w in Application.Current.Windows.OfType<Window>().Where(w => w.IsVisible).ToList()) One(w, step);
        }

        public static void One(Window window, string step)
        {
            try
            {
                if (!(window.Content is FrameworkElement root) || root.ActualWidth < 1 || root.ActualHeight < 1) return;
                Directory.CreateDirectory(Folder);
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth), (int)Math.Ceiling(root.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                // The window's background isn't part of its content, so it's painted first.
                var visual = new DrawingVisual();
                using (var dc = visual.RenderOpen())
                {
                    dc.DrawRectangle(window.Background ?? Brushes.White, null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
                    dc.DrawRectangle(new VisualBrush(root), null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
                }
                bitmap.Render(visual);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                var name = $"{++_count:000} {Clean(step)} - {Clean(window.Title)}.png";
                using (var fs = File.Create(Path.Combine(Folder, name))) encoder.Save(fs);
            }
            catch (Exception)
            {
                // A picture that can't be taken never fails the self-test.
            }
        }

        private static string Clean(string text)
        {
            var sb = new StringBuilder();
            foreach (var c in text ?? string.Empty) sb.Append(Path.GetInvalidFileNameChars().Contains(c) ? '_' : c);
            var s = sb.ToString().Trim();
            return s.Length > 60 ? s.Substring(0, 60) : s;
        }
    }
}
