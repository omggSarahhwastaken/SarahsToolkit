using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using SarahsToolkit.Models;
using SarahsToolkit.Services;

namespace SarahsToolkit
{
    public partial class MainWindow : Window
    {
        private readonly TweakService _tweaks = new TweakService();
        private readonly CleanupService _cleanup = new CleanupService();
        private readonly DiagnosticsService _diag = new DiagnosticsService();
        private List<StartupEntry> _startupEntries = new List<StartupEntry>();
        private readonly DebloatService _debloat = new DebloatService();
        private readonly ServiceOptimizerService _services = new ServiceOptimizerService();
        private readonly ToolsService _tools = new ToolsService();
        private readonly UpdateService _updates = new UpdateService();
        private readonly PerformanceTrackerService _perf = new PerformanceTrackerService();
        private readonly SettingsService _settings = new SettingsService();
        private bool _applyingSettings;
        private DispatcherTimer _perfTimer;
        private bool _perfSampling;
        private System.Windows.Forms.NotifyIcon _tray;
        private bool _allowClose;

        private List<TweakDefinition> _tweakDefs = new List<TweakDefinition>();
        private List<PresetDefinition> _presetDefs = new List<PresetDefinition>();
        // Tweaks the config file says are applied but the registry disagrees
        // with (reverted outside the app, e.g. by a Windows update).
        private List<TweakDefinition> _drifted = new List<TweakDefinition>();
        private readonly PresetService _presets = new PresetService();
        private readonly Dictionary<string, TextBlock> _presetRatingLabels =
            new Dictionary<string, TextBlock>();
        private List<DebloatApp> _debloatApps = new List<DebloatApp>();
        private List<ServiceDefinition> _serviceDefs = new List<ServiceDefinition>();
        private bool _servicesRefreshed = false;
        private bool _dashboardLoaded = false;
        private readonly GameFeedService _games = new GameFeedService();
        private readonly VitalsService _vitals = new VitalsService();
        private readonly HealthService _health = new HealthService();
        private bool _healthLoaded;
        private DispatcherTimer _dashTimer;
        private bool _dashboardLoading;
        private CancellationTokenSource _cleanCts;
        private bool _buildingUi;

        private class CategoryAnalysis
        {
            public CleanupCategory Cat;
            public long Bytes;
            public long Files;
            public CheckBox Box;
        }
        private List<CategoryAnalysis> _analyses = new List<CategoryAnalysis>();

        private DispatcherTimer _gaugeTimer;
        private double _gaugeCurrent;
        private double _gaugeTarget;
        private const double GaugeMaxMbps = 1000;

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute,
            ref int pvAttribute, int cbAttribute);
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

        public MainWindow()
        {
            InitializeComponent();
            Loaded += MainWindow_Loaded;
            SourceInitialized += MainWindow_SourceInitialized;
            // Let Windows shut down / restart without being trapped in the tray.
            Application.Current.SessionEnding += (s, e) => _allowClose = true;
        }

        // ---------- System tray ----------

        protected override void OnClosing(CancelEventArgs e)
        {
            if (_allowClose)
            {
                if (_tray != null) { _tray.Dispose(); _tray = null; }
                try { _vitals.Dispose(); } catch { }
                base.OnClosing(e);
                return;
            }
            // Closing the window parks the app in the tray instead of exiting,
            // unless the user turned that off in Settings.
            if (!_settings.Settings.MinimizeToTray)
            {
                if (_tray != null) { _tray.Dispose(); _tray = null; }
                try { _vitals.Dispose(); } catch { }
                base.OnClosing(e);
                return;
            }
            e.Cancel = true;
            Hide();
            EnsureTrayIcon();
            _tray.Visible = true;
            _tray.ShowBalloonTip(3000, "Sarah's Toolkit",
                "Still running — double-click the tray icon to reopen, or right-click it to exit.",
                System.Windows.Forms.ToolTipIcon.Info);
        }

        private void EnsureTrayIcon()
        {
            if (_tray != null) return;
            _tray = new System.Windows.Forms.NotifyIcon();
            try
            {
                // Environment.ProcessPath works for single-file apps, where
                // Assembly.Location returns an empty string.
                string exePath = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exePath))
                    _tray.Icon = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
            }
            catch { }
            _tray.Text = "Sarah's Toolkit";
            _tray.DoubleClick += (s, a) => ShowFromTray();
            var menu = new System.Windows.Forms.ContextMenuStrip();
            var showItem = new System.Windows.Forms.ToolStripMenuItem("Show Sarah's Toolkit");
            showItem.Click += (s, a) => ShowFromTray();
            var exitItem = new System.Windows.Forms.ToolStripMenuItem("Exit");
            exitItem.Click += (s, a) =>
            {
                _allowClose = true;
                if (_tray != null) { _tray.Dispose(); _tray = null; }
                Application.Current.Shutdown();
            };
            menu.Items.Add(showItem);
            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            menu.Items.Add(exitItem);
            _tray.ContextMenuStrip = menu;
        }

        private void ShowFromTray()
        {
            Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            if (_tray != null) _tray.Visible = false;
            Activate();
        }

        private void MainWindow_SourceInitialized(object sender, EventArgs e)
        {
            try
            {
                IntPtr hwnd = new WindowInteropHelper(this).Handle;
                // Dark title bar to match the app theme (Windows 10 1809+).
                // The window icon (app.ico) is left alone so it shows in the title bar.
                int dark = 1;
                DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE,
                    ref dark, sizeof(int));
            }
            catch
            {
                // Cosmetic only; the app works fine without it.
            }
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            // Clean up the update installer the updater left in %TEMP% — it has
            // already installed by the time the app relaunches. Best effort.
            try
            {
                string leftover = System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(), "SarahsToolkitSetup_update.exe");
                if (System.IO.File.Exists(leftover))
                {
                    try { System.IO.File.Delete(leftover); } catch { }
                }
            }
            catch { }

            try
            {
                string v = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3);
                string vt = "v" + (string.IsNullOrEmpty(v) ? "1.0.0" : v);
                VersionText.Text = vt;
                RailVersionText.Text = vt;
            }
            catch
            {
                VersionText.Text = "v1.0.0";
                RailVersionText.Text = "v1.0.0";
            }

            try
            {
                _tweakDefs = _tweaks.LoadTweaks();
                BuildTweakTab(OptimizePanel, new[] { "Privacy", "Gaming", "Performance" });
                BuildTweakTab(CustomizePanel, new[] { "Theme", "Taskbar", "Explorer", "Start" });
                BuildTweakTab(SecurityTweaksPanel, new[] { "Security" });
            }
            catch (Exception ex)
            {
                MessageBox.Show("Failed to load tweaks: " + ex.Message,
                    "Sarah's Toolkit", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            try
            {
                _presetDefs = _presets.LoadPresets();
                BuildPresetsTab();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Failed to load presets: " + ex.Message,
                    "Sarah's Toolkit", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            try
            {
                _debloatApps = _debloat.LoadApps();
                BuildDebloatList();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Failed to load debloat list: " + ex.Message,
                    "Sarah's Toolkit", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            try
            {
                _serviceDefs = _services.LoadDefinitions();
                BuildServicesList();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Failed to load service list: " + ex.Message,
                    "Sarah's Toolkit", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            // Performance tracker: sample every 15s, log only while a known game runs.
            _perfTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
            _perfTimer.Tick += PerfTimer_Tick;
            _perfTimer.Start();

            DrawGauge(0);
            SetStatus("Ready.");
            await RefreshDebloatInstalledAsync();

            // Settings: load, apply, and honor the startup-page choice.
            _settings.Load();
            BackfillLedgerIfNeeded();
            ApplySettings();
            // If tweaks the config remembers got reverted outside the app
            // (Windows updates do this), offer them back in one click.
            RefreshDriftButton();
            if (_drifted.Count > 0)
                SetStatus(_drifted.Count + " of your tweaks were changed outside the app " +
                    "(Windows updates can do this). Re-apply them from the Optimize page.");

            // Silent update check on every launch (unless disabled in Settings):
            // only speaks up if an update exists.
            if (_settings.Settings.CheckUpdatesOnLaunch)
                await CheckForUpdatesOnLaunchAsync();

            // Home dashboard: load on launch (Nav_Checked can fire before the pages
            // exist, so it can't be trusted to do the first load) and keep it live
            // with a 1-second refresh while Home is visible.
            _dashboardLoaded = true;
            await LoadDashboardAsync();
            BuildGameFeed();
            _dashTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _dashTimer.Tick += DashTimer_Tick;
            _dashTimer.Start();

            // Open on the user's chosen page.
            if (_settings.Settings.DefaultPage == "Last" &&
                !string.IsNullOrEmpty(_settings.Settings.LastPage) &&
                _settings.Settings.LastPage != "Home")
                NavigateToPage(_settings.Settings.LastPage);
        }

        // ---------- Navigation ----------

        private async void Nav_Checked(object sender, RoutedEventArgs e)
        {
            var rb = sender as RadioButton;
            if (rb == null || rb.IsChecked != true) return;
            // Fires during InitializeComponent before the pages exist.
            if (PageDashboard == null || PageHost == null) return;

            PageDashboard.Visibility = Visibility.Collapsed;
            PageHealth.Visibility = Visibility.Collapsed;
            PageCleanup.Visibility = Visibility.Collapsed;
            PageDebloat.Visibility = Visibility.Collapsed;
            PageServices.Visibility = Visibility.Collapsed;
            PageOptimize.Visibility = Visibility.Collapsed;
            PagePresets.Visibility = Visibility.Collapsed;
            PageCustomize.Visibility = Visibility.Collapsed;
            PageNetwork.Visibility = Visibility.Collapsed;
            PageSecurity.Visibility = Visibility.Collapsed;
            PageTools.Visibility = Visibility.Collapsed;
            PageDev.Visibility = Visibility.Collapsed;
            PageAbout.Visibility = Visibility.Collapsed;
            PageSettings.Visibility = Visibility.Collapsed;

            // Remember where the user was, for the "last page" startup option.
            _settings.Settings.LastPage = rb.Content.ToString();
            _settings.Save();

            switch (rb.Name)
            {
                case "NavDashboard":
                    PageDashboard.Visibility = Visibility.Visible;
                    PageTitle.Text = "Home";
                    PageSubtitle.Text = "System overview";
                    if (!_dashboardLoaded)
                    {
                        _dashboardLoaded = true;
                        await LoadDashboardAsync();
                    }
                    break;
                case "NavHealth":
                    PageHealth.Visibility = Visibility.Visible;
                    PageTitle.Text = "Health";
                    PageSubtitle.Text = "Battery, storage and system maintenance";
                    if (!_healthLoaded)
                    {
                        _healthLoaded = true;
                        _ = LoadHealthAsync();
                    }
                    break;
                case "NavCleanup":
                    PageCleanup.Visibility = Visibility.Visible;
                    PageTitle.Text = "Cleanup";
                    PageSubtitle.Text = "Temporary files and caches";
                    break;
                case "NavDebloat":
                    PageDebloat.Visibility = Visibility.Visible;
                    PageTitle.Text = "Debloat";
                    PageSubtitle.Text = "Remove unwanted preinstalled apps";
                    break;
                case "NavServices":
                    PageServices.Visibility = Visibility.Visible;
                    PageTitle.Text = "Services";
                    PageSubtitle.Text = "Trim unnecessary Windows services";
                    if (!_servicesRefreshed)
                    {
                        _servicesRefreshed = true;
                        await RefreshServiceStatesAsync();
                    }
                    break;
                case "NavOptimize":
                    PageOptimize.Visibility = Visibility.Visible;
                    PageTitle.Text = "Optimize";
                    PageSubtitle.Text = "Privacy, gaming and performance tweaks";
                    break;
                case "NavPresets":
                    PagePresets.Visibility = Visibility.Visible;
                    PageTitle.Text = "Presets";
                    PageSubtitle.Text = "One-click tweak bundles";
                    RefreshPresetRatings();
                    break;
                case "NavCustomize":
                    PageCustomize.Visibility = Visibility.Visible;
                    PageTitle.Text = "Customize";
                    PageSubtitle.Text = "Theme, taskbar, Explorer and Start";
                    break;
                case "NavNetwork":
                    PageNetwork.Visibility = Visibility.Visible;
                    PageTitle.Text = "Network";
                    PageSubtitle.Text = "DNS, speed test and rescue";
                    break;
                case "NavSecurity":
                    PageSecurity.Visibility = Visibility.Visible;
                    PageTitle.Text = "Security";
                    PageSubtitle.Text = "Defender exclusions";
                    RefreshDefenderPanel();
                    break;
                case "NavTools":
                    PageTools.Visibility = Visibility.Visible;
                    PageTitle.Text = "Tools";
                    PageSubtitle.Text = "Diagnostics and system utilities";
                    RefreshMemoryLabel();
                    break;
                case "NavDev":
                    PageDev.Visibility = Visibility.Visible;
                    PageTitle.Text = "Dev";
                    PageSubtitle.Text = "Validation suite for the app itself";
                    break;
                case "NavAbout":
                    PageAbout.Visibility = Visibility.Visible;
                    PageTitle.Text = "About";
                    PageSubtitle.Text = "Sarah's Toolkit";
                    break;
                case "NavSettings":
                    PageSettings.Visibility = Visibility.Visible;
                    PageTitle.Text = "Settings";
                    PageSubtitle.Text = "Appearance and behavior";
                    RefreshStorageLabel();
                    break;
            }
        }

        // ---------- Settings ----------

        private void ApplySettings()
        {
            _applyingSettings = true;
            try
            {
                ApplyGuiScale();
                _perf.Fahrenheit = _settings.Settings.TempFahrenheit;
                ApplyStartWithWindows();

                SettingsScaleCombo.SelectedIndex = ScaleToIndex(_settings.Settings.GuiScale);
                SettingsTrayCheck.IsChecked = _settings.Settings.MinimizeToTray;
                SettingsUpdatesCheck.IsChecked = _settings.Settings.CheckUpdatesOnLaunch;
                SettingsStartupCheck.IsChecked = _settings.Settings.StartWithWindows;
                SettingsDefaultPageCombo.SelectedIndex =
                    _settings.Settings.DefaultPage == "Last" ? 1 : 0;
                SettingsTempCombo.SelectedIndex =
                    _settings.Settings.TempFahrenheit ? 1 : 0;
                PerfTrackEnabled.IsChecked = _settings.Settings.LogPerformance;
            }
            finally { _applyingSettings = false; }
        }

        private static int ScaleToIndex(double scale)
        {
            double[] steps = { 0.8, 0.9, 1.0, 1.1, 1.25, 1.5 };
            int best = 2;
            for (int i = 0; i < steps.Length; i++)
                if (Math.Abs(steps[i] - scale) < Math.Abs(steps[best] - scale)) best = i;
            return best;
        }

        private void ApplyGuiScale()
        {
            double s = _settings.Settings.GuiScale;
            if (RootGrid != null)
                RootGrid.LayoutTransform = new ScaleTransform(s, s);
        }

        private void ApplyStartWithWindows()
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    if (key == null) return;
                    if (_settings.Settings.StartWithWindows)
                    {
                        key.SetValue("SarahsToolkit",
                            "\"" + Process.GetCurrentProcess().MainModule.FileName + "\"");
                        // Clear any "disabled in Task Manager" flag, so ON really means ON.
                        using (var approved = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run", true))
                            approved?.DeleteValue("SarahsToolkit", false);
                    }
                    else if (key.GetValue("SarahsToolkit") != null)
                        key.DeleteValue("SarahsToolkit");
                }
            }
            catch { }
        }

        private void NavigateToPage(string pageName)
        {
            RadioButton target;
            switch (pageName)
            {
                case "Cleanup": target = NavCleanup; break;
                case "Debloat": target = NavDebloat; break;
                case "Services": target = NavServices; break;
                case "Optimize": target = NavOptimize; break;
                case "Health": target = NavHealth; break;
                case "Presets": target = NavPresets; break;
                case "Customize": target = NavCustomize; break;
                case "Network": target = NavNetwork; break;
                case "Security": target = NavSecurity; break;
                case "Tools": target = NavTools; break;
                case "Dev": target = NavDev; break;
                case "About": target = NavAbout; break;
                case "Settings": target = NavSettings; break;
                default: target = NavDashboard; break;
            }
            if (target != null) target.IsChecked = true;
        }

        private void SettingsScale_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_applyingSettings || SettingsScaleCombo.SelectedIndex < 0) return;
            double[] steps = { 0.8, 0.9, 1.0, 1.1, 1.25, 1.5 };
            _settings.Settings.GuiScale = steps[SettingsScaleCombo.SelectedIndex];
            _settings.Save();
            ApplyGuiScale();
        }

        private void SettingsFlag_Changed(object sender, RoutedEventArgs e)
        {
            if (_applyingSettings) return;
            if (sender == SettingsTrayCheck)
                _settings.Settings.MinimizeToTray = SettingsTrayCheck.IsChecked == true;
            else if (sender == SettingsUpdatesCheck)
                _settings.Settings.CheckUpdatesOnLaunch = SettingsUpdatesCheck.IsChecked == true;
            _settings.Save();
        }

        private void SettingsStartup_Changed(object sender, RoutedEventArgs e)
        {
            if (_applyingSettings) return;
            _settings.Settings.StartWithWindows = SettingsStartupCheck.IsChecked == true;
            _settings.Save();
            ApplyStartWithWindows();
        }

        private void SettingsDefaultPage_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_applyingSettings || SettingsDefaultPageCombo.SelectedIndex < 0) return;
            _settings.Settings.DefaultPage =
                SettingsDefaultPageCombo.SelectedIndex == 1 ? "Last" : "Home";
            _settings.Save();
        }

        private void SettingsTemp_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_applyingSettings || SettingsTempCombo.SelectedIndex < 0) return;
            _settings.Settings.TempFahrenheit = SettingsTempCombo.SelectedIndex == 1;
            _settings.Save();
            _perf.Fahrenheit = _settings.Settings.TempFahrenheit;
        }

        private void SettingsOpenFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Directory.CreateDirectory(_settings.FolderPath);
                Process.Start(new ProcessStartInfo("explorer.exe", _settings.FolderPath)
                    { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not open the settings folder:\n" + ex.Message,
                    "Sarah's Toolkit", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // Shows the app's total disk footprint: program files + settings/logs.
        private void RefreshStorageLabel()
        {
            try
            {
                long appBytes = DirSize(AppDomain.CurrentDomain.BaseDirectory);
                long dataBytes = DirSize(_settings.FolderPath);
                SettingsStorageText.Text =
                    "App: " + FormatBytes(appBytes) +
                    "    Data (settings + logs): " + FormatBytes(dataBytes) +
                    "    Total: " + FormatBytes(appBytes + dataBytes);
            }
            catch
            {
                SettingsStorageText.Text = "Could not measure disk usage.";
            }
        }

        private static long DirSize(string path)
        {
            long total = 0;
            try
            {
                foreach (string f in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    try { total += new FileInfo(f).Length; } catch { }
                }
            }
            catch { }
            return total;
        }

        private void SettingsReset_Click(object sender, RoutedEventArgs e)
        {
            var res = MessageBox.Show("Reset all settings to their defaults?",
                "Sarah's Toolkit", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (res != MessageBoxResult.Yes) return;
            _settings.Settings.StartWithWindows = false;
            ApplyStartWithWindows();
            _settings.Reset();
            ApplySettings();
        }

        // ---------- Dashboard ----------

        private void DashTimer_Tick(object sender, EventArgs e)
        {
            if (WindowState == WindowState.Minimized) return;
            if (PageDashboard == null || PageDashboard.Visibility != Visibility.Visible) return;
            _ = LoadDashboardAsync(quiet: true);
            _ = UpdateVitalsAsync();
        }

        private bool _vitalsUpdating;

        private async Task UpdateVitalsAsync()
        {
            if (_vitalsUpdating) return;
            _vitalsUpdating = true;
            try
            {
                await _vitals.SampleAsync().ConfigureAwait(true);

                SetVital(VitalCpuVal, VitalCpuGraph, VitalCpuDot, _vitals.Cpu, _vitals.CpuHistory,
                    v => v.ToString("0") + "%");
                if (_vitals.HasGpu)
                {
                    VitalGpuCard.Visibility = Visibility.Visible;
                    SetVital(VitalGpuVal, VitalGpuGraph, VitalGpuDot, _vitals.Gpu, _vitals.GpuHistory,
                        v => v.ToString("0") + "%");
                }
                else
                {
                    VitalGpuCard.Visibility = Visibility.Collapsed;
                }
                SetVital(VitalRamVal, VitalRamGraph, VitalRamDot, _vitals.Ram, _vitals.RamHistory,
                    v => double.IsNaN(_vitals.RamUsedGb) || double.IsNaN(_vitals.RamTotalGb)
                        ? "—"
                        : _vitals.RamUsedGb.ToString("0.0") + "/" + _vitals.RamTotalGb.ToString("0.0") + " GB");
                VitalRamPct.Text = double.IsNaN(_vitals.Ram) ? "" : _vitals.Ram.ToString("0") + "% used";
                SetVital(VitalDiskVal, VitalDiskGraph, VitalDiskDot, _vitals.Disk, _vitals.DiskHistory,
                    v => v.ToString("0") + "%");
                SetVital(VitalNetVal, VitalNetGraph, VitalNetDot, _vitals.NetMbps, _vitals.NetHistory,
                    FormatMbps, DotColor.Blue);
                SetVital(VitalPingVal, VitalPingGraph, VitalPingDot, _vitals.PingMs, _vitals.PingHistory,
                    v => v.ToString("0") + " ms",
                    double.IsNaN(_vitals.PingMs) ? DotColor.Gray
                        : _vitals.PingMs >= 150 ? DotColor.Red
                        : _vitals.PingMs >= 80 ? DotColor.Amber : DotColor.Green);
            }
            catch { /* vitals are best-effort; never break the dashboard */ }
            finally { _vitalsUpdating = false; }
        }

        private static string FormatMbps(double mbps)
        {
            if (mbps < 1) return (mbps * 1000).ToString("0") + " Kbps";
            if (mbps < 1000) return mbps.ToString("0.0") + " Mbps";
            return (mbps / 1000).ToString("0.00") + " Gbps";
        }

        private void SetVital(TextBlock val, Controls.Sparkline graph, Ellipse dot,
            double current, double[] history, Func<double, string> format, DotColor? dotOverride = null)
        {
            if (double.IsNaN(current))
            {
                val.Text = "—";
                SetDot(dot, DotColor.Gray);
            }
            else
            {
                val.Text = format(current);
                SetDot(dot, dotOverride ?? (current >= 90 ? DotColor.Red : current >= 75 ? DotColor.Amber : DotColor.Green));
            }
            graph.Values = history;
        }

        // ---------- Health ----------

        private async Task LoadHealthAsync()
        {
            var batteryTask = _health.GetBatteryInfoAsync();
            var disksTask = _health.GetDiskHealthAsync();
            var updatesTask = _health.GetWindowsUpdateInfoAsync();

            var battery = await batteryTask;
            if (battery.HasBattery)
            {
                HealthBatteryEmpty.Visibility = Visibility.Collapsed;
                HealthBatteryFacts.Visibility = Visibility.Visible;
                if (!double.IsNaN(battery.HealthPercent))
                {
                    HealthBatteryPct.Text = battery.HealthPercent.ToString("0") + "%";
                    HealthBatteryBar.Value = Math.Max(0, Math.Min(100, battery.HealthPercent));
                    SetDot(HealthBatteryDot, battery.HealthPercent >= 80 ? DotColor.Green
                        : battery.HealthPercent >= 60 ? DotColor.Amber : DotColor.Red);
                }
                else
                {
                    HealthBatteryPct.Text = "—";
                    SetDot(HealthBatteryDot, DotColor.Gray);
                }
                HealthBatteryDesign.Text = battery.DesignCapacityMwh > 0
                    ? (battery.DesignCapacityMwh / 1000.0).ToString("0.0") + " Wh" : "—";
                HealthBatteryFull.Text = battery.FullChargeCapacityMwh > 0
                    ? (battery.FullChargeCapacityMwh / 1000.0).ToString("0.0") + " Wh" : "—";
                HealthBatteryCycles.Text = battery.CycleCount >= 0 ? battery.CycleCount.ToString() : "—";
                string charge = battery.ChargePercent >= 0 ? battery.ChargePercent + "%" : "—";
                if (!string.IsNullOrEmpty(battery.PowerState)) charge += " · " + battery.PowerState;
                HealthBatteryCharge.Text = charge;
            }
            else
            {
                HealthBatteryFacts.Visibility = Visibility.Collapsed;
                HealthBatteryEmpty.Visibility = Visibility.Visible;
                HealthBatteryPct.Text = "—";
                SetDot(HealthBatteryDot, DotColor.Gray);
            }

            var disks = await disksTask;
            HealthDisksPanel.Children.Clear();
            if (disks.Count == 0)
            {
                HealthDisksPanel.Children.Add(new TextBlock
                {
                    Text = "Couldn't read disk health on this machine.",
                    Style = (Style)FindResource("Muted12")
                });
            }
            foreach (var d in disks)
            {
                var dot = new Ellipse { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center };
                bool healthy = d.HealthStatus.Equals("Healthy", StringComparison.OrdinalIgnoreCase);
                bool worn = d.WearPercent >= 80;
                dot.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(
                    !healthy || worn ? "#EF5350" : d.WearPercent >= 50 ? "#FFA726" : "#4CAF50"));

                var name = new TextBlock
                {
                    Text = d.Name,
                    FontSize = 13,
                    FontWeight = FontWeights.SemiBold,
                    TextWrapping = TextWrapping.Wrap
                };
                var sub = new TextBlock
                {
                    FontSize = 11,
                    Foreground = (Brush)FindResource("DkMutedBrush"),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 2, 0, 0)
                };
                var parts = new List<string>();
                if (!string.IsNullOrWhiteSpace(d.MediaType) && !d.MediaType.Equals("Unspecified", StringComparison.OrdinalIgnoreCase))
                    parts.Add(d.MediaType);
                parts.Add(healthy ? "Healthy" : d.HealthStatus);
                if (d.SizeBytes > 0) parts.Add((d.SizeBytes / 1073741824.0).ToString("0") + " GB");
                if (d.WearPercent >= 0) parts.Add("Wear " + d.WearPercent + "%");
                if (d.TemperatureC >= 0) parts.Add(d.TemperatureC + "°C");
                sub.Text = string.Join(" · ", parts);

                var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
                row.Children.Add(dot);
                var textCol = new StackPanel { Margin = new Thickness(8, 0, 0, 0) };
                textCol.Children.Add(name);
                textCol.Children.Add(sub);
                row.Children.Add(textCol);
                HealthDisksPanel.Children.Add(row);
            }

            var updates = await updatesTask;
            if (updates.Checked)
            {
                if (updates.PendingCount == 0)
                {
                    HealthUpdatesVal.Text = "Up to date";
                    SetDot(HealthUpdatesDot, DotColor.Green);
                }
                else
                {
                    HealthUpdatesVal.Text = updates.PendingCount + " pending";
                    SetDot(HealthUpdatesDot, DotColor.Amber);
                }
            }
            else
            {
                HealthUpdatesVal.Text = "Couldn't check";
                SetDot(HealthUpdatesDot, DotColor.Gray);
            }
        }

        private bool _maintRunning;

        private void SetMaintRunning(bool running)
        {
            _maintRunning = running;
            MaintRetrimBtn.IsEnabled = !running;
            MaintDismBtn.IsEnabled = !running;
            MaintSfcBtn.IsEnabled = !running;
        }

        private async void MaintRetrim_Click(object sender, RoutedEventArgs e)
        {
            if (_maintRunning) return;
            SetMaintRunning(true);
            MaintRetrimStatus.Text = "Running…";
            try
            {
                var r = await _health.RunRetrimAsync();
                MaintRetrimStatus.Text = r.Summary;
            }
            catch (Exception ex) { MaintRetrimStatus.Text = "Failed: " + ex.Message; }
            finally { SetMaintRunning(false); }
        }

        private async void MaintDism_Click(object sender, RoutedEventArgs e)
        {
            if (_maintRunning) return;
            SetMaintRunning(true);
            MaintDismStatus.Text = "Running — this can take several minutes…";
            try
            {
                var r = await _health.RunComponentCleanupAsync();
                MaintDismStatus.Text = r.Summary;
            }
            catch (Exception ex) { MaintDismStatus.Text = "Failed: " + ex.Message; }
            finally { SetMaintRunning(false); }
        }

        private async void MaintSfc_Click(object sender, RoutedEventArgs e)
        {
            if (_maintRunning) return;
            SetMaintRunning(true);
            MaintSfcStatus.Text = "Running — this can take several minutes…";
            try
            {
                var r = await _health.RunSfcAsync();
                MaintSfcStatus.Text = r.Summary;
            }
            catch (Exception ex) { MaintSfcStatus.Text = "Failed: " + ex.Message; }
            finally { SetMaintRunning(false); }
        }

        private async Task LoadDashboardAsync(bool quiet = false)
        {
            if (_dashboardLoading) return;
            _dashboardLoading = true;
            try
            {
                // Top process lists refresh on their own slower cadence so the
                // 1-second snapshot loop stays light (GPU sampling takes ~2s).
                if ((DateTime.UtcNow - _topProcsAt).TotalSeconds >= 10)
                {
                    _topProcsAt = DateTime.UtcNow;
                    _ = RefreshTopProcsAsync();
                }
                if (!quiet)
                {
                    SetStatus("Reading system info...");
                    DashCpu.Text = "Reading…";
                    DashGpu.Text = "Reading…";
                }
                var snap = await _diag.GetDashboardSnapshotAsync();

                DashCpu.Text = string.IsNullOrWhiteSpace(snap.Cpu) ? "Unknown" : snap.Cpu;
                DashGpu.Text = string.IsNullOrWhiteSpace(snap.Gpu) ? "Unknown" : snap.Gpu;

                if (snap.DiskTotalGb > 0)
                {
                    double used = Math.Max(0, snap.DiskTotalGb - snap.DiskFreeGb);
                    double pct = used / snap.DiskTotalGb * 100;
                    double freePct = 100 - pct;
                    DashDisk.Text = snap.DiskFreeGb.ToString("0.0") + " GB free of " + snap.DiskTotalGb.ToString("0.0") + " GB";
                    DashDiskBar.Value = pct;
                    DashDiskSub.Text = pct.ToString("0") + "% used";
                    SetDot(DashDiskDot, freePct <= 10 ? DotColor.Red : freePct <= 20 ? DotColor.Amber : DotColor.Green);
                }
                else
                {
                    DashDisk.Text = "Unknown";
                    SetDot(DashDiskDot, DotColor.Gray);
                }

                DashUptime.Text = string.IsNullOrWhiteSpace(snap.Uptime) ? "Unknown" : snap.Uptime;
                DashBuild.Text = string.IsNullOrWhiteSpace(snap.WindowsBuild)
                    ? "" : "Windows build " + snap.WindowsBuild;

                if (snap.PendingReboot)
                {
                    DashReboot.Text = "Restart pending";
                    SetDot(DashRebootDot, DotColor.Red);
                    RebootBanner.Visibility = Visibility.Visible;
                }
                else
                {
                    DashReboot.Text = "No restart needed";
                    SetDot(DashRebootDot, DotColor.Green);
                    RebootBanner.Visibility = Visibility.Collapsed;
                }

                if (!quiet) SetStatus("Ready.");
            }
            catch (Exception ex)
            {
                if (!quiet)
                {
                    SetStatus("System info unavailable: " + ex.Message);
                    DashCpu.Text = "Unavailable";
                    DashGpu.Text = "Unavailable";
                }
            }
            finally
            {
                _dashboardLoading = false;
            }
        }

        private enum DotColor { Green, Amber, Red, Blue, Gray }

        private static void SetDot(Ellipse dot, DotColor color)
        {
            string hex = color == DotColor.Green ? "#4CAF50"
                : color == DotColor.Amber ? "#FFA726"
                : color == DotColor.Red ? "#EF5350"
                : color == DotColor.Blue ? "#42A5F5" : "#616161";
            dot.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        }

        private void DashRefresh_Click(object sender, RoutedEventArgs e)
        {
            _ = LoadDashboardAsync();
        }

        // ---------- Top processes (Home): memory / CPU / GPU ----------

        private DateTime _topProcsAt = DateTime.MinValue;

        private async Task RefreshTopProcsAsync()
        {
            await Task.WhenAll(RefreshTopMemAsync(), RefreshTopCpuAsync(), RefreshTopGpuAsync());
        }

        private async Task RefreshTopMemAsync()
        {
            List<(string name, double v)> items;
            try
            {
                var r = await Task.Run(() => PowerShellRunner.RunScript(
                    "Get-Process -ErrorAction SilentlyContinue | Group-Object ProcessName | " +
                    "ForEach-Object { [pscustomobject]@{ N=$_.Name; MB=[math]::Round((($_.Group | " +
                    "Measure-Object WorkingSet64 -Sum).Sum) / 1MB) } } | " +
                    "Sort-Object MB -Descending | Select-Object -First 6 | " +
                    "ForEach-Object { $_.N + '|' + $_.MB }", 2));
                items = ParseProcLines(r.Output);
            }
            catch { return; }
            if (items.Count == 0) return; // keep the old list on failure
            FillProcPanel(TopMemPanel, items, v =>
                v >= 1024 ? (v / 1024).ToString("0.0") + " GB" : ((int)v).ToString("N0") + " MB");
        }

        private async Task RefreshTopCpuAsync()
        {
            List<(string name, double v)> items;
            try
            {
                var r = await Task.Run(() => PowerShellRunner.RunScript(
                    "$c=$env:NUMBER_OF_PROCESSORS; $a=@{}; " +
                    "Get-Process -ErrorAction SilentlyContinue | ForEach-Object { " +
                    "$a[$_.Id]=@($_.ProcessName,$_.TotalProcessorTime.TotalMilliseconds) }; " +
                    "Start-Sleep -Milliseconds 800; " +
                    "Get-Process -ErrorAction SilentlyContinue | ForEach-Object { " +
                    "if ($a.ContainsKey($_.Id)) { " +
                    "$d=$_.TotalProcessorTime.TotalMilliseconds - $a[$_.Id][1]; " +
                    "if ($d -gt 0) { [pscustomobject]@{ N=$_.ProcessName; P=$d/800/$c*100 } } } } | " +
                    "Group-Object N | ForEach-Object { [pscustomobject]@{ N=$_.Name; " +
                    "P=[math]::Round(($_.Group | Measure-Object P -Sum).Sum,1) } } | " +
                    "Sort-Object P -Descending | Select-Object -First 6 | " +
                    "ForEach-Object { $_.N + '|' + $_.P }", 2));
                items = ParseProcLines(r.Output);
            }
            catch { return; }
            if (items.Count == 0) return; // keep the old list on failure
            FillProcPanel(TopCpuPanel, items, v => v.ToString("0.0") + "%");
        }

        private bool _topGpuHasData;

        private async Task RefreshTopGpuAsync()
        {
            List<(string name, double v)> items;
            try
            {
                var r = await Task.Run(() => PowerShellRunner.RunScript(
                    "$s=(Get-Counter '\\GPU Engine(*)\\Utilization Percentage' " +
                    "-SampleInterval 1 -MaxSamples 2 -ErrorAction SilentlyContinue).CounterSamples | " +
                    "Where-Object { $_.Path -match 'pid_(\\d+)' } | " +
                    "Group-Object { ([regex]::Match($_.Path,'pid_(\\d+)')).Groups[1].Value } | " +
                    "ForEach-Object { [pscustomobject]@{ Pid=$_.Name; " +
                    "Pct=($_.Group | Measure-Object CookedValue -Average).Average } } | " +
                    "Where-Object { $_.Pct -gt 0.5 } | Sort-Object Pct -Descending | " +
                    "Select-Object -First 6; $p=@{}; " +
                    "Get-Process -ErrorAction SilentlyContinue | " +
                    "ForEach-Object { $p[$_.Id]=$_.ProcessName }; " +
                    "foreach ($x in $s) { $id=[int]$x.Pid; " +
                    "if ($p.ContainsKey($id)) { $p[$id] + '|' + [math]::Round($x.Pct,1) } }", 2));
                items = ParseProcLines(r.Output);
            }
            catch { return; }
            if (items.Count == 0)
            {
                if (!_topGpuHasData)
                {
                    TopGpuPanel.Children.Clear();
                    TopGpuPanel.Children.Add(new TextBlock
                    {
                        Text = "No per-process GPU data on this machine.",
                        Style = (Style)FindResource("Muted12")
                    });
                }
                return;
            }
            _topGpuHasData = true;
            FillProcPanel(TopGpuPanel, items, v => v.ToString("0.0") + "%");
        }

        private static List<(string name, double v)> ParseProcLines(string output)
        {
            var items = new List<(string name, double v)>();
            foreach (var line in (output ?? "").Split(
                new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var p = line.Trim().Split('|');
                if (p.Length == 2 && double.TryParse(p[1], out double v) && v > 0)
                    items.Add((p[0], v));
            }
            return items;
        }

        private void FillProcPanel(StackPanel panel, List<(string name, double v)> items,
            Func<double, string> format)
        {
            panel.Children.Clear();
            double max = items.Max(i => i.v);
            var muted = (Brush)FindResource("DkMutedBrush");
            var normal = (Brush)FindResource("DkTextBrush");
            var barStyle = (Style)FindResource("RoundProgress");
            foreach (var item in items)
            {
                var row = new StackPanel { Margin = new Thickness(0, 0, 0, 6) };
                var top = new DockPanel();
                var right = new TextBlock
                {
                    Foreground = muted,
                    FontSize = 11,
                    VerticalAlignment = VerticalAlignment.Center,
                    Text = format(item.v)
                };
                right.SetValue(DockPanel.DockProperty, Dock.Right);
                top.Children.Add(right);
                top.Children.Add(new TextBlock
                {
                    Text = item.name,
                    Foreground = normal,
                    FontSize = 12,
                    TextTrimming = TextTrimming.CharacterEllipsis
                });
                row.Children.Add(top);
                row.Children.Add(new ProgressBar
                {
                    Style = barStyle,
                    Height = 4,
                    Minimum = 0,
                    Maximum = 100,
                    Value = max > 0 ? item.v / max * 100 : 0,
                    Margin = new Thickness(0, 3, 0, 0)
                });
                panel.Children.Add(row);
            }
        }

        // ---------- Game ping test (Network) ----------

        private async void PingTest_Click(object sender, RoutedEventArgs e)
        {
            PingResultsPanel.Children.Clear();
            PingResultsPanel.Children.Add(new TextBlock
            {
                Text = "Pinging game servers (takes ~20 seconds)…",
                Style = (Style)FindResource("Muted12")
            });
            SetStatus("Pinging game servers...");
            List<PingTargetResult> results;
            try
            {
                results = await _diag.PingGameServersAsync();
            }
            catch (Exception ex)
            {
                SetStatus("Ping test failed: " + ex.Message);
                return;
            }
            PingResultsPanel.Children.Clear();
            var muted = (Brush)FindResource("DkMutedBrush");
            var normal = (Brush)FindResource("DkTextBrush");
            foreach (var pr in results)
            {
                var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
                var right = new TextBlock
                {
                    FontSize = 12,
                    VerticalAlignment = VerticalAlignment.Center
                };
                right.SetValue(DockPanel.DockProperty, Dock.Right);
                if (pr.AvgMs < 0)
                {
                    right.Text = "no reply";
                    right.Foreground = muted;
                }
                else
                {
                    right.Text = pr.AvgMs + " ms" +
                        (pr.LossPct > 0 ? " • " + pr.LossPct + "% loss" : "");
                    string hex = pr.AvgMs < 50 ? "#4CAF50"
                        : pr.AvgMs < 120 ? "#FFA726" : "#EF5350";
                    right.Foreground =
                        new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
                }
                row.Children.Add(right);
                row.Children.Add(new TextBlock
                {
                    Text = pr.Name,
                    Foreground = normal,
                    FontSize = 13
                });
                PingResultsPanel.Children.Add(row);
            }
            SetStatus("Ping test complete.");
        }

        // ---------- Game release feed (right rail) ----------

        private void BuildGameFeed()
        {
            try
            {
                var games = _games.LoadUpcoming();
                GameFeedPanel.Children.Clear();
                GameFeedCount.Text = games.Count == 1 ? "1 game on the way"
                    : games.Count + " games on the way";
                if (games.Count == 0)
                {
                    GameFeedPanel.Children.Add(new TextBlock
                    {
                        Text = "No upcoming releases in the feed.",
                        FontSize = 12,
                        Foreground = (Brush)FindResource("DkMutedBrush"),
                        TextWrapping = TextWrapping.Wrap
                    });
                    return;
                }
                var muted = (Brush)FindResource("DkMutedBrush");
                var accent = (Brush)FindResource("DkAccentBrush");
                string lastMonth = null;
                for (int i = 0; i < games.Count; i++)
                {
                    var g = games[i];
                    string month = g.Date.ToString("MMMM yyyy").ToUpperInvariant();
                    if (month != lastMonth)
                    {
                        lastMonth = month;
                        GameFeedPanel.Children.Add(new TextBlock
                        {
                            Text = month,
                            FontSize = 11,
                            FontWeight = FontWeights.SemiBold,
                            Foreground = muted,
                            Margin = new Thickness(0, i == 0 ? 0 : 14, 0, 6)
                        });
                    }
                    var card = new Border
                    {
                        Style = (Style)FindResource("Card"),
                        Margin = new Thickness(0, 0, 0, 8),
                        Padding = new Thickness(12)
                    };
                    var stack = new StackPanel();
                    stack.Children.Add(new TextBlock
                    {
                        Text = g.Title,
                        FontSize = 13,
                        FontWeight = FontWeights.SemiBold,
                        TextWrapping = TextWrapping.Wrap
                    });
                    var dateRow = new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Margin = new Thickness(0, 4, 0, 0)
                    };
                    dateRow.Children.Add(new TextBlock
                    {
                        Text = g.Date.ToString("MMM d"),
                        FontSize = 12,
                        FontWeight = FontWeights.SemiBold,
                        Foreground = accent
                    });
                    dateRow.Children.Add(new TextBlock
                    {
                        Text = "  ·  " + GameFeedService.RelativeLabel(g.Date),
                        FontSize = 12,
                        Foreground = muted
                    });
                    stack.Children.Add(dateRow);
                    if (!string.IsNullOrWhiteSpace(g.Platforms))
                    {
                        stack.Children.Add(new TextBlock
                        {
                            Text = g.Platforms,
                            FontSize = 11,
                            Foreground = muted,
                            TextWrapping = TextWrapping.Wrap,
                            Margin = new Thickness(0, 2, 0, 0)
                        });
                    }
                    card.Child = stack;
                    GameFeedPanel.Children.Add(card);
                }
            }
            catch
            {
                // The sidebar must never break the app.
            }
        }

        private async void DashQuickClean_Click(object sender, RoutedEventArgs e)
        {
            ModeQuick.IsChecked = true;
            NavCleanup.IsChecked = true;
            await AnalyzeAsync();
            await CleanSelectedAsync();
        }

        private async void DashFullClean_Click(object sender, RoutedEventArgs e)
        {
            ModeFull.IsChecked = true;
            NavCleanup.IsChecked = true;
            await AnalyzeAsync();
        }

        private void DashDebloat_Click(object sender, RoutedEventArgs e)
        {
            NavDebloat.IsChecked = true;
        }

        private async void DashSpeedTest_Click(object sender, RoutedEventArgs e)
        {
            NavNetwork.IsChecked = true;
            await RunSpeedTestAsync();
        }

        // ---------- Tweaks (toggle switches) ----------

        private void BuildTweakTab(StackPanel panel, string[] categories)
        {
            _buildingUi = true;
            try
            {
                panel.Children.Clear();
                foreach (string cat in categories)
                {
                    var tweaks = _tweakDefs.Where(t => t.Category == cat).ToList();
                    if (tweaks.Count == 0) continue;

                    var group = new GroupBox
                    {
                        Header = cat,
                        Margin = new Thickness(0, 0, 0, 12),
                        Padding = new Thickness(12, 8, 12, 8)
                    };
                    var stack = new StackPanel();
                    foreach (var tw in tweaks)
                    {
                        TweakState state = _tweaks.GetState(tw);
                        var row = new Grid { Margin = new Thickness(0, 8, 0, 8) };
                        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                        var left = new StackPanel();
                        var nameRow = new StackPanel
                        {
                            Orientation = Orientation.Horizontal,
                            VerticalAlignment = VerticalAlignment.Center
                        };
                        nameRow.Children.Add(new TextBlock
                        {
                            Text = tw.Name,
                            FontWeight = FontWeights.SemiBold,
                            TextWrapping = TextWrapping.Wrap,
                            VerticalAlignment = VerticalAlignment.Center
                        });
                        if (tw.Recommended)
                        {
                            nameRow.Children.Add(new Border
                            {
                                Background = (Brush)FindResource("DkAccentBrush"),
                                CornerRadius = new CornerRadius(4),
                                Padding = new Thickness(8, 2, 8, 2),
                                Margin = new Thickness(8, 0, 0, 0),
                                VerticalAlignment = VerticalAlignment.Center,
                                Child = new TextBlock
                                {
                                    Text = "★ Recommended",
                                    FontSize = 11,
                                    FontWeight = FontWeights.SemiBold,
                                    Foreground = Brushes.White,
                                    VerticalAlignment = VerticalAlignment.Center
                                }
                            });
                        }
                        left.Children.Add(nameRow);
                        left.Children.Add(new TextBlock
                        {
                            Text = tw.Description + (tw.RequiresReboot ? " (Restart required.)" : ""),
                            TextWrapping = TextWrapping.Wrap,
                            Foreground = (Brush)FindResource("DkMutedBrush"),
                            Margin = new Thickness(0, 2, 0, 0),
                            FontSize = 12
                        });
                        row.Children.Add(left);

                        var toggle = new CheckBox
                        {
                            Style = (Style)FindResource("ToggleSwitch"),
                            Tag = tw,
                            VerticalAlignment = VerticalAlignment.Center,
                            Margin = new Thickness(16, 0, 0, 0)
                        };
                        toggle.IsChecked = state == TweakState.Applied;
                        if (state == TweakState.Unknown)
                        {
                            toggle.IsEnabled = false;
                            string why = _tweaks.GetStateError(tw);
                            toggle.ToolTip = "Could not read current state" +
                                (string.IsNullOrEmpty(why) ? "" : ": " + why);
                        }
                        toggle.Checked += TweakBox_Toggled;
                        toggle.Unchecked += TweakBox_Toggled;
                        Grid.SetColumn(toggle, 1);
                        row.Children.Add(toggle);

                        stack.Children.Add(row);
                        if (tw != tweaks[tweaks.Count - 1])
                            stack.Children.Add(new Separator
                            {
                                Background = (Brush)FindResource("DkCardBorderBrush"),
                                Margin = new Thickness(0, 2, 0, 2)
                            });
                    }
                    group.Content = stack;
                    panel.Children.Add(group);
                }
            }
            finally
            {
                _buildingUi = false;
            }
        }

        private void TweakBox_Toggled(object sender, RoutedEventArgs e)
        {
            if (_buildingUi) return;
            var cb = (CheckBox)sender;
            var tw = (TweakDefinition)cb.Tag;
            try
            {
                if (cb.IsChecked == true)
                {
                    _tweaks.Apply(tw);
                    LedgerNoteApplied(tw);
                }
                else
                {
                    _tweaks.Revert(tw);
                    LedgerNoteReverted(tw);
                }
                RefreshDriftButton();
                SetStatus(tw.Name + (cb.IsChecked == true ? " applied." : " reverted.")
                    + (tw.RequiresReboot ? " Restart Windows to take full effect." : ""));
            }
            catch (Exception ex)
            {
                _buildingUi = true;
                try { cb.IsChecked = _tweaks.GetState(tw) == TweakState.Applied; }
                finally { _buildingUi = false; }
                SetStatus("Error: " + ex.Message);
                MessageBox.Show("Failed to change setting:\n" + ex.Message,
                    "Sarah's Toolkit", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // ---------- Tweak ledger: the config file remembers your toggles ----------

        private void LedgerNoteApplied(TweakDefinition tw)
        {
            var list = _settings.Settings.AppliedTweaks;
            if (list == null)
            {
                list = new List<string>();
                _settings.Settings.AppliedTweaks = list;
            }
            if (!list.Contains(tw.Id))
            {
                list.Add(tw.Id);
                _settings.Save();
            }
        }

        private void LedgerNoteReverted(TweakDefinition tw)
        {
            var list = _settings.Settings.AppliedTweaks;
            if (list != null && list.Remove(tw.Id))
                _settings.Save();
        }

        // First run of this feature: record what's currently applied, so drift
        // caused later (Windows updates, other tools) can be detected and undone.
        private void BackfillLedgerIfNeeded()
        {
            if (_settings.Settings.AppliedTweaks != null) return;
            // Never cement defaults over a config file we failed to read —
            // that blanked the whole config after a torn write.
            if (_settings.HadFile && !_settings.LoadedOk) return;
            var ids = new List<string>();
            foreach (var tw in _tweakDefs)
            {
                try { if (_tweaks.GetState(tw) == TweakState.Applied) ids.Add(tw.Id); }
                catch { }
            }
            _settings.Settings.AppliedTweaks = ids;
            _settings.Save();
        }

        // Tweaks the ledger says are applied but the registry says aren't.
        // Unreadable states don't count — only definite reverts.
        private List<TweakDefinition> FindDriftedTweaks()
        {
            var result = new List<TweakDefinition>();
            var ledger = _settings.Settings.AppliedTweaks;
            if (ledger == null) return result;
            foreach (string id in ledger)
            {
                var tw = _tweakDefs.FirstOrDefault(t => t.Id == id);
                if (tw == null) continue;
                try { if (_tweaks.GetState(tw) == TweakState.NotApplied) result.Add(tw); }
                catch { }
            }
            return result;
        }

        private void RefreshDriftButton()
        {
            _drifted = FindDriftedTweaks();
            if (_drifted.Count == 0)
            {
                ReapplyDriftButton.Visibility = Visibility.Collapsed;
                return;
            }
            ReapplyDriftButton.Content = "Re-apply my tweaks (" + _drifted.Count + ")";
            ReapplyDriftButton.Visibility = Visibility.Visible;
        }

        private void ReapplyDrift_Click(object sender, RoutedEventArgs e)
        {
            int applied = 0, failed = 0;
            foreach (var tw in _drifted.ToList())
            {
                try { _tweaks.Apply(tw); applied++; }
                catch { failed++; }
            }
            BuildTweakTab(OptimizePanel, new[] { "Privacy", "Gaming", "Performance" });
            BuildTweakTab(CustomizePanel, new[] { "Theme", "Taskbar", "Explorer", "Start" });
            BuildTweakTab(SecurityTweaksPanel, new[] { "Security" });
            RefreshDriftButton();
            SetStatus("Re-applied " + applied + " tweak(s)" +
                (failed > 0 ? ", " + failed + " failed." : "."));
        }

        // ---------- Presets (visual cards) ----------

        private static readonly Dictionary<string, string> PresetDotColors =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "Balanced", "#4CAF50" },
                { "Gaming", "#42A5F5" },
                { "Max Performance", "#FFA726" }
            };

        private void BuildPresetsTab()
        {
            PresetsPanel.Children.Clear();
            _presetRatingLabels.Clear();
            foreach (var preset in _presetDefs)
            {
                string dotHex = PresetDotColors.TryGetValue(preset.Name, out var hex)
                    ? hex : "#8B5CF6";

                var card = new Border
                {
                    Style = (Style)FindResource("Card"),
                    Margin = new Thickness(0, 0, 12, 12)
                };
                var stack = new StackPanel();

                var header = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Margin = new Thickness(0, 0, 0, 8)
                };
                header.Children.Add(new Ellipse
                {
                    Width = 12, Height = 12,
                    Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(dotHex)),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 10, 0)
                });
                header.Children.Add(new TextBlock
                {
                    Text = preset.Name,
                    FontSize = 15,
                    FontWeight = FontWeights.SemiBold,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextWrapping = TextWrapping.Wrap
                });
                stack.Children.Add(header);

                stack.Children.Add(new TextBlock
                {
                    Text = preset.Description,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = (Brush)FindResource("DkMutedBrush"),
                    Margin = new Thickness(0, 0, 0, 10),
                    FontSize = 12,
                    MinHeight = 48
                });

                var ratingLabel = new TextBlock
                {
                    FontWeight = FontWeights.SemiBold,
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 10),
                    MinHeight = 36
                };
                stack.Children.Add(ratingLabel);

                if (PresetNeedsReboot(preset))
                {
                    stack.Children.Add(new TextBlock
                    {
                        Text = "Restart Windows afterwards for full effect.",
                        Foreground = (Brush)FindResource("DkMutedBrush"),
                        FontSize = 12,
                        Margin = new Thickness(0, 0, 0, 10)
                    });
                }

                var applyBtn = new Button
                {
                    Content = "Apply preset",
                    Style = (Style)FindResource("AccentButton"),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Tag = preset
                };
                applyBtn.Click += PresetApply_Click;
                stack.Children.Add(applyBtn);

                card.Child = stack;
                PresetsPanel.Children.Add(card);
                _presetRatingLabels[preset.Id] = ratingLabel;
            }
            RefreshPresetRatings();
        }

        private bool PresetNeedsReboot(PresetDefinition preset)
        {
            var ids = (preset.Tweaks ?? Enumerable.Empty<string>())
                .Concat(PresetService.ResolveRevertIds(preset, _tweakDefs));
            return ids.Select(id => _tweakDefs.FirstOrDefault(t => t.Id == id))
                .Any(tw => tw != null && tw.RequiresReboot);
        }

        private void RefreshPresetRatings()
        {
            if (_presetRatingLabels.Count == 0) return;
            foreach (var preset in _presetDefs)
            {
                if (!_presetRatingLabels.TryGetValue(preset.Id, out var label)) continue;
                label.Text = ImpactRatingText(_presets.Evaluate(preset, _tweakDefs, _tweaks));
            }
        }

        // The rating is measured live on this PC: the share of the preset's
        // improvements that are not active yet. More pending = more it helps.
        private static string ImpactRatingText(PresetImpact impact)
        {
            if (impact.Measurable == 0)
                return "☆☆☆☆☆  Can't measure on this PC";
            if (impact.Pending == 0)
                return "✓ Already applied — nothing to gain right now";
            double frac = (double)impact.Pending / impact.Measurable;
            string stars = frac >= 0.8 ? "★★★★★"
                : frac >= 0.6 ? "★★★★☆"
                : frac >= 0.4 ? "★★★☆☆"
                : frac >= 0.2 ? "★★☆☆☆"
                : "★☆☆☆☆";
            string level = frac >= 0.8 ? "very high impact"
                : frac >= 0.6 ? "high impact"
                : frac >= 0.4 ? "moderate impact"
                : frac >= 0.2 ? "low impact"
                : "minimal impact";
            return stars + "  " + impact.Pending + " of " + impact.Measurable +
                " changes pending — " + level;
        }

        private void PresetApply_Click(object sender, RoutedEventArgs e)
        {
            var preset = (PresetDefinition)((Button)sender).Tag;
            var impact = _presets.Evaluate(preset, _tweakDefs, _tweaks);
            if (impact.Pending == 0)
            {
                SetStatus(preset.Name + " preset is already fully applied.");
                return;
            }
            bool needsReboot = PresetNeedsReboot(preset);
            var confirm = MessageBox.Show(
                "Apply the " + preset.Name + " preset?\n\nThis will change " +
                impact.Pending + " setting(s)." +
                (needsReboot ? "\n\nRestart Windows afterwards for full effect." : ""),
                "Sarah's Toolkit", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes) return;

            int applied = 0, reverted = 0, failed = 0;
            foreach (string id in preset.Tweaks ?? Enumerable.Empty<string>())
            {
                var tw = _tweakDefs.FirstOrDefault(t => t.Id == id);
                if (tw == null) continue;
                if (_tweaks.GetState(tw) != TweakState.NotApplied) continue;
                try { _tweaks.Apply(tw); LedgerNoteApplied(tw); applied++; }
                catch { failed++; }
            }
            foreach (string id in PresetService.ResolveRevertIds(preset, _tweakDefs))
            {
                var tw = _tweakDefs.FirstOrDefault(t => t.Id == id);
                if (tw == null) continue;
                if (_tweaks.GetState(tw) != TweakState.Applied) continue;
                try { _tweaks.Revert(tw); LedgerNoteReverted(tw); reverted++; }
                catch { failed++; }
            }
            var changedParts = new List<string>();
            if (applied > 0) changedParts.Add(applied + " applied");
            if (reverted > 0) changedParts.Add(reverted + " reverted");
            string changed = changedParts.Count > 0 ? string.Join(", ", changedParts) : "no changes";
            SetStatus(preset.Name + " preset: " + changed +
                (failed > 0 ? ", " + failed + " failed." : ".") +
                (needsReboot ? " Restart Windows for full effect." : ""));
            // Re-sync the Optimize/Customize/Security toggles and the ratings.
            BuildTweakTab(OptimizePanel, new[] { "Privacy", "Gaming", "Performance" });
            BuildTweakTab(CustomizePanel, new[] { "Theme", "Taskbar", "Explorer", "Start" });
            BuildTweakTab(SecurityTweaksPanel, new[] { "Security" });
            RefreshDriftButton();
            RefreshPresetRatings();
        }

        // ---------- Cleanup: analyze + determinate clean + summary ----------

        private void CleanMode_Changed(object sender, RoutedEventArgs e)
        {
            if (ModeHint == null) return;
            bool quick = ModeQuick.IsChecked == true;
            ModeHint.Text = quick
                ? "Quick Clean targets the fast, safe temp locations. Full Clean covers every category below."
                : "Full Clean covers every category below. This takes longer than Quick Clean.";
            // Mode changed -> any previous analysis no longer applies.
            _analyses.Clear();
            CatList.Children.Clear();
            CatCard.Visibility = Visibility.Collapsed;
            SummaryCard.Visibility = Visibility.Collapsed;
            CleanButton.IsEnabled = false;
            FreedLabel.Text = "";
        }

        private async void Analyze_Click(object sender, RoutedEventArgs e)
        {
            await AnalyzeAsync();
        }

        private async Task AnalyzeAsync()
        {
            AnalyzeButton.IsEnabled = false;
            CatCard.Visibility = Visibility.Collapsed;
            SummaryCard.Visibility = Visibility.Collapsed;
            CleanButton.IsEnabled = false;
            FreedLabel.Text = "";
            CatList.Children.Clear();
            _analyses.Clear();

            bool quick = ModeQuick.IsChecked == true;
            var cats = _cleanup.LoadCategories().Where(c => !quick || c.QuickClean).ToList();
            SetStatus("Analyzing " + cats.Count + " categories...");

            long totalBytes = 0, totalFiles = 0;
            var muted = (Brush)FindResource("DkMutedBrush");
            foreach (var cat in cats)
            {
                ScanResult sr;
                try { sr = await _cleanup.ScanDetailedAsync(cat); }
                catch { sr = new ScanResult(); }

                var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var cb = new CheckBox { Margin = new Thickness(0, 4, 0, 4), VerticalAlignment = VerticalAlignment.Center };
                var content = new StackPanel();
                content.Children.Add(new TextBlock
                {
                    Text = cat.Name,
                    FontWeight = FontWeights.SemiBold,
                    TextWrapping = TextWrapping.Wrap
                });
                content.Children.Add(new TextBlock
                {
                    Text = cat.Description,
                    FontSize = 11,
                    Foreground = muted,
                    TextWrapping = TextWrapping.Wrap
                });
                cb.Content = content;
                cb.IsChecked = sr.Bytes > 0;
                cb.IsEnabled = sr.Bytes > 0;
                row.Children.Add(cb);

                row.Children.Add(new TextBlock
                {
                    Text = FormatBytes(sr.Bytes) + " · " + sr.Files + " files",
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = muted,
                    Margin = new Thickness(16, 0, 0, 0)
                });
                Grid.SetColumn(row.Children[1], 1);

                CatList.Children.Add(row);
                _analyses.Add(new CategoryAnalysis
                {
                    Cat = cat,
                    Bytes = sr.Bytes,
                    Files = sr.Files,
                    Box = cb
                });
                totalBytes += sr.Bytes;
                totalFiles += sr.Files;
            }

            AnalyzeTotal.Text = cats.Count + " categories · " + FormatBytes(totalBytes) +
                " reclaimable · " + totalFiles + " files";
            CatCard.Visibility = Visibility.Visible;
            CleanButton.IsEnabled = _analyses.Any(a => a.Bytes > 0);
            SetStatus("Analysis complete: " + FormatBytes(totalBytes) + " found.");
            AnalyzeButton.IsEnabled = true;
        }

        private async void Clean_Click(object sender, RoutedEventArgs e)
        {
            await CleanSelectedAsync();
        }

        private void CancelClean_Click(object sender, RoutedEventArgs e)
        {
            if (_cleanCts != null) _cleanCts.Cancel();
        }

        private async Task CleanSelectedAsync()
        {
            var selected = _analyses.Where(a => a.Box.IsChecked == true && a.Bytes > 0).ToList();
            if (selected.Count == 0)
            {
                MessageBox.Show("Tick at least one category with files to clean first. Press Analyze if you haven't yet.",
                    "Sarah's Toolkit", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            string mode = ModeQuick.IsChecked == true ? "Quick Clean" : "Full Clean";
            var confirm = MessageBox.Show(
                mode + " will delete temporary and cache files in " + selected.Count +
                " categor" + (selected.Count == 1 ? "y" : "ies") +
                " (" + FormatBytes(selected.Sum(a => a.Bytes)) + ").\n\nYour personal files are not touched. Continue?",
                "Sarah's Toolkit", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes) return;

            SetCleanUiRunning(true);
            _cleanCts = new CancellationTokenSource();
            SummaryCard.Visibility = Visibility.Collapsed;
            ProgressCard.Visibility = Visibility.Visible;
            CleanProgress.Value = 0;
            CleanProgress.IsIndeterminate = false;
            ProgressPct.Text = "0%";
            ProgressFile.Text = "";
            CleanLog.Items.Clear();
            CleanLog.Items.Add("=== " + mode + " ===");
            FreedLabel.Text = "";

            long totalFilesPlanned = Math.Max(1, selected.Sum(a => a.Files));
            long bytesFreed = 0, filesDeleted = 0, filesSkipped = 0;
            string currentCat = null;
            long catStartBytes = 0, catStartFiles = 0, catStartSkipped = 0;
            string catSkipKind = "";
            string topSkipKind = "";
            long topSkipped = 0;
            var perCat = new List<Tuple<string, long, long, long, string>>();

            var progress = new Progress<CleanupProgress>(p =>
            {
                if (p.Category == "Done")
                {
                    bytesFreed = p.BytesFreed;
                    filesDeleted = p.FilesDeleted;
                    filesSkipped = p.SkippedFiles;
                    ProgressStage.Text = "Finishing…";
                    return;
                }
                if (p.Category != currentCat)
                {
                    if (currentCat != null)
                    {
                        long cs = filesSkipped - catStartSkipped;
                        perCat.Add(Tuple.Create(currentCat, bytesFreed - catStartBytes,
                            filesDeleted - catStartFiles, cs, catSkipKind));
                        if (cs > topSkipped) { topSkipped = cs; topSkipKind = catSkipKind; }
                    }
                    currentCat = p.Category;
                    catStartBytes = bytesFreed;
                    catStartFiles = filesDeleted;
                    catStartSkipped = filesSkipped;
                    catSkipKind = "";
                    ProgressStage.Text = "Cleaning " + p.Category + "…";
                }
                bytesFreed = p.BytesFreed;
                filesDeleted = p.FilesDeleted;
                filesSkipped = p.SkippedFiles;
                if (!string.IsNullOrEmpty(p.SkipKind)) catSkipKind = p.SkipKind;
                double pct = Math.Min(100, (double)filesDeleted / totalFilesPlanned * 100);
                CleanProgress.Value = pct;
                ProgressPct.Text = ((int)pct) + "%";
                if (!string.IsNullOrEmpty(p.CurrentFile))
                {
                    string shown = CensorUserName(p.CurrentFile);
                    ProgressFile.Text = shown;
                    CleanLog.Items.Add(shown);
                    if (CleanLog.Items.Count > 400) CleanLog.Items.RemoveAt(0);
                    CleanLog.ScrollIntoView(CleanLog.Items[CleanLog.Items.Count - 1]);
                }
                FreedLabel.Text = FormatBytes(bytesFreed) + " freed · " + filesDeleted + " files";
            });

            try
            {
                await _cleanup.CleanAsync(selected.Select(a => a.Cat).ToList(), progress, _cleanCts.Token);
                if (currentCat != null)
                {
                    long cs = filesSkipped - catStartSkipped;
                    perCat.Add(Tuple.Create(currentCat, bytesFreed - catStartBytes,
                        filesDeleted - catStartFiles, cs, catSkipKind));
                    if (cs > topSkipped) { topSkipped = cs; topSkipKind = catSkipKind; }
                }

                CleanProgress.Value = 100;
                ProgressPct.Text = "100%";
                ProgressStage.Text = "Done";
                ProgressFile.Text = "";
                CleanLog.Items.Add("=== Done: " + FormatBytes(bytesFreed) + " freed ===");
                if (filesSkipped > 0)
                    CleanLog.Items.Add("=== " + filesSkipped + " files could not be deleted" +
                        (string.IsNullOrEmpty(topSkipKind) ? "" : " (" + topSkipKind + ")") + " ===");

                // Post-clean summary, per category.
                SummaryList.Children.Clear();
                var muted = (Brush)FindResource("DkMutedBrush");
                foreach (var pc in perCat)
                {
                    var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    row.Children.Add(new TextBlock { Text = pc.Item1, TextWrapping = TextWrapping.Wrap });
                    var amtText = FormatBytes(pc.Item2) + " · " + pc.Item3 + " files";
                    if (pc.Item4 > 0)
                        amtText += " · " + pc.Item4 + " skipped" +
                            (string.IsNullOrEmpty(pc.Item5) ? "" : " (" + pc.Item5 + ")");
                    var amt = new TextBlock
                    {
                        Text = amtText,
                        Foreground = muted,
                        Margin = new Thickness(16, 0, 0, 0)
                    };
                    Grid.SetColumn(amt, 1);
                    row.Children.Add(amt);
                    SummaryList.Children.Add(row);
                }
                SummaryTotal.Text = FormatBytes(bytesFreed) + " · " + filesDeleted + " files";
                SummaryCard.Visibility = Visibility.Visible;

                SetStatus("Clean complete: " + FormatBytes(bytesFreed) + " freed.");
                MessageBox.Show(mode + " complete.\n" + FormatBytes(bytesFreed) + " freed in " +
                    filesDeleted + " files.", "Sarah's Toolkit", MessageBoxButton.OK,
                    MessageBoxImage.Information);
                await AnalyzeAsync();
            }
            catch (OperationCanceledException)
            {
                SetStatus("Clean cancelled.");
                CleanLog.Items.Add("=== Cancelled ===");
                ProgressStage.Text = "Cancelled";
            }
            catch (Exception ex)
            {
                SetStatus("Clean failed.");
                MessageBox.Show("Clean failed:\n" + ex.Message,
                    "Sarah's Toolkit", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                SetCleanUiRunning(false);
            }
        }

        private void SetCleanUiRunning(bool running)
        {
            AnalyzeButton.IsEnabled = !running;
            CleanButton.IsEnabled = !running;
            CancelCleanButton.IsEnabled = running;
            if (running) ProgressCard.Visibility = Visibility.Visible;
        }

        // ---------- Copy buttons (paste output into Discord) ----------

        private void CopyCleanLog_Click(object sender, RoutedEventArgs e)
        {
            CopyItemsToClipboard(CleanLog.Items);
        }

        private void CopyToolsOutput_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(ToolsOutput.Text))
                Clipboard.SetText(ToolsOutput.Text);
        }

        private void CopyDevLog_Click(object sender, RoutedEventArgs e)
        {
            CopyItemsToClipboard(DevLog.Items);
        }

        private void CopyItemsToClipboard(System.Collections.IList items)
        {
            var lines = items.Cast<object>().Select(o => o == null ? "" : o.ToString());
            string text = string.Join("\r\n", lines);
            if (!string.IsNullOrEmpty(text))
                Clipboard.SetText(text);
        }

        // ---------- Debloat ----------

        private void BuildDebloatList()
        {
            DebloatPanel.Children.Clear();
            foreach (var app in _debloatApps)
            {
                var cb = new CheckBox
                {
                    Content = app.Name,
                    Tag = app,
                    Margin = new Thickness(0, 2, 0, 2),
                    IsEnabled = false
                };
                DebloatPanel.Children.Add(cb);
            }
        }

        private async void RefreshDebloat_Click(object sender, RoutedEventArgs e)
        {
            await RefreshDebloatInstalledAsync();
        }

        private async Task RefreshDebloatInstalledAsync()
        {
            SetStatus("Checking installed apps (this takes a moment)...");
            try
            {
                await _debloat.RefreshInstalledAsync(_debloatApps);
            }
            catch (Exception ex)
            {
                SetStatus("App check failed: " + ex.Message);
                return;
            }
            var normal = (Brush)FindResource("DkTextBrush");
            // Only installed apps are shown at all; anything not installed is hidden.
            DebloatPanel.Children.Clear();
            foreach (var app in _debloatApps.Where(a => a.Installed))
            {
                DebloatPanel.Children.Add(new CheckBox
                {
                    Content = app.Name,
                    Tag = app,
                    Margin = new Thickness(0, 2, 0, 2),
                    Foreground = normal
                });
            }
            int installed = _debloatApps.Count(a => a.Installed);
            int hidden = _debloatApps.Count - installed;
            if (installed == 0)
            {
                DebloatPanel.Children.Add(new TextBlock
                {
                    Text = "All good — none of the listed bloatware apps are installed on this PC.",
                    Foreground = (Brush)FindResource("DkMutedBrush"),
                    TextWrapping = TextWrapping.Wrap
                });
            }
            SetStatus(installed + " installed app(s) shown" +
                (hidden > 0 ? ", " + hidden + " not installed (hidden)" : "") + ".");
        }

        private async void RemoveDebloat_Click(object sender, RoutedEventArgs e)
        {
            var selected = new List<DebloatApp>();
            foreach (CheckBox cb in DebloatPanel.Children.OfType<CheckBox>())
            {
                var app = (DebloatApp)cb.Tag;
                if (cb.IsChecked == true && app.Installed) selected.Add(app);
            }
            if (selected.Count == 0)
            {
                MessageBox.Show("Tick at least one installed app first.",
                    "Sarah's Toolkit", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var confirm = MessageBox.Show(
                "Remove " + selected.Count + " app(s)?\n\n" +
                string.Join("\n", selected.Select(a => "• " + a.Name)) +
                "\n\nTo get one back later, reinstall it from the Microsoft Store.",
                "Sarah's Toolkit", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes) return;

            SetStatus("Removing apps...");
            PowerShellResult result;
            try
            {
                result = await _debloat.RemoveAsync(selected);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Removal failed:\n" + ex.Message,
                    "Sarah's Toolkit", MessageBoxButton.OK, MessageBoxImage.Warning);
                SetStatus("Removal failed.");
                return;
            }

            int ok = 0, fail = 0;
            var failedNames = new List<string>();
            foreach (var line in result.Output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string t = line.Trim();
                if (t.StartsWith("OK=")) ok++;
                else if (t.StartsWith("FAIL=")) { fail++; failedNames.Add(t.Substring(5)); }
            }
            MessageBox.Show("Removed: " + ok + "\nFailed: " + fail +
                (fail > 0 ? "\n\n" + string.Join("\n", failedNames) : ""),
                "Sarah's Toolkit", MessageBoxButton.OK,
                fail > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);

            BuildDebloatList();
            await RefreshDebloatInstalledAsync();
        }

        // ---------- Services ----------

        private void BuildServicesList()
        {
            ServicesPanel.Children.Clear();
            var muted = (Brush)FindResource("DkMutedBrush");
            foreach (var def in _serviceDefs)
            {
                var cb = new CheckBox
                {
                    Tag = def,
                    Margin = new Thickness(0, 4, 0, 0),
                    FontWeight = FontWeights.SemiBold,
                    IsEnabled = false
                };
                cb.Content = def.DisplayName + "  (" + def.Name + ")";
                ServicesPanel.Children.Add(cb);

                var desc = new TextBlock
                {
                    Text = def.Description,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = muted,
                    Margin = new Thickness(20, 0, 0, 2)
                };
                ServicesPanel.Children.Add(desc);
            }
        }

        private async void RefreshServices_Click(object sender, RoutedEventArgs e)
        {
            await RefreshServiceStatesAsync();
        }

        private async Task RefreshServiceStatesAsync()
        {
            SetStatus("Reading service states...");
            try
            {
                await _services.RefreshStatesAsync(_serviceDefs);
            }
            catch (Exception ex)
            {
                SetStatus("Service check failed: " + ex.Message);
                return;
            }
            var muted = (Brush)FindResource("DkMutedBrush");
            var normal = (Brush)FindResource("DkTextBrush");
            int i = 0;
            foreach (var def in _serviceDefs)
            {
                if (i >= ServicesPanel.Children.Count) break;
                var cb = (CheckBox)ServicesPanel.Children[i++];
                var desc = (TextBlock)ServicesPanel.Children[i++];
                cb.IsEnabled = def.Status != "Missing";
                cb.IsChecked = false;
                desc.Text = def.Description + "  —  Status: " + def.Status +
                            ", Startup: " + def.StartType +
                            (def.Status == "Missing" ? " (not present on this PC)" : "");
                cb.Foreground = def.StartType == "Disabled" ? muted : normal;
            }
            SetStatus(_serviceDefs.Count + " services checked.");
        }

        private List<ServiceDefinition> GetCheckedServices()
        {
            var list = new List<ServiceDefinition>();
            foreach (var child in ServicesPanel.Children)
            {
                var cb = child as CheckBox;
                if (cb != null && cb.IsChecked == true)
                    list.Add((ServiceDefinition)cb.Tag);
            }
            return list;
        }

        private async void DisableServices_Click(object sender, RoutedEventArgs e)
        {
            var selected = GetCheckedServices();
            if (selected.Count == 0)
            {
                MessageBox.Show("Tick at least one service first.",
                    "Sarah's Toolkit", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var confirm = MessageBox.Show(
                "Disable " + selected.Count + " service(s)? They will be set to Disabled and stopped.\n\n" +
                string.Join("\n", selected.Select(s => "• " + s.DisplayName)) +
                "\n\nRestore any of them later with 'Restore selected'.",
                "Sarah's Toolkit", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes) return;
            SetStatus("Disabling services...");
            PowerShellResult r;
            try { r = await _services.DisableAsync(selected); }
            catch (Exception ex)
            {
                MessageBox.Show("Failed:\n" + ex.Message,
                    "Sarah's Toolkit", MessageBoxButton.OK, MessageBoxImage.Warning);
                SetStatus("Ready.");
                return;
            }
            string line = FirstLineStartingWith(r.Output, "Done=");
            string changed = line.StartsWith("Done=") ? line.Substring(5) : "?";
            SetStatus("Service optimization done (" + changed + " changed). Reboot recommended.");
            await RefreshServiceStatesAsync();
        }

        private async void RestoreServices_Click(object sender, RoutedEventArgs e)
        {
            var selected = GetCheckedServices();
            if (selected.Count == 0)
            {
                MessageBox.Show("Tick at least one service first.",
                    "Sarah's Toolkit", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            SetStatus("Restoring services...");
            PowerShellResult r;
            try { r = await _services.RestoreAsync(selected); }
            catch (Exception ex)
            {
                MessageBox.Show("Failed:\n" + ex.Message,
                    "Sarah's Toolkit", MessageBoxButton.OK, MessageBoxImage.Warning);
                SetStatus("Ready.");
                return;
            }
            string line = FirstLineStartingWith(r.Output, "Done=");
            string changed = line.StartsWith("Done=") ? line.Substring(5) : "?";
            SetStatus("Services restored (" + changed + " changed). Reboot recommended.");
            await RefreshServiceStatesAsync();
        }

        // ---------- Tools ----------

        private async void Dns_Click(object sender, RoutedEventArgs e)
        {
            var btn = (Button)sender;
            string[] ips = ((string)btn.Tag).Split(',');
            SetStatus("Setting DNS to " + btn.Content + "...");
            try
            {
                var r = await _tools.SetDnsAsync(ips);
                SetStatus(r.Output.Contains("DNS updated") ? "DNS set to " + btn.Content + ". Cache flushed." : "DNS change attempted - check output.");
            }
            catch (Exception ex)
            {
                SetStatus("DNS change failed.");
                MessageBox.Show("DNS change failed:\n" + ex.Message,
                    "Sarah's Toolkit", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private async void AutoDns_Click(object sender, RoutedEventArgs e)
        {
            SetStatus("Pinging DNS providers to find the fastest...");
            var r = await _tools.AutoSelectDnsAsync();
            string line = FirstLineStartingWith(r.Output, "DNS auto-selected:");
            if (string.IsNullOrEmpty(line))
                line = FirstLineStartingWith(r.Output, "DNS auto-select failed");
            SetStatus(!string.IsNullOrEmpty(line) ? line : "DNS auto-select finished.");
        }

        private async void SpeedTest_Click(object sender, RoutedEventArgs e)
        {
            await RunSpeedTestAsync();
        }

        private async Task RunSpeedTestAsync()
        {
            SpeedTestButton.IsEnabled = false;
            SpeedDetails.Text = "Downloading Speedtest CLI (Ookla)…";
            GaugeValue.Text = "— Mbps";
            SetGaugeTarget(0);
            try
            {
                var r = await _tools.SpeedTestCliAsync();
                string wifi = null, result = null, url = null, error = null;
                foreach (var line in (r.Output ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (line.StartsWith("WIFI|")) wifi = line.Substring(5);
                    else if (line.StartsWith("RESULT|")) result = line.Substring(7);
                    else if (line.StartsWith("URL|")) url = line.Substring(4);
                    else if (line.StartsWith("ERROR|")) error = line.Substring(6);
                }
                if (result != null)
                {
                    var parts = result.Split('|');
                    double down = 0;
                    double.TryParse(parts[1], out down);
                    SetGaugeTarget(down);
                    GaugeValue.Text = parts[1] + " Mbps";
                    SpeedDetails.Text =
                        "ISP: " + parts[3] + "\n" +
                        "Ping: " + parts[0] + " ms\n" +
                        "Download: " + parts[1] + " Mbps\n" +
                        "Upload: " + parts[2] + " Mbps\n" +
                        (wifi ?? "").TrimEnd('\n');
                    SpeedUrl.Text = url != null ? "Full results: " + url : "";
                }
                else
                {
                    SpeedDetails.Text = "Speed test failed" + (error != null ? ": " + error + "." : ".") +
                        " Check your connection or try speedtest.net in your browser.";
                    SpeedUrl.Text = "";
                }
            }
            catch (Exception ex)
            {
                SpeedDetails.Text = "Speed test failed: " + ex.Message;
                SpeedUrl.Text = "";
            }
            SpeedTestButton.IsEnabled = true;
        }

        // ---------- Speed-test gauge ----------

        private void SetGaugeTarget(double mbps)
        {
            _gaugeTarget = Math.Max(0, mbps);
            if (_gaugeTimer == null)
            {
                _gaugeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
                _gaugeTimer.Tick += (s, e) =>
                {
                    _gaugeCurrent += (_gaugeTarget - _gaugeCurrent) * 0.18;
                    if (Math.Abs(_gaugeTarget - _gaugeCurrent) < 0.5)
                    {
                        _gaugeCurrent = _gaugeTarget;
                        _gaugeTimer.Stop();
                    }
                    DrawGauge(_gaugeCurrent);
                };
            }
            _gaugeTimer.Start();
        }

        private static Point GaugePoint(double cx, double cy, double r, double deg)
        {
            double rad = deg * Math.PI / 180;
            return new Point(cx + r * Math.Cos(rad), cy - r * Math.Sin(rad));
        }

        private static PathGeometry GaugeArc(double cx, double cy, double r, double startDeg, double endDeg)
        {
            var seg = new ArcSegment(
                GaugePoint(cx, cy, r, endDeg),
                new Size(r, r), 0, false, SweepDirection.Clockwise, true);
            var fig = new PathFigure(GaugePoint(cx, cy, r, startDeg),
                new PathSegmentCollection { seg }, false);
            return new PathGeometry(new PathFigureCollection { fig });
        }

        private void DrawGauge(double value)
        {
            GaugeCanvas.Children.Clear();
            double cx = 160, cy = 170, r = 128;
            var track = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x38));
            var accent = (Brush)FindResource("DkAccentHoverBrush");
            var muted = (Brush)FindResource("DkMutedBrush");

            var bg = new System.Windows.Shapes.Path
            {
                Stroke = track,
                StrokeThickness = 14,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Data = GaugeArc(cx, cy, r, 180, 0)
            };
            GaugeCanvas.Children.Add(bg);

            if (value > 0.5)
            {
                double endDeg = 180 - 180 * Math.Min(value, GaugeMaxMbps) / GaugeMaxMbps;
                var fg = new System.Windows.Shapes.Path
                {
                    Stroke = accent,
                    StrokeThickness = 14,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                    Data = GaugeArc(cx, cy, r, 180, endDeg)
                };
                GaugeCanvas.Children.Add(fg);
            }

            // Ticks every 100 Mbps, labels every 250.
            for (int v = 0; v <= 1000; v += 100)
            {
                double deg = 180 - 180.0 * v / 1000;
                Point p1 = GaugePoint(cx, cy, r - 14, deg);
                Point p2 = GaugePoint(cx, cy, r - 22, deg);
                GaugeCanvas.Children.Add(new Line
                {
                    X1 = p1.X, Y1 = p1.Y, X2 = p2.X, Y2 = p2.Y,
                    Stroke = muted, StrokeThickness = v % 250 == 0 ? 2.5 : 1.2
                });
                if (v % 250 == 0)
                {
                    Point lp = GaugePoint(cx, cy, r - 36, deg);
                    var label = new TextBlock
                    {
                        Text = v.ToString(),
                        FontSize = 10,
                        Foreground = muted
                    };
                    Canvas.SetLeft(label, lp.X - 12);
                    Canvas.SetTop(label, lp.Y - 8);
                    GaugeCanvas.Children.Add(label);
                }
            }

            // Needle (accent colored - only text may be white).
            double needleDeg = 180 - 180 * Math.Min(value, GaugeMaxMbps) / GaugeMaxMbps;
            Point tip = GaugePoint(cx, cy, r - 28, needleDeg);
            GaugeCanvas.Children.Add(new Line
            {
                X1 = cx, Y1 = cy, X2 = tip.X, Y2 = tip.Y,
                Stroke = accent, StrokeThickness = 3,
                StrokeStartLineCap = PenLineCap.Round
            });
            GaugeCanvas.Children.Add(new Ellipse
            {
                Width = 14, Height = 14,
                Fill = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x26)),
                Stroke = accent, StrokeThickness = 2
            });
            var hub = (Ellipse)GaugeCanvas.Children[GaugeCanvas.Children.Count - 1];
            Canvas.SetLeft(hub, cx - 7);
            Canvas.SetTop(hub, cy - 7);
        }

        private async void ReTrim_Click(object sender, RoutedEventArgs e)
        {
            var confirm = MessageBox.Show(
                "Run SSD ReTrim on drive C:? This re-optimizes SSD/NVMe storage cells and can take a few minutes.",
                "Sarah's Toolkit", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes) return;
            SetStatus("Running SSD ReTrim (can take a few minutes)...");
            await _tools.ReTrimAsync();
            SetStatus("ReTrim complete.");
            MessageBox.Show("SSD ReTrim complete.", "Sarah's Toolkit", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private async void NetworkRescue_Click(object sender, RoutedEventArgs e)
        {
            var confirm = MessageBox.Show(
                "Run network rescue? This flushes DNS, resets Winsock and TCP/IP, and renews the DHCP lease. Your connection will drop briefly.",
                "Sarah's Toolkit", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes) return;
            SetStatus("Running network rescue...");
            var r = await _tools.NetworkRescueAsync();
            SetStatus(r.Output.Contains("done") ? "Network rescue done. Reboot recommended." : "Network rescue attempted - check Tools output.");
            ToolsLog("=== Network rescue ===");
            ToolsLog((r.Output ?? "").Trim());
        }

        // ---------- Diagnostics (Tools tab) ----------

        private void ToolsLog(string text)
        {
            ToolsOutput.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " +
                CensorUserName(text) + "\r\n");
            ToolsOutput.ScrollToEnd();
        }

        private async Task RunToolAsync(string title, Func<Task<PowerShellResult>> run)
        {
            SetStatus(title + "...");
            ToolsLog("=== " + title + " ===");
            try
            {
                var r = await run();
                string output = (r.Output ?? "").Trim();
                ToolsLog(string.IsNullOrEmpty(output) ? "(no output)" : output);
                if (!string.IsNullOrEmpty(r.Error))
                    ToolsLog("Errors: " + r.Error.Trim());
            }
            catch (Exception ex)
            {
                ToolsLog("FAILED: " + ex.Message);
            }
            SetStatus(title + " finished.");
        }

        private async void SpecsCheck_Click(object sender, RoutedEventArgs e)
        {
            await RunToolAsync("Specs check", () => _diag.SpecsCheckAsync());
        }

        private void RefreshMemoryLabel()
        {
            try
            {
                ulong avail = MemoryService.GetAvailableBytes();
                MemoryAvailLabel.Text = "Available: " + MemoryService.FormatGb(avail);
            }
            catch
            {
                MemoryAvailLabel.Text = "Available: —";
            }
        }

        private void ApplyRecommended_Click(object sender, RoutedEventArgs e)
        {
            var recs = _tweakDefs.Where(t => t.Recommended).ToList();
            int applied = 0, already = 0, skipped = 0, failed = 0;
            bool needsReboot = false;
            var errors = new List<string>();
            foreach (var tw in recs)
            {
                TweakState state;
                try { state = _tweaks.GetState(tw); }
                catch { state = TweakState.Unknown; }
                if (state == TweakState.Applied) { already++; continue; }
                if (state == TweakState.Unknown) { skipped++; continue; }
                try
                {
                    _tweaks.Apply(tw);
                    LedgerNoteApplied(tw);
                    applied++;
                    if (tw.RequiresReboot) needsReboot = true;
                }
                catch (Exception ex)
                {
                    failed++;
                    errors.Add(tw.Name + ": " + ex.Message);
                }
            }
            // Re-sync the Optimize toggles.
            BuildTweakTab(OptimizePanel, new[] { "Privacy", "Gaming", "Performance" });
            RefreshDriftButton();
            string msg = "Recommended tweaks: " + applied + " applied";
            if (already > 0) msg += ", " + already + " already on";
            if (skipped > 0) msg += ", " + skipped + " unreadable";
            if (failed > 0) msg += ", " + failed + " failed";
            msg += "." + (needsReboot ? " Restart Windows for full effect." : "");
            SetStatus(msg);
            if (failed > 0)
                MessageBox.Show("Some tweaks failed:\n" + string.Join("\n", errors),
                    "Sarah's Toolkit", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        private async void RefreshDefenderPanel()
        {
            DefenderStatusLabel.Text = "Scanning for installed apps…";
            var apps = await Task.Run(() => DefenderExclusionService.DetectKnownApps());
            string err = "";
            var exclusions = await Task.Run(() => DefenderExclusionService.GetExclusions(out err));
            var excluded = new HashSet<string>(exclusions.Select(DefenderExclusionService.Normalize));

            DefenderAppsPanel.Children.Clear();
            var visible = apps.Where(a => !excluded.Contains(DefenderExclusionService.Normalize(a.Path))).ToList();
            if (visible.Count == 0)
            {
                DefenderAppsPanel.Children.Add(new TextBlock
                {
                    Text = apps.Count == 0
                        ? "No known apps detected on this PC."
                        : "All detected apps are already excluded.",
                    Foreground = (Brush)FindResource("DkMutedBrush")
                });
            }
            foreach (var app in visible)
            {
                var cb = new CheckBox
                {
                    Tag = app.Path,
                    Margin = new Thickness(0, 2, 0, 2),
                    VerticalAlignment = VerticalAlignment.Center,
                    IsChecked = true
                };
                var label = new StackPanel { Orientation = Orientation.Horizontal };
                label.Children.Add(new TextBlock { Text = app.Name, FontWeight = FontWeights.SemiBold });
                label.Children.Add(new TextBlock
                {
                    Text = "  " + CensorUserName(app.Path),
                    Foreground = (Brush)FindResource("DkMutedBrush"),
                    TextTrimming = TextTrimming.CharacterEllipsis
                });
                cb.Content = label;
                DefenderAppsPanel.Children.Add(cb);
            }

            DefenderExclusionsList.Items.Clear();
            foreach (string p in exclusions)
                DefenderExclusionsList.Items.Add(CensorUserName(p));
            DefenderStatusLabel.Text = visible.Count == 0
                ? (apps.Count == 0
                    ? "No known apps found. You can still add folders manually below."
                    : "Everything detected is already excluded. You can still add folders manually below.")
                : "Found " + visible.Count + " known app(s). Uncheck any you don't want excluded.";
            if (!string.IsNullOrEmpty(err))
                DefenderStatusLabel.Text = "Could not read current exclusions: " + err;
        }

        private void DefenderRefresh_Click(object sender, RoutedEventArgs e)
        {
            RefreshDefenderPanel();
        }

        private async void DefenderExcludeSelected_Click(object sender, RoutedEventArgs e)
        {
            var btn = (Button)sender;
            btn.IsEnabled = false;
            try
            {
                var paths = DefenderAppsPanel.Children.OfType<CheckBox>()
                    .Where(cb => cb.IsEnabled && cb.IsChecked == true)
                    .Select(cb => (string)cb.Tag).ToList();
                if (paths.Count == 0)
                {
                    DefenderStatusLabel.Text = "Nothing selected.";
                    return;
                }
                DefenderStatusLabel.Text = "Adding " + paths.Count + " exclusion(s)…";
                string err = "";
                bool ok = await Task.Run(() => DefenderExclusionService.TryAddExclusions(paths, out err));
                if (ok)
                {
                    DefenderStatusLabel.Text = "Excluded " + paths.Count + " folder(s). Defender will skip them in real-time scans.";
                    ToolsLog("Defender exclusions added: " + string.Join(", ", paths.Select(CensorUserName)));
                }
                else
                {
                    DefenderStatusLabel.Text = "Failed: " + err;
                    ToolsLog("Defender exclusion FAILED: " + err);
                }
                RefreshDefenderPanel();
            }
            finally
            {
                btn.IsEnabled = true;
            }
        }

        private async void DefenderRemoveSelected_Click(object sender, RoutedEventArgs e)
        {
            string shown = DefenderExclusionsList.SelectedItem as string;
            if (string.IsNullOrEmpty(shown))
            {
                DefenderStatusLabel.Text = "Select an exclusion to remove.";
                return;
            }
            // The list shows censored paths; re-read the real ones and match by censored form.
            string err = "";
            var real = await Task.Run(() => DefenderExclusionService.GetExclusions(out err));
            string target = real.FirstOrDefault(p => CensorUserName(p) == shown);
            if (target == null)
            {
                DefenderStatusLabel.Text = "Could not match the selected exclusion.";
                return;
            }
            bool ok = await Task.Run(() => DefenderExclusionService.TryRemoveExclusion(target, out err));
            DefenderStatusLabel.Text = ok ? "Removed exclusion." : "Failed: " + err;
            ToolsLog((ok ? "Defender exclusion removed: " : "Defender exclusion removal FAILED: ") + CensorUserName(target));
            RefreshDefenderPanel();
        }

        private async void DefenderAddCustom_Click(object sender, RoutedEventArgs e)
        {
            string path = DefenderCustomPath.Text.Trim().Trim('"');
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
            {
                DefenderStatusLabel.Text = "Enter an existing folder path first.";
                return;
            }
            string err = "";
            bool ok = await Task.Run(() => DefenderExclusionService.TryAddExclusions(new[] { path }, out err));
            DefenderStatusLabel.Text = ok ? "Excluded." : "Failed: " + err;
            ToolsLog((ok ? "Defender exclusion added: " : "Defender exclusion FAILED: ") + CensorUserName(path));
            if (ok) DefenderCustomPath.Text = "";
            RefreshDefenderPanel();
        }

        private async void PurgeMemory_Click(object sender, RoutedEventArgs e)
        {
            var btn = (Button)sender;
            btn.IsEnabled = false;
            try
            {
                ulong before = MemoryService.GetAvailableBytes();
                MemoryAvailLabel.Text = "Available: " + MemoryService.FormatGb(before) + " — purging…";
                SetStatus("Purging standby cache...");
                string err = "";
                bool ok = await Task.Run(() => MemoryService.TryPurgeStandbyList(out err));
                ulong after = MemoryService.GetAvailableBytes();
                MemoryAvailLabel.Text = "Available: " + MemoryService.FormatGb(after);
                if (ok)
                {
                    double freedGb = Math.Max(0, (after - before) / 1073741824.0);
                    string msg = "Purged the standby cache — freed " + freedGb.ToString("0.0") + " GB (" +
                        MemoryService.FormatGb(before) + " → " + MemoryService.FormatGb(after) + " available).";
                    MemoryResultLabel.Text = msg;
                    ToolsLog("Memory purge: " + msg);
                    SetStatus("Memory purge finished.");
                }
                else
                {
                    MemoryResultLabel.Text = "Purge failed: " + err;
                    ToolsLog("Memory purge FAILED: " + err);
                    SetStatus("Memory purge failed.");
                }
            }
            finally
            {
                btn.IsEnabled = true;
            }
        }

        private async void SpaceAnalyzer_Click(object sender, RoutedEventArgs e)
        {
            await RunToolAsync("Space analyzer", () => _diag.SpaceAnalyzerAsync());
        }

        private async void DriveHealth_Click(object sender, RoutedEventArgs e)
        {
            await RunToolAsync("Drive health", () => _diag.DriveHealthAsync());
        }

        private async void SfcScan_Click(object sender, RoutedEventArgs e)
        {
            var confirm = MessageBox.Show(
                "Run sfc /scannow? This scans and repairs Windows system files and usually takes 10-20 minutes.",
                "Sarah's Toolkit", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes) return;
            await RunToolAsync("SFC scan", () => _diag.SfcScanAsync());
        }

        /// <summary>
        /// True when a startup entry is already disabled via Task Manager / Settings
        /// (flagged in the StartupApproved keys, so Win32_StartupCommand still lists it).
        /// These are hidden from the startup manager list.
        /// </summary>
        private static bool IsStartupEntryDisabled(string name, string location)
        {
            try
            {
                // Scheduled tasks are pre-filtered by the scan (disabled ones
                // are never emitted), so they always count as enabled here.
                if (location.StartsWith("SCHED:", StringComparison.OrdinalIgnoreCase))
                    return false;
                bool isRegistry = location.StartsWith("HKCU", StringComparison.OrdinalIgnoreCase) ||
                                  location.StartsWith("HKLM", StringComparison.OrdinalIgnoreCase);
                if (!isRegistry)
                {
                    using (var key = Registry.CurrentUser.OpenSubKey(
                        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder", false))
                    {
                        if (key == null) return false;
                        var v = key.GetValue(name) as byte[] ?? key.GetValue(name + ".lnk") as byte[];
                        return v != null && v.Length > 0 && v[0] == 0x03;
                    }
                }
                RegistryKey hive = location.StartsWith("HKLM", StringComparison.OrdinalIgnoreCase)
                    ? Registry.LocalMachine : Registry.CurrentUser;
                string[] subs =
                {
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run",
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run32"
                };
                foreach (var sub in subs)
                {
                    using (var key = hive.OpenSubKey(sub, false))
                    {
                        if (key == null) continue;
                        var v = key.GetValue(name) as byte[];
                        if (v != null && v.Length > 0 && v[0] == 0x03) return true;
                    }
                }
                return false;
            }
            catch { return false; }
        }

        /// <summary>
        /// WMI reports per-user registry startup locations as HKU\&lt;sid&gt;\...
        /// Map the current user's SID back to HKCU so the disabled-check and the
        /// disable flow treat it like any other per-user entry.
        /// </summary>
        private static string NormalizeStartupLocation(string location)
        {
            try
            {
                var sid = System.Security.Principal.WindowsIdentity.GetCurrent()?.User?.Value;
                if (!string.IsNullOrEmpty(sid) &&
                    location.StartsWith("HKU\\" + sid, StringComparison.OrdinalIgnoreCase))
                    return "HKCU" + location.Substring(4 + sid.Length);
            }
            catch { }
            return location;
        }

        private async void StartupManager_Click(object sender, RoutedEventArgs e)
        {
            await RefreshStartupListAsync();
        }

        private async Task RefreshStartupListAsync()
        {
            SetStatus("Reading startup entries...");
            ToolsLog("=== Startup manager ===");
            try
            {
                var r = await _diag.ListStartupAsync();
                _startupEntries.Clear();
                ToolsLog("Idx | Name | Command");
                // Same app registered in several places (HKCU Run, HKLM Run,
                // Startup folder...) shows once; all locations are remembered
                // so disabling hits every one of them.
                var seen = new Dictionary<string, StartupEntry>(StringComparer.OrdinalIgnoreCase);
                int visible = 0, hidden = 0, merged = 0;
                foreach (var line in (r.Output ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var parts = line.Split('|');
                    if (parts.Length < 4) continue;
                    string name = parts[1].Trim(), command = parts[2].Trim();
                    if (string.IsNullOrEmpty(name)) continue;
                    string location = NormalizeStartupLocation(parts[3].Trim());
                    if (IsStartupEntryDisabled(name, location)) { hidden++; continue; }
                    string key = name + "\n" + command;
                    if (seen.TryGetValue(key, out var existing))
                    {
                        if (!existing.Locations.Contains(location))
                            existing.Locations.Add(location);
                        merged++;
                        continue;
                    }
                    var entry = new StartupEntry
                    {
                        Index = visible,
                        Name = name,
                        Command = command,
                        Location = location
                    };
                    entry.Locations.Add(location);
                    seen[key] = entry;
                    _startupEntries.Add(entry);
                    string cmd = command;
                    if (cmd.Length > 80) cmd = cmd.Substring(0, 80) + "...";
                    ToolsLog(visible + " | " + name + " | " + cmd);
                    visible++;
                }
                if (hidden > 0)
                    ToolsLog("(" + hidden + " already-disabled " + (hidden == 1 ? "entry" : "entries") + " hidden)");
                if (merged > 0)
                    ToolsLog("(" + merged + " duplicate " + (merged == 1 ? "row" : "rows") + " merged)");
                if (_startupEntries.Count == 0)
                    ToolsLog("(no startup entries found)");
                else
                    ToolsLog("Type indices to disable (e.g. 0,2,3) in the box above and press Disable. A backup is kept under HKCU\\SOFTWARE\\SarahsToolkit\\DisabledStartup.");
            }
            catch (Exception ex)
            {
                ToolsLog("FAILED: " + ex.Message);
            }
            SetStatus("Startup list ready.");
        }

        private async void DisableStartup_Click(object sender, RoutedEventArgs e)
        {
            if (_startupEntries.Count == 0)
            {
                MessageBox.Show("Press 'Startup manager' first to list the entries.",
                    "Sarah's Toolkit", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var selected = new List<StartupEntry>();
            foreach (var token in StartupIndicesBox.Text.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int idx;
                if (!int.TryParse(token.Trim(), out idx)) continue;
                var entry = _startupEntries.Find(x => x.Index == idx);
                if (entry != null && !selected.Contains(entry)) selected.Add(entry);
            }
            if (selected.Count == 0)
            {
                MessageBox.Show("No valid indices. Example: 0,2,3",
                    "Sarah's Toolkit", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var confirm = MessageBox.Show(
                "Disable " + selected.Count + " startup entr" + (selected.Count == 1 ? "y" : "ies") + "? A backup is kept in the registry.",
                "Sarah's Toolkit", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes) return;
            await RunToolAsync("Disable startup entries", () => _diag.DisableStartupAsync(selected));
            await RefreshStartupListAsync();
        }

        private async void ShaderSweep_Click(object sender, RoutedEventArgs e)
        {
            await RunToolAsync("Shader sweep", () => _diag.ShaderSweepAsync());
        }

        private async void UpdateFixer_Click(object sender, RoutedEventArgs e)
        {
            var confirm = MessageBox.Show(
                "Reset Windows Update components? This stops update services and renames the SoftwareDistribution and catroot2 folders.",
                "Sarah's Toolkit", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes) return;
            await RunToolAsync("Update fixer", () => _diag.UpdateFixerAsync());
        }

        private async void Troubleshoot_Click(object sender, RoutedEventArgs e)
        {
            await RunToolAsync("Auto-troubleshoot", () => _diag.TroubleshootAsync());
        }

        private async void PackageUpdater_Click(object sender, RoutedEventArgs e)
        {
            var confirm = MessageBox.Show(
                "Update all packages? This runs winget upgrades (10 minute limit) and then Microsoft Store updates.",
                "Sarah's Toolkit", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes) return;
            await RunToolAsync("Package updater (winget)", () => _diag.PackageUpdaterAsync());
            await RunToolAsync("Package updater (Store)", () => _diag.StoreUpdaterAsync());
        }

        private async void SystemMaintenance_Click(object sender, RoutedEventArgs e)
        {
            var confirm = MessageBox.Show(
                "Run system maintenance? This clears event logs and runs DISM component cleanup (takes a while).",
                "Sarah's Toolkit", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes) return;
            await RunToolAsync("System maintenance", () => _diag.SystemMaintenanceAsync());
        }

        // ---------- Extra cleanup (Tools, not Cleanup) ----------

        private async void EmptyRecycleBin_Click(object sender, RoutedEventArgs e)
        {
            var confirm = MessageBox.Show(
                "Empty the Recycle Bin? This cannot be undone.",
                "Sarah's Toolkit", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes) return;
            await RunToolAsync("Empty Recycle Bin", () => _diag.EmptyRecycleBinAsync());
        }

        private async void ClearCrashDumps_Click(object sender, RoutedEventArgs e)
        {
            await RunToolAsync("Clear crash dumps", () => _diag.ClearCrashDumpsAsync());
        }

        private async void ClearGameLogs_Click(object sender, RoutedEventArgs e)
        {
            await RunToolAsync("Clear game logs & data", () => _diag.ClearGameLogsAsync());
        }

        private async void RemoveWindowsOld_Click(object sender, RoutedEventArgs e)
        {
            var confirm = MessageBox.Show(
                "Remove Windows.old? This permanently deletes the previous Windows installation and kills the 10-day rollback option.",
                "Sarah's Toolkit", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes) return;
            await RunToolAsync("Remove Windows.old", () => _diag.RemoveWindowsOldAsync());
        }

        private async void RemoveDriverLeftovers_Click(object sender, RoutedEventArgs e)
        {
            var confirm = MessageBox.Show(
                "Remove driver installer leftovers (C:\\AMD, C:\\NVIDIA, C:\\Intel)? These are installer extracts, not live drivers.",
                "Sarah's Toolkit", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes) return;
            await RunToolAsync("Remove driver leftovers", () => _diag.RemoveDriverLeftoversAsync());
        }

        private async void DeleteRestorePoints_Click(object sender, RoutedEventArgs e)
        {
            var confirm = MessageBox.Show(
                "Delete ALL system restore points? You will lose the ability to roll back to an earlier state.",
                "Sarah's Toolkit", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes) return;
            await RunToolAsync("Delete restore points", () => _diag.DeleteRestorePointsAsync());
        }

        private async void ResetStoreCache_Click(object sender, RoutedEventArgs e)
        {
            await RunToolAsync("Reset Store cache", () => _diag.ResetStoreCacheAsync());
        }

        private async void CrashHistory_Click(object sender, RoutedEventArgs e)
        {
            await RunToolAsync("Crash history", () => _diag.CrashHistoryAsync());
        }

        private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
        {
            SetStatus("Checking for updates...");
            UpdateLabel.Text = "Checking...";
            UpdateDot.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFA726"));
            UpdateInfo info = await _updates.CheckForUpdatesAsync();
            if (info.Available)
            {
                UpdateLabel.Text = "Update available: v" + info.Version;
                UpdateDot.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#8B5CF6"));
                await PromptAndInstallUpdateAsync(info);
            }
            else
            {
                UpdateLabel.Text = info.Message;
                UpdateDot.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#4CAF50"));
            }
            SetStatus("Ready.");
        }

        private async Task CheckForUpdatesOnLaunchAsync()
        {
            try
            {
                UpdateInfo info = await _updates.CheckForUpdatesAsync();
                if (info.Available)
                {
                    UpdateLabel.Text = "Update available: v" + info.Version;
                    UpdateDot.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#8B5CF6"));
                    await PromptAndInstallUpdateAsync(info);
                }
            }
            catch
            {
                // Never break launch because the update check had a bad day.
            }
        }

        private async Task PromptAndInstallUpdateAsync(UpdateInfo info)
        {
            string msg = "Version " + info.Version + " is available" +
                (string.IsNullOrWhiteSpace(info.LocalVersion) ? "" : " (you're on v" + info.LocalVersion + ")") + "." +
                (string.IsNullOrWhiteSpace(info.Notes) ? "" : "\n\n" + info.Notes) +
                "\n\nDownload and install it now?";
            var go = MessageBox.Show(msg, "Sarah's Toolkit - Update available",
                MessageBoxButton.YesNo, MessageBoxImage.Information);
            if (go != MessageBoxResult.Yes)
                return;

            SetStatus("Downloading update...");
            try
            {
                string tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "SarahsToolkitSetup_update.exe");
                using (var client = new HttpClient())
                {
                    client.DefaultRequestHeaders.UserAgent.ParseAdd("SarahsToolkit");
                    client.Timeout = TimeSpan.FromMinutes(10);
                    // Cache-buster: the installer URL sits behind a CDN that can
                    // otherwise keep serving the previous version's bytes.
                    string dlUrl = info.Url +
                        (info.Url.Contains("?") ? "&" : "?") +
                        "t=" + DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    // The installer is hosted base64-encoded in the gist (gists are text-only).
                    byte[] bytes;
                    if (string.Equals(info.Encoding, "base64", StringComparison.OrdinalIgnoreCase))
                        bytes = Convert.FromBase64String(await client.GetStringAsync(dlUrl));
                    else
                        bytes = await client.GetByteArrayAsync(dlUrl);
                    File.WriteAllBytes(tmp, bytes);
                }
                SetStatus("Launching installer...");
                Process.Start(new ProcessStartInfo(tmp) { UseShellExecute = true });
                _allowClose = true; // don't get parked in the tray; really exit
                Application.Current.Shutdown();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not download the update:\n" + ex.Message,
                    "Sarah's Toolkit", MessageBoxButton.OK, MessageBoxImage.Warning);
                SetStatus("Ready.");
            }
        }

        // ---------- Performance tracker ----------

        private async void PerfTimer_Tick(object sender, EventArgs e)
        {
            if (_perfSampling) return;
            if (PerfTrackEnabled == null || PerfTrackEnabled.IsChecked != true) return;
            _perfSampling = true;
            try
            {
                PerfSample s = await _perf.SampleAsync();
                if (string.IsNullOrEmpty(s.Game))
                {
                    PerfStatus.Text = "Watching for games… (Fortnite, Roblox, Minecraft, VRChat, DCS, GTA V)";
                    return;
                }
                try { _perf.AppendLog(s); } catch { }
                PerfStatus.Text = "Logging: " + s.Game + " — CPU " + s.CpuPct.ToString("0") +
                    "% • RAM " + s.RamUsedGb.ToString("0.0") + "/" + s.RamTotalGb.ToString("0.0") + " GB" +
                    (s.TempC >= 0 ? " • " + _perf.FormatTemp(s.TempC) : "");
            }
            catch { }
            finally { _perfSampling = false; }
        }

        private void PerfTrackEnabled_Changed(object sender, RoutedEventArgs e)
        {
            if (PerfTrackEnabled == null || PerfStatus == null) return;
            if (!_applyingSettings)
            {
                _settings.Settings.LogPerformance = PerfTrackEnabled.IsChecked == true;
                _settings.Save();
            }
            if (PerfTrackEnabled.IsChecked == true)
            {
                if (_perfTimer != null) _perfTimer.Start();
                PerfStatus.Text = "Watching for games… (Fortnite, Roblox, Minecraft, VRChat, DCS, GTA V)";
            }
            else
            {
                if (_perfTimer != null) _perfTimer.Stop();
                PerfStatus.Text = "Tracker paused.";
            }
        }

        private void PerfOpenLog_Click(object sender, RoutedEventArgs e)
        {
            string path = _perf.LogPath;
            try
            {
                if (File.Exists(path))
                    Process.Start(new ProcessStartInfo("explorer.exe",
                        "/select,\"" + path + "\"") { UseShellExecute = true });
                else
                    Process.Start(new ProcessStartInfo("explorer.exe",
                        System.IO.Path.GetDirectoryName(path)) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not open the log location:\n" + ex.Message,
                    "Sarah's Toolkit", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void PerfClearLog_Click(object sender, RoutedEventArgs e)
        {
            _perf.ClearLog();
            PerfStatus.Text = "Log cleared. Watching for games… (Fortnite, Roblox, Minecraft, VRChat, DCS, GTA V)";
            SetStatus("Performance log cleared.");
        }

        // ---------- Dev ----------

        private void DevLogLine(string text)
        {
            DevLog.Items.Add(DateTime.Now.ToString("HH:mm:ss") + "  " + text);
            DevLog.ScrollIntoView(DevLog.Items[DevLog.Items.Count - 1]);
        }

        private async void DevRunAll_Click(object sender, RoutedEventArgs e)
        {
            DevLog.Items.Clear();
            DevLogLine("Running all checks...");
            DevValidateData();
            DevPagesCheck();
            await DevUpdateCheckAsync();
            await DevPayloadCheckAsync();
            await DevVitalsCheckAsync();
            await DevHealthCheckAsync();
            await DevEnvCheckAsync();
            DevLogLine("All checks finished.");
        }

        private void DevValidateData_Click(object sender, RoutedEventArgs e)
        {
            DevValidateData();
        }

        private void DevValidateData()
        {
            DevLogLine("--- Data files ---");
            try
            {
                var tweaks = _tweaks.LoadTweaks();
                int bad = tweaks.Count(t => string.IsNullOrWhiteSpace(t.Id) ||
                    string.IsNullOrWhiteSpace(t.Name) || t.Apply == null);
                int dupes = tweaks.GroupBy(t => t.Id).Count(g => g.Count() > 1);
                DevLogLine(((bad == 0 && dupes == 0) ? "PASS" : "FAIL") + ": tweaks.json — " +
                    tweaks.Count + " tweaks, " + bad + " invalid, " + dupes + " duplicate ids");
            }
            catch (Exception ex) { DevLogLine("FAIL: tweaks.json — " + ex.Message); }

            try
            {
                var cats = _cleanup.LoadCategories();
                int bad = cats.Count(c => string.IsNullOrWhiteSpace(c.Id) ||
                    string.IsNullOrWhiteSpace(c.Name) || c.Paths == null);
                DevLogLine((bad == 0 ? "PASS" : "FAIL") + ": cleanup.json — " +
                    cats.Count + " categories, " + bad + " invalid");
            }
            catch (Exception ex) { DevLogLine("FAIL: cleanup.json — " + ex.Message); }

            try
            {
                var apps = _debloat.LoadApps();
                int bad = apps.Count(a => string.IsNullOrWhiteSpace(a.Id) ||
                    string.IsNullOrWhiteSpace(a.Name));
                DevLogLine((bad == 0 ? "PASS" : "FAIL") + ": debloat.json — " +
                    apps.Count + " apps, " + bad + " invalid");
            }
            catch (Exception ex) { DevLogLine("FAIL: debloat.json — " + ex.Message); }

            try
            {
                var svcs = _services.LoadDefinitions();
                int bad = svcs.Count(s => string.IsNullOrWhiteSpace(s.Name) ||
                    string.IsNullOrWhiteSpace(s.DisplayName));
                DevLogLine((bad == 0 ? "PASS" : "FAIL") + ": services.json — " +
                    svcs.Count + " services, " + bad + " invalid");
            }
            catch (Exception ex) { DevLogLine("FAIL: services.json — " + ex.Message); }

            try
            {
                var presets = _presets.LoadPresets();
                var tweakIds = new HashSet<string>(_tweakDefs.Select(t => t.Id));
                int bad = presets.Count(p => string.IsNullOrWhiteSpace(p.Id) ||
                    string.IsNullOrWhiteSpace(p.Name) ||
                    ((p.Tweaks == null || p.Tweaks.Count == 0) &&
                     (p.Revert == null || p.Revert.Count == 0) && !p.RevertAll));
                int dangling = presets
                    .SelectMany(p => (p.Tweaks ?? Enumerable.Empty<string>())
                        .Concat(p.Revert ?? Enumerable.Empty<string>()))
                    .Count(id => !tweakIds.Contains(id));
                DevLogLine(((bad == 0 && dangling == 0) ? "PASS" : "FAIL") +
                    ": presets.json — " + presets.Count + " presets, " +
                    bad + " invalid, " + dangling + " dangling tweak refs");
            }
            catch (Exception ex) { DevLogLine("FAIL: presets.json — " + ex.Message); }

            try
            {
                var releases = _games.LoadUpcoming();
                int bad = releases.Count(r => string.IsNullOrWhiteSpace(r.Title) ||
                    r.Date == default(DateTime));
                DevLogLine((bad == 0 ? "PASS" : "FAIL") + ": game_releases.json — " +
                    releases.Count + " upcoming releases, " + bad + " invalid");
            }
            catch (Exception ex) { DevLogLine("FAIL: game_releases.json — " + ex.Message); }
        }

        private async void DevUpdateCheck_Click(object sender, RoutedEventArgs e)
        {
            await DevUpdateCheckAsync();
        }

        private async Task DevUpdateCheckAsync()
        {
            DevLogLine("--- Update check ---");
            try
            {
                var info = await _updates.CheckForUpdatesAsync();
                string local = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?";
                DevLogLine("PASS: local v" + local +
                    ", manifest v" + (string.IsNullOrEmpty(info.Version) ? "(none)" : info.Version) +
                    ", update available: " + info.Available);
                if (!info.Available && !string.IsNullOrEmpty(info.Message))
                    DevLogLine("INFO: " + info.Message);
            }
            catch (Exception ex) { DevLogLine("FAIL: update check — " + ex.Message); }
        }

        private async void DevPayloadCheck_Click(object sender, RoutedEventArgs e)
        {
            await DevPayloadCheckAsync();
        }

        private async Task DevPayloadCheckAsync()
        {
            DevLogLine("--- Update payload ---");
            try
            {
                var info = await _updates.CheckForUpdatesAsync();
                if (!info.Available || string.IsNullOrEmpty(info.Url))
                {
                    DevLogLine("SKIP: no update available, nothing to download");
                    return;
                }
                string dlUrl = info.Url +
                    (info.Url.Contains("?") ? "&" : "?") +
                    "t=" + DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                using (var client = new HttpClient())
                {
                    client.Timeout = TimeSpan.FromMinutes(10);
                    // Mirror the real updater: base64 for old manifests, raw bytes otherwise.
                    byte[] bytes;
                    if (string.Equals(info.Encoding, "base64", StringComparison.OrdinalIgnoreCase))
                        bytes = Convert.FromBase64String(await client.GetStringAsync(dlUrl));
                    else
                        bytes = await client.GetByteArrayAsync(dlUrl);
                    bool mz = bytes.Length > 2 && bytes[0] == 'M' && bytes[1] == 'Z';
                    bool footer = bytes.Length > 16 &&
                        System.Text.Encoding.ASCII.GetString(bytes, bytes.Length - 8, 8) == "STKINSTL";
                    DevLogLine((mz && footer ? "PASS" : "FAIL") + ": payload " +
                        (string.Equals(info.Encoding, "base64",
                            StringComparison.OrdinalIgnoreCase) ? "decoded" : "downloaded") +
                        ", " + bytes.Length + " bytes, exe header " + (mz ? "OK" : "BAD") +
                        ", installer footer " + (footer ? "OK" : "BAD"));
                }
            }
            catch (Exception ex) { DevLogLine("FAIL: payload — " + ex.Message); }
        }

        private async void DevEnvCheck_Click(object sender, RoutedEventArgs e)
        {
            await DevEnvCheckAsync();
        }

        private Task DevEnvCheckAsync()
        {
            return Task.Run(() =>
            {
                Dispatcher.Invoke(new Action(() => DevLogLine("--- Environment ---")));
                try
                {
                    var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
                    var principal = new System.Security.Principal.WindowsPrincipal(identity);
                    bool admin = principal.IsInRole(
                        System.Security.Principal.WindowsBuiltInRole.Administrator);
                    Dispatcher.Invoke(new Action(() => DevLogLine(
                        (admin ? "PASS" : "FAIL") + ": running as administrator: " + admin)));
                }
                catch (Exception ex)
                {
                    Dispatcher.Invoke(new Action(() => DevLogLine("FAIL: admin check — " + ex.Message)));
                }
                Dispatcher.Invoke(new Action(() => DevLogLine(
                    "INFO: OS " + Environment.OSVersion.Version +
                    ", 64-bit OS: " + Environment.Is64BitOperatingSystem +
                    ", CLR " + Environment.Version)));
                try
                {
                    var r = PowerShellRunner.RunScript("$PSVersionTable.PSVersion.ToString()", 1);
                    Dispatcher.Invoke(new Action(() => DevLogLine(
                        "INFO: Windows PowerShell " + r.Output.Trim())));
                }
                catch (Exception ex)
                {
                    Dispatcher.Invoke(new Action(() => DevLogLine("FAIL: PowerShell — " + ex.Message)));
                }
            });
        }

        // ---------- Dev self-checks ----------

        private void DevPagesCheck()
        {
            DevLogLine("--- Pages ---");
            string[] pages = { "Dashboard", "Health", "Cleanup", "Debloat", "Services",
                "Optimize", "Presets", "Customize", "Network", "Security", "Tools",
                "Dev", "About", "Settings" };
            int bad = 0;
            foreach (var p in pages)
            {
                bool nav = FindName("Nav" + p) != null;
                bool page = FindName("Page" + p) != null;
                if (!nav || !page)
                {
                    bad++;
                    DevLogLine("FAIL: " + p + " — nav button found: " + nav + ", page found: " + page);
                }
            }
            if (bad == 0)
                DevLogLine("PASS: all " + pages.Length + " nav buttons map to a page");
        }

        private async Task DevVitalsCheckAsync()
        {
            DevLogLine("--- Live vitals ---");
            try
            {
                // Sparkline smoke test: construct + feed values, no render needed.
                var spark = new Controls.Sparkline
                {
                    Values = new double[] { 10, 20, double.NaN, 40 },
                    LineColor = System.Windows.Media.Colors.DodgerBlue,
                    AutoScale = true
                };
                DevLogLine(spark.Values.Length == 4 ? "PASS: sparkline control constructs"
                    : "FAIL: sparkline control");
            }
            catch (Exception ex) { DevLogLine("FAIL: sparkline — " + ex.Message); }

            try
            {
                await _vitals.SampleAsync();
                LogVitalRange("CPU", _vitals.Cpu, 0, 100, v => v.ToString("0") + "%");
                LogVitalRange("GPU", _vitals.Gpu, 0, 100, v => v.ToString("0") + "%");
                LogVitalRange("Memory", _vitals.Ram, 0, 100, v => v.ToString("0") + "%");
                LogVitalRange("Disk", _vitals.Disk, 0, 100, v => v.ToString("0") + "%");
                if (double.IsNaN(_vitals.NetMbps))
                    DevLogLine("INFO: network — no interface counters on this machine");
                else if (_vitals.NetMbps < 0)
                    DevLogLine("FAIL: network — negative throughput: " + _vitals.NetMbps);
                else
                    DevLogLine("PASS: network = " + FormatMbps(_vitals.NetMbps));
                if (double.IsNaN(_vitals.PingMs))
                    DevLogLine("INFO: ping — ICMP blocked or no network");
                else if (_vitals.PingMs < 0 || _vitals.PingMs > 10000)
                    DevLogLine("FAIL: ping out of range: " + _vitals.PingMs);
                else
                    DevLogLine("PASS: ping = " + _vitals.PingMs.ToString("0") + " ms");
                foreach (var h in new[] { _vitals.CpuHistory, _vitals.GpuHistory,
                    _vitals.RamHistory, _vitals.DiskHistory, _vitals.NetHistory, _vitals.PingHistory })
                {
                    if (h.Length > VitalsService.HistoryLength)
                        DevLogLine("FAIL: vital history exceeded capacity: " + h.Length);
                }
                DevLogLine("PASS: vital histories within capacity");
            }
            catch (Exception ex) { DevLogLine("FAIL: vitals sampling — " + ex.Message); }
        }

        private void LogVitalRange(string name, double v, double min, double max,
            Func<double, string> format)
        {
            if (double.IsNaN(v))
                DevLogLine("INFO: " + name + " — counter unavailable on this machine");
            else if (v < min || v > max)
                DevLogLine("FAIL: " + name + " out of range: " + v);
            else
                DevLogLine("PASS: " + name + " = " + format(v));
        }

        private async Task DevHealthCheckAsync()
        {
            DevLogLine("--- Health (read-only) ---");
            try
            {
                var b = await _health.GetBatteryInfoAsync();
                if (!b.HasBattery)
                {
                    DevLogLine("PASS: battery query — no battery detected, handled as desktop");
                }
                else if (!double.IsNaN(b.HealthPercent) &&
                    (b.HealthPercent < 0 || b.HealthPercent > 110))
                {
                    DevLogLine("FAIL: battery health out of range: " + b.HealthPercent);
                }
                else
                {
                    DevLogLine("PASS: battery query — health " +
                        (double.IsNaN(b.HealthPercent) ? "unknown"
                            : b.HealthPercent.ToString("0") + "%") +
                        ", cycles " + (b.CycleCount >= 0 ? b.CycleCount.ToString() : "unknown"));
                }
            }
            catch (Exception ex) { DevLogLine("FAIL: battery query — " + ex.Message); }

            try
            {
                var disks = await _health.GetDiskHealthAsync();
                if (disks.Count == 0)
                {
                    DevLogLine("FAIL: disk health — no disks returned");
                }
                else
                {
                    bool sane = disks.All(d => !string.IsNullOrWhiteSpace(d.Name) &&
                        (d.WearPercent == -1 || (d.WearPercent >= 0 && d.WearPercent <= 100)) &&
                        (d.TemperatureC == -1 || (d.TemperatureC >= 0 && d.TemperatureC < 120)));
                    DevLogLine((sane ? "PASS" : "FAIL") + ": disk health — " +
                        disks.Count + " disk(s), values " + (sane ? "sane" : "out of range"));
                }
            }
            catch (Exception ex) { DevLogLine("FAIL: disk health — " + ex.Message); }

            try
            {
                DevLogLine("INFO: Windows Update check can take a minute on first run…");
                var u = await _health.GetWindowsUpdateInfoAsync();
                DevLogLine(u.Checked
                    ? "PASS: Windows Update query — " + u.PendingCount + " pending"
                    : "FAIL: Windows Update query — no result");
            }
            catch (Exception ex) { DevLogLine("FAIL: Windows Update query — " + ex.Message); }

            // Maintenance buttons exist and are wired; the actions themselves are
            // never run here because this suite promises not to change the system.
            bool wired = MaintRetrimBtn != null && MaintDismBtn != null && MaintSfcBtn != null;
            DevLogLine(wired ? "PASS: maintenance buttons wired (actions not run here)"
                : "FAIL: maintenance buttons missing");
        }

        private async void DevVitalsHealth_Click(object sender, RoutedEventArgs e)
        {
            DevPagesCheck();
            await DevVitalsCheckAsync();
            await DevHealthCheckAsync();
        }

        // ---------- Helpers ----------

        private void SetStatus(string text)
        {
            StatusText.Text = text;
        }

        /// <summary>
        /// Replaces the current Windows user name in a path with *** so logs
        /// never leak it, e.g. C:\Users\conpl\AppData\... becomes
        /// C:\Users\***\AppData\...
        /// </summary>
        private static string CensorUserName(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            try
            {
                string profile = Environment.GetFolderPath(
                    Environment.SpecialFolder.UserProfile);
                if (!string.IsNullOrEmpty(profile))
                {
                    string parent = System.IO.Path.GetDirectoryName(profile.TrimEnd(
                        System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));
                    if (!string.IsNullOrEmpty(parent))
                    {
                        return Regex.Replace(text, Regex.Escape(profile),
                            parent + System.IO.Path.DirectorySeparatorChar + "***",
                            RegexOptions.IgnoreCase);
                    }
                }
            }
            catch
            {
                // Cosmetic only; never break output over it.
            }
            return text;
        }

        private static string FormatBytes(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double v = bytes;
            int u = 0;
            while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
            return v.ToString("0.0") + " " + units[u];
        }

        private static string FirstLineStartingWith(string output, string prefix)
        {
            if (string.IsNullOrEmpty(output)) return "";
            foreach (var line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string t = line.Trim();
                if (t.StartsWith(prefix)) return t;
            }
            return "";
        }
    }
}
