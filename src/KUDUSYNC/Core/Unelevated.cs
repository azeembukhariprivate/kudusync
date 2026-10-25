// Shared KUDU code: this file comes from kudu-common (https://github.com/azeembukhariprivate/kudu-common).
// Change it there and run its sync script; the build fails when this copy differs from kudu-common.
using System;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Kudusync.Core
{
    /// <summary>
    /// Opens files and folders through File Explorer's desktop, so they run with normal rights even while the app runs
    /// as administrator. Otherwise a file opened from it, perhaps a downloaded program, would run as administrator too. This asks the desktop's own shell to open it, as Raymond Chen describes in
    /// "How can I launch an unelevated process from my elevated process?".
    /// </summary>
    public static class Unelevated
    {
        /// <summary>Opens a file, folder or program with normal rights. Returns false when File Explorer couldn't be asked.</summary>
        public static bool Open(string file, string arguments = null)
        {
            try
            {
                var windows = (IShellWindows)new ShellWindows();
                object location = 0; // the desktop
                object root = null;
                var desktop = windows.FindWindowSW(ref location, ref root, SwcDesktop, out _, SwfoNeedDispatch);
                if (desktop == null) return false;
                var service = SidTopLevelBrowser;
                var iid = typeof(IShellBrowser).GUID;
                var browser = (IShellBrowser)((IServiceProvider)desktop).QueryService(ref service, ref iid);
                browser.QueryActiveShellView(out var view);
                var dispatch = IidDispatch;
                view.GetItemObject(SvgioBackground, ref dispatch, out var folderView);
                var shell = folderView.GetType().InvokeMember("Application", BindingFlags.GetProperty, null, folderView, null);
                shell.GetType().InvokeMember("ShellExecute", BindingFlags.InvokeMethod, null, shell,
                    new object[] { file, arguments ?? string.Empty, string.Empty, "open", 1 });
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private const int SwcDesktop = 8;
        private const int SwfoNeedDispatch = 1;
        private const uint SvgioBackground = 0;
        private static readonly Guid SidTopLevelBrowser = new Guid("4C96BE40-915C-11CF-99D3-00AA004AE837");
        private static readonly Guid IidDispatch = new Guid("00020400-0000-0000-C000-000000000046");

        [ComImport, Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39")]
        private class ShellWindows { }

        // Only FindWindowSW is called; the methods before it keep the interface's layout.
        [ComImport, Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
        private interface IShellWindows
        {
            int Count { get; }
            [return: MarshalAs(UnmanagedType.IDispatch)] object Item(object index);
            [return: MarshalAs(UnmanagedType.IUnknown)] object NewEnum();
            void Register();
            void RegisterPending();
            void Revoke();
            void OnNavigate();
            void OnActivated();
            [return: MarshalAs(UnmanagedType.IDispatch)]
            object FindWindowSW(ref object location, ref object root, int windowClass, out int hwnd, int options);
        }

        [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IServiceProvider
        {
            [return: MarshalAs(UnmanagedType.IUnknown)] object QueryService(ref Guid service, ref Guid iid);
        }

        // Only QueryActiveShellView is called (the first two are IOleWindow's).
        [ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellBrowser
        {
            void GetWindow();
            void ContextSensitiveHelp();
            void InsertMenusSB();
            void SetMenuSB();
            void RemoveMenusSB();
            void SetStatusTextSB();
            void EnableModelessSB();
            void TranslateAcceleratorSB();
            void BrowseObject();
            void GetViewStateStream();
            void GetControlWindow();
            void SendControlMsg();
            void QueryActiveShellView(out IShellView view);
        }

        // Only GetItemObject is called (the first two are IOleWindow's).
        [ComImport, Guid("000214E3-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellView
        {
            void GetWindow();
            void ContextSensitiveHelp();
            void TranslateAccelerator();
            void EnableModeless();
            void UIActivate();
            void Refresh();
            void CreateViewWindow();
            void DestroyViewWindow();
            void GetCurrentInfo();
            void AddPropertySheetPages();
            void SaveViewState();
            void SelectItem();
            void GetItemObject(uint item, ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object result);
        }
    }
}
