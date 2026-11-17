using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;
using Kudusync.Core;
using Microsoft.Win32;

namespace Kudusync.Views
{
    /// <summary>
    /// Windows notifications that stay in the notification centre until they're looked at (a notification from the
    /// notification-area icon disappears with the icon). KUDUSYNC registers its name, icon and a "kudusync:" link for
    /// the current user only, so a click opens KUDUSYNC even when it isn't running. When Windows can't show one (some
    /// servers, notifications turned off), the short notification is shown instead.
    /// </summary>
    public static class Toasts
    {
        public const string AppId = "Azeem.KUDUSYNC";
        public const string Link = "kudusync:alerts";
        private const string AppIdKey = @"Software\Classes\AppUserModelId\" + AppId;
        private const string LinkKey = @"Software\Classes\kudusync";

        public static string IconFile => Path.Combine(DataStore.Folder, "notification-icon.png");

        /// <summary>Shows a lasting notification about an alert. False when Windows couldn't show it.</summary>
        public static bool Show(Alert alert)
        {
            if (SelfTest.Running && !SelfTest.AllowToasts) return true;
            try
            {
                Register();
                string E(string s) => WebUtility.HtmlEncode(s ?? string.Empty);
                var xml = new Windows.Data.Xml.Dom.XmlDocument();
                xml.LoadXml(
                    "<toast launch=\"" + Link + "\" activationType=\"protocol\">" +
                    "<visual><binding template=\"ToastGeneric\"><text>" + E(alert.Title) + "</text><text>" + E(alert.Text) + "</text></binding></visual>" +
                    "<actions><action content=\"" + E(Lang.T("Open KUDUSYNC")) + "\" activationType=\"protocol\" arguments=\"" + Link + "\"/></actions>" +
                    "</toast>");
                var toast = new Windows.UI.Notifications.ToastNotification(xml)
                {
                    // One per pair: a newer alert about the same pair takes the older one's place.
                    Tag = TagOf(alert.PairId),
                    Group = "alerts",
                    ExpirationTime = DateTimeOffset.Now.AddDays(7)
                };
                Windows.UI.Notifications.ToastNotificationManager.CreateToastNotifier(AppId).Show(toast);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>Takes a pair's notification out of the notification centre (it was looked at).</summary>
        public static void Remove(string pairId)
        {
            try { Windows.UI.Notifications.ToastNotificationManager.History.Remove(TagOf(pairId), "alerts", AppId); }
            catch (Exception) { /* not there, or not supported */ }
        }

        /// <summary>Whether a notification about a pair is in the notification centre (for the self-test).</summary>
        public static bool InNotificationCentre(string pairId)
        {
            try { return Windows.UI.Notifications.ToastNotificationManager.History.GetHistory(AppId).Any(t => t.Tag == TagOf(pairId)); }
            catch (Exception) { return false; }
        }

        public static string TagOf(string pairId) => "pair-" + DataStore.StableHash(pairId ?? string.Empty);

        /// <summary>KUDUSYNC's name and icon for notifications, and the "kudusync:" link a click opens (this user only).</summary>
        public static void Register()
        {
            SetCurrentProcessExplicitAppUserModelID(AppId);
            if (!File.Exists(IconFile))
            {
                Directory.CreateDirectory(DataStore.Folder);
                var encoder = new PngBitmapEncoder();
                using (var stream = Dialogs.AppIconStream())
                {
                    var decoder = new IconBitmapDecoder(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                    encoder.Frames.Add(decoder.Frames.OrderByDescending(f => f.PixelWidth).First());
                }
                using (var file = File.Create(IconFile)) encoder.Save(file);
            }
            using (var key = Registry.CurrentUser.CreateSubKey(AppIdKey))
            {
                key.SetValue("DisplayName", "KUDUSYNC");
                key.SetValue("IconUri", IconFile);
            }
            using (var key = Registry.CurrentUser.CreateSubKey(LinkKey))
            {
                key.SetValue(string.Empty, "URL:KUDUSYNC");
                key.SetValue("URL Protocol", string.Empty);
                using (var command = key.CreateSubKey(@"shell\open\command"))
                    command.SetValue(string.Empty, "\"" + AppInfo.ExePath + "\" \"%1\"");
            }
        }

        /// <summary>Removes what <see cref="Register"/> added (when KUDUSYNC is uninstalled).</summary>
        public static void Unregister()
        {
            try { Registry.CurrentUser.DeleteSubKeyTree(AppIdKey, false); } catch { /* ignore */ }
            try { Registry.CurrentUser.DeleteSubKeyTree(LinkKey, false); } catch { /* ignore */ }
            try { Windows.UI.Notifications.ToastNotificationManager.History.Clear(AppId); } catch { /* ignore */ }
        }

        /// <summary>Notifies the alerts not notified yet: lasting notifications, or the short kind if Windows can't.</summary>
        public static void NotifyNew()
        {
            var alerts = Alerts.TakeNew(DateTime.Now);
            if (Settings.Notify == NotifyMode.Never) return; // notifications are off; the window and the tray icon still say so
            foreach (var alert in alerts)
                if (!Show(alert) && !SelfTest.Running) Notifier.ShowAlertAndWait(alert);
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
        private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);
    }
}
