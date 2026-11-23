// Where KUDUSYNC starts: the main window, the tray part, a scheduled or right-click run, the self-test, and handing
// over to a copy already running.
using System;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using Kudusync.Core;
using Kudusync.Views;

namespace Kudusync
{
    public static class Program
    {
        /// <summary>
        /// KUDUSYNC.exe                          open
        /// KUDUSYNC.exe --sync-folder "D:\x"     run the pairs that use this folder (folders' right-click menu)
        /// KUDUSYNC.exe --new-pair "D:\x"        create a pair backing up this folder (right-click menu)
        /// KUDUSYNC.exe --history                open the list of recent runs (clicking a notification)
        /// KUDUSYNC.exe kudusync:alerts         open, showing what needs looking at (clicking a lasting notification)
        /// KUDUSYNC.exe --run-scheduled ID       run a pair with no window (Windows Task Scheduler)
        /// KUDUSYNC.exe --tray                   run in the notification area: live sync and drive notifications
        /// KUDUSYNC.exe --register-shell | --unregister   used by the installer
        /// KUDUSYNC.exe --self-test              automated checks for the build
        /// Any of them can add --data "folder" to keep pairs and logs somewhere else (used by the self-test).
        /// </summary>
        [STAThread]
        public static int Main(string[] args)
        {
            bool Has(string name) => args.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
            string Value(string name)
            {
                for (var i = 0; i < args.Length - 1; i++)
                    if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
                return null;
            }

            var data = Value("--data");
            if (!string.IsNullOrEmpty(data)) DataStore.OverrideFolder = data;

            var app = new App();
            if (Has("--self-test")) return app.RunSelfTest();

            if (Has("--register-shell"))
            {
                Settings.ShellMenu = true;
                ShellIntegration.Register();
                return 0;
            }
            if (Has("--unregister"))
            {
                ShellIntegration.Remove();
                ShellIntegration.SetStartWithWindows(false);
                Schedules.RemoveAll();
                Tray.StopRunning();
                Toasts.Unregister();
                return 0;
            }

            var scheduled = Value("--run-scheduled");
            if (scheduled != null) return app.RunScheduled(scheduled);

            if (Has("--tray")) return app.RunTray();

            return app.RunMain(args);
        }
    }

    public sealed class App : Application
    {
        /// <summary>The running app.</summary>
        public static App Instance => (App)Current;

        /// <summary>Starts the main window (or hands the command to the one already open).</summary>
        public int RunMain(string[] args)
        {
            using (var single = new SingleInstance("main"))
            {
                if (!single.IsFirst && single.Forward(args)) return 0;
                Startup += (s, e) =>
                {
                    Init();
                    var main = new MainWindow();
                    MainWindow = main;
                    main.Show();
                    ShellIntegration.Refresh();
                    if (single.IsFirst)
                        single.Listen(forwarded => Dispatcher.BeginInvoke(new Action(() =>
                        {
                            main.BringToFront();
                            main.HandleCommandLine(forwarded);
                        })));
                    main.HandleCommandLine(args);
                };
                return Run();
            }
        }

        /// <summary>A scheduled run: no window, just a notification when it's done (if wanted).</summary>
        public int RunScheduled(string pairId)
        {
            var pair = DataStore.LoadPairs().FirstOrDefault(p => p.Id == pairId);
            if (pair == null)
            {
                Schedules.Remove(pairId); // the pair was deleted
                return 2;
            }
            var result = Runner.RunUnattended(pair, Triggers.Scheduled);
            // A scheduled backup that went wrong, and any other backup missed meanwhile, get a notice that stays.
            var lasting = result.Status == RunStatus.Failed || result.Status == RunStatus.Warnings ||
                          (result.Status == RunStatus.Skipped && result.Message != null && result.Message.StartsWith("Paused", StringComparison.Ordinal));
            try { Toasts.NotifyNew(); }
            catch (Exception) { lasting = false; }
            if (!lasting && Runner.ShouldNotify(result, Triggers.Scheduled)) Notifier.ShowAndWait(result);
            return result.Status == RunStatus.Ok || result.Status == RunStatus.Warnings || result.Status == RunStatus.Skipped ? 0 : 1;
        }

        /// <summary>Starts the tray part (only one per user).</summary>
        public int RunTray()
        {
            using (var single = new SingleInstance("tray"))
            {
                if (!single.IsFirst) return 0;
                Startup += (s, e) =>
                {
                    ShutdownMode = ShutdownMode.OnExplicitShutdown;
                    DispatcherUnhandledException += (o, x) =>
                    {
                        Tray.Log("Error: " + x.Exception);
                        x.Handled = true;
                    };
                    ThemeManager.Init(Settings.Theme);
                    new Tray().Start();
                };
                return Run();
            }
        }

        /// <summary>Runs the self-test instead of the app, and returns its result as the exit code.</summary>
        public int RunSelfTest()
        {
            var code = 1;
            Startup += (s, e) =>
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                DispatcherUnhandledException += OnUnhandledException;
                code = SelfTest.Run();
                Shutdown(code);
            };
            Run();
            return code;
        }

        /// <summary>What every way of starting needs: the theme, the language and the error handler.</summary>
        internal void Init()
        {
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            DispatcherUnhandledException += OnUnhandledException;
            SafetyNet.Install(); // says so if an error behind the window closes the app
            LangUi.Start(); // the app in the chosen language
            ThemeManager.Init(Settings.Theme);
            ThemeManager.WatchSystem();
        }

        /// <summary>
        /// Shows an error that wasn't expected instead of closing the app (the self-test keeps it as a failure).
        /// </summary>
        private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            if (SelfTest.Running)
            {
                SelfTest.Errors.Add(e.Exception.ToString());
                e.Handled = true;
                return;
            }
            Dialogs.Error(MainWindow?.IsVisible == true ? MainWindow : null, "Something went wrong:\n\n" + e.Exception.Message);
            e.Handled = true;
        }
    }
}
