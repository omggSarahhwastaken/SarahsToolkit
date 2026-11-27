using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace SarahsToolkit
{
    public partial class App : Application
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        private const int SW_SHOW = 5;

        private static Mutex _instanceMutex;
        // Wakes the first instance when a second launch happens. A plain
        // FindWindow + ShowWindow can't do it: SW_RESTORE won't un-hide a
        // window parked with Hide(), so the tray-parked app stayed
        // invisible. The event is held in a static so it lives for the
        // app's life, next to the mutex.
        private static EventWaitHandle _wakeEvent;

        /// <summary>The wake event the first instance listens on (null if it could not be created).</summary>
        public static EventWaitHandle WakeEvent => _wakeEvent;

        /// <summary>
        /// Writes full exception details (type, message, stack trace) to
        /// %LOCALAPPDATA%\SarahsToolkit\crash.log so a crash is never a mystery
        /// again. UI-thread exceptions are logged and swallowed so the app
        /// stays alive; fatal ones are logged before the process dies.
        /// </summary>
        private void SetupCrashLogging()
        {
            string logPath;
            try
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "SarahsToolkit");
                Directory.CreateDirectory(dir);
                logPath = Path.Combine(dir, "crash.log");
            }
            catch { return; }

            void WriteCrash(string source, Exception ex)
            {
                try
                {
                    string version = Assembly.GetExecutingAssembly()
                        .GetName().Version?.ToString() ?? "?";
                    File.AppendAllText(logPath,
                        $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] v{version} [{source}]" +
                        Environment.NewLine + (ex?.ToString() ?? "(no exception)") +
                        Environment.NewLine + Environment.NewLine);
                }
                catch { }
            }

            DispatcherUnhandledException += (s, e) =>
            {
                WriteCrash("ui", e.Exception);
                e.Handled = true;
            };
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
                WriteCrash("fatal", e.ExceptionObject as Exception);
            TaskScheduler.UnobservedTaskException += (s, e) =>
            {
                WriteCrash("task", e.Exception);
                e.SetObserved();
            };
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            SetupCrashLogging();

            // Single instance: a second launch (e.g. from the installer
            // auto-launch while an old instance is still shutting down, or
            // double-clicking the shortcut) wakes the running copy instead
            // of starting a second process that could stomp its settings.
            // The mutex is held in a static so it lives for the app's life.
            bool created = false;
            try { _instanceMutex = new Mutex(true, @"Local\Sarah.SarahsToolkit.SingleInstance", out created); }
            catch { created = true; }
            if (!created)
            {
                // Signal the running copy first: its watcher thread brings
                // the window back even when it is parked in the tray.
                // If the event can't be opened, the FindWindow fallback
                // below still runs.
                try
                {
                    using (var wake = EventWaitHandle.OpenExisting(@"Local\Sarah.SarahsToolkit.Wake"))
                        wake.Set();
                }
                catch { }
                try
                {
                    IntPtr hwnd = FindWindow(null, "Sarah's Toolkit");
                    if (hwnd != IntPtr.Zero)
                    {
                        // SW_SHOW, not SW_RESTORE: restore won't un-hide a
                        // window hidden with Hide() (the tray-parked
                        // window), so the second launch used to show
                        // nothing at all.
                        ShowWindow(hwnd, SW_SHOW);
                        SetForegroundWindow(hwnd);
                    }
                }
                catch { }
                Shutdown();
                return;
            }
            // First instance: own the wake event the second launch signals.
            try { _wakeEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\Sarah.SarahsToolkit.Wake"); }
            catch { _wakeEvent = null; }

            base.OnStartup(e);

            // Splash while the main window builds and loads. It closes once
            // BOTH are true: startup work finished AND the draw-in animation
            // played through, so the animation is actually seen instead of
            // being cut off the moment the app is ready. The 8-second cap
            // closes it no matter what, so a slow step can't hold the app
            // hostage.
            // The splash itself is guarded (finding 5): if its window
            // fails to open, the app still starts — just without a splash.
            SplashWindow splash = null;
            try
            {
                splash = new SplashWindow();
                splash.Show();
            }
            catch
            {
                splash = null;
            }
            bool startupDone = false;
            bool animationDone = false;
            var splashTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(8)
            };
            void TryCloseSplash()
            {
                if (splash == null || !startupDone || !animationDone) return;
                splashTimer.Stop();
                try { splash.Close(); } catch { }
            }
            splashTimer.Tick += (s, _) =>
            {
                splashTimer.Stop();
                try { splash?.Close(); } catch { }
            };
            splashTimer.Start();
            if (splash != null)
                splash.AnimationFinished += (s, _) => { animationDone = true; TryCloseSplash(); };

            // Create the main window on a background-priority callback so
            // the splash gets to render and start the video before the
            // heavy main-window load begins.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    var main = new MainWindow();
                    main.StartupComplete += (s, _) => { startupDone = true; TryCloseSplash(); };
                    MainWindow = main;
                    main.Show();
                }
                catch
                {
                    Shutdown();
                }
            }), System.Windows.Threading.DispatcherPriority.Background);
        }
    }
}
