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

        private const int SW_RESTORE = 9;

        private static Mutex _instanceMutex;

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
                try
                {
                    IntPtr hwnd = FindWindow(null, "Sarah's Toolkit");
                    if (hwnd != IntPtr.Zero)
                    {
                        ShowWindow(hwnd, SW_RESTORE);
                        SetForegroundWindow(hwnd);
                    }
                }
                catch { }
                Shutdown();
                return;
            }

            base.OnStartup(e);
        }
    }
}
