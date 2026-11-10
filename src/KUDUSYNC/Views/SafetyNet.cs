// Shared KUDU code: this file comes from kudu-common (https://github.com/azeembukhariprivate/kudu-common).
// Change it there and run its sync script; the build fails when this copy differs from kudu-common.
using System;
using System.Windows;
using Kudusync.Core;

namespace Kudusync.Views
{
    /// <summary>
    /// Says so in plain words when the app has to close because of an error in work going on behind the window.
    /// Errors in the window itself are caught by the app (DispatcherUnhandledException) and it carries on; an error
    /// on another thread can't be recovered from, and without this the app would just vanish.
    /// </summary>
    public static class SafetyNet
    {
        private static bool _installed;

        /// <summary>
        /// Call once when the app starts with its window. Not for runs with nobody watching (a schedule, the
        /// self-test): a message there would wait for ever for someone to close it.
        /// </summary>
        public static void Install()
        {
            if (_installed) return;
            _installed = true;
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                if (!e.IsTerminating) return;
                var why = (e.ExceptionObject as Exception)?.Message;
                try
                {
                    MessageBox.Show(
                        AppInfo.Name + " ran into a problem it couldn't get past, and has to close." +
                        (string.IsNullOrEmpty(why) ? string.Empty : "\n\n" + why),
                        AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Error);
                }
                catch (Exception)
                {
                    // Nothing more can be done; Windows closes the app.
                }
            };
        }
    }
}
