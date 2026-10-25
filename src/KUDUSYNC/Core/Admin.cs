// Shared KUDU code: this file comes from kudu-common (https://github.com/azeembukhariprivate/kudu-common).
// Change it there and run its sync script; the build fails when this copy differs from kudu-common.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;

namespace Kudusync.Core
{
    public static class Admin
    {
        private static bool? _isAdmin;

        public static bool IsAdmin
        {
            get
            {
                if (_isAdmin == null)
                {
                    try
                    {
                        using (var identity = WindowsIdentity.GetCurrent())
                            _isAdmin = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
                    }
                    catch
                    {
                        _isAdmin = false;
                    }
                }
                return _isAdmin.Value;
            }
        }

        /// <summary>Starts a new copy of the app as administrator. Returns false if the user said no.</summary>
        public static bool RestartElevated(IEnumerable<string> args)
        {
            try
            {
                Process.Start(new ProcessStartInfo(AppInfo.ExePath, Args.Join(args))
                {
                    UseShellExecute = true,
                    Verb = "runas"
                });
                return true;
            }
            catch (Win32Exception)
            {
                return false; // The Windows prompt was cancelled.
            }
        }
    }
}
