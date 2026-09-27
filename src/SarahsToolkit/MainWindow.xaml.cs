using System;
using System.Collections.Generic;
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

        private List<TweakDefinition> _tweakDefs = new List<TweakDefinition>();
        private List<PresetDefinition> _presetDefs = new List<PresetDefinition>();
        private readonly PresetService _presets = new PresetService();
        private readonly Dictionary<string, TextBlock> _presetRatingLabels =
            new Dictionary<string, TextBlock>();
        private List<DebloatApp> _debloatApps = new List<DebloatApp>();
        private List<ServiceDefinition> _serviceDefs = new List<ServiceDefinition>();
        private bool _servicesRefreshed = false;
        private CancellationTokenSource _cleanCts;
        private bool _buildingUi;

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute,
            ref int pvAttribute, int cbAttribute);
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

        private const int WM_SETICON = 0x0080;
        private const int ICON_SMALL = 0;
        private const int ICON_BIG = 1;

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg,
            int wParam, IntPtr lParam);

        public MainWindow()
        {
            InitializeComponent();
            Loaded += MainWindow_Loaded;
            SourceInitialized += MainWindow_SourceInitialized;
        }

        private void MainWindow_SourceInitialized(object sender, EventArgs e)
        {
            try
            {
                IntPtr hwnd = new WindowInteropHelper(this).Handle;
                // Dark title bar to match the app theme (Windows 10 1809+).
                int dark = 1;
                DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE,
                    ref dark, sizeof(int));
                // Drop the empty default icon from the title bar.
                SendMessage(hwnd, WM_SETICON, ICON_SMALL, IntPtr.Zero);
                SendMessage(hwnd, WM_SETICON, ICON_BIG, IntPtr.Zero);
            }
            catch
            {
                // Cosmetic only; the app works fine without it.
            }
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                string v = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3);
                VersionText.Text = "v" + (string.IsNullOrEmpty(v) ? "1.0.0" : v);
            }
            catch
            {
                VersionText.Text = "v1.0.0";
            }

            try
            {
                _tweakDefs = _tweaks.LoadTweaks();
                BuildTweakTab(OptimizePanel, new[] { "Privacy", "Gaming", "Performance" });
                BuildTweakTab(CustomizePanel, new[] { "Theme", "Taskbar", "Explorer", "Start" });
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

            SetStatus("Ready.");
            await RefreshDebloatInstalledAsync();

            // Silent update check on every launch: only speaks up if an update exists.
            await CheckForUpdatesOnLaunchAsync();
        }

        // ---------- Tweaks ----------

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
                        Margin = new Thickness(0, 0, 0, 8),
                        Padding = new Thickness(8)
                    };
                    var stack = new StackPanel();
                    foreach (var tw in tweaks)
                    {
                        TweakState state = _tweaks.GetState(tw);
                        var cb = new CheckBox
                        {
                            Content = tw.Name,
                            Tag = tw,
                            Margin = new Thickness(0, 4, 0, 0),
                            FontWeight = FontWeights.SemiBold
                        };
                        cb.ToolTip = tw.Description;
                        cb.IsChecked = state == TweakState.Applied;
                        if (state == TweakState.Unknown)
                        {
                            cb.IsEnabled = false;
                            cb.ToolTip = "Could not read current state: " + tw.Description;
                        }
                        cb.Checked += TweakBox_Toggled;
                        cb.Unchecked += TweakBox_Toggled;
                        stack.Children.Add(cb);

                        var desc = new TextBlock
                        {
                            Text = tw.Description + (tw.RequiresReboot ? " (Restart required.)" : ""),
                            TextWrapping = TextWrapping.Wrap,
                            Foreground = Brushes.Gray,
                            Margin = new Thickness(20, 0, 0, 6),
                            FontSize = 12
                        };
                        stack.Children.Add(desc);
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
                    _tweaks.Apply(tw);
                else
                    _tweaks.Revert(tw);
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

        // ---------- Presets ----------

        private void BuildPresetsTab()
        {
            PresetsPanel.Children.Clear();
            _presetRatingLabels.Clear();
            foreach (var preset in _presetDefs)
            {
                var group = new GroupBox
                {
                    Header = preset.Name,
                    Margin = new Thickness(0, 0, 0, 8),
                    Padding = new Thickness(8)
                };
                var stack = new StackPanel();
                stack.Children.Add(new TextBlock
                {
                    Text = preset.Description,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = Brushes.Gray,
                    Margin = new Thickness(0, 0, 0, 4),
                    FontSize = 12
                });

                var row = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Margin = new Thickness(0, 0, 0, 4)
                };
                var ratingLabel = new TextBlock
                {
                    FontWeight = FontWeights.SemiBold,
                    VerticalAlignment = VerticalAlignment.Center
                };
                row.Children.Add(ratingLabel);
                var applyBtn = new Button
                {
                    Content = "Apply preset",
                    Width = 100,
                    Margin = new Thickness(12, 0, 0, 0),
                    Tag = preset
                };
                applyBtn.Click += PresetApply_Click;
                row.Children.Add(applyBtn);
                stack.Children.Add(row);

                if (PresetNeedsReboot(preset))
                {
                    stack.Children.Add(new TextBlock
                    {
                        Text = "Restart Windows afterwards for full effect.",
                        Foreground = Brushes.Gray,
                        FontSize = 12
                    });
                }

                group.Content = stack;
                PresetsPanel.Children.Add(group);
                _presetRatingLabels[preset.Id] = ratingLabel;
            }
            RefreshPresetRatings();
        }

        private bool PresetNeedsReboot(PresetDefinition preset)
        {
            return (preset.Tweaks ?? Enumerable.Empty<string>())
                .Select(id => _tweakDefs.FirstOrDefault(t => t.Id == id))
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

            int applied = 0, failed = 0;
            foreach (string id in preset.Tweaks ?? Enumerable.Empty<string>())
            {
                var tw = _tweakDefs.FirstOrDefault(t => t.Id == id);
                if (tw == null) continue;
                if (_tweaks.GetState(tw) != TweakState.NotApplied) continue;
                try { _tweaks.Apply(tw); applied++; }
                catch { failed++; }
            }
            SetStatus(preset.Name + " preset: " + applied + " setting(s) applied" +
                (failed > 0 ? ", " + failed + " failed." : ".") +
                (needsReboot ? " Restart Windows for full effect." : ""));
            // Re-sync the Optimize/Customize checkboxes and the ratings.
            BuildTweakTab(OptimizePanel, new[] { "Privacy", "Gaming", "Performance" });
            BuildTweakTab(CustomizePanel, new[] { "Theme", "Taskbar", "Explorer", "Start" });
            RefreshPresetRatings();
        }

        // ---------- Cleanup ----------

        private async void QuickClean_Click(object sender, RoutedEventArgs e)
        {
            await RunClean(quickOnly: true);
        }

        private async void FullClean_Click(object sender, RoutedEventArgs e)
        {
            await RunClean(quickOnly: false);
        }

        private void CancelClean_Click(object sender, RoutedEventArgs e)
        {
            if (_cleanCts != null) _cleanCts.Cancel();
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

        private async Task RunClean(bool quickOnly)
        {
            var cats = _cleanup.LoadCategories().Where(c => !quickOnly || c.QuickClean).ToList();
            string mode = quickOnly ? "Quick Clean" : "Full Clean";
            var confirm = MessageBox.Show(
                mode + " will delete temporary and cache files.\n\nYour personal files are not touched. Continue?",
                "Sarah's Toolkit", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes) return;

            SetCleanUiRunning(true);
            _cleanCts = new CancellationTokenSource();
            CleanLog.Items.Clear();
            CleanLog.Items.Add("=== " + mode + " ===");
            FreedLabel.Text = "";

            var progress = new Progress<CleanupProgress>(p =>
            {
                StatusText.Text = p.Category == "Done" ? "Finishing..." : "Cleaning: " + p.Category;
                FreedLabel.Text = FormatBytes(p.BytesFreed) + " freed  ·  " + p.FilesDeleted + " files";
                if (!string.IsNullOrEmpty(p.CurrentFile))
                {
                    CleanLog.Items.Add(p.CurrentFile);
                    if (CleanLog.Items.Count > 400) CleanLog.Items.RemoveAt(0);
                    CleanLog.ScrollIntoView(CleanLog.Items[CleanLog.Items.Count - 1]);
                }
            });

            try
            {
                long bytes = await _cleanup.CleanAsync(cats, progress, _cleanCts.Token);
                CleanLog.Items.Add("=== Done: " + FormatBytes(bytes) + " freed ===");
                SetStatus("Clean complete: " + FormatBytes(bytes) + " freed.");
                MessageBox.Show(mode + " complete.\n" + FormatBytes(bytes) + " freed.",
                    "Sarah's Toolkit", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (OperationCanceledException)
            {
                SetStatus("Clean cancelled.");
                CleanLog.Items.Add("=== Cancelled ===");
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
            QuickCleanButton.IsEnabled = !running;
            FullCleanButton.IsEnabled = !running;
            CancelCleanButton.IsEnabled = running;
            CleanProgress.IsIndeterminate = running;
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
            int i = 0;
            foreach (var app in _debloatApps)
            {
                if (i >= DebloatPanel.Children.Count) break;
                var cb = (CheckBox)DebloatPanel.Children[i++];
                cb.IsEnabled = app.Installed;
                cb.IsChecked = false;
                cb.Content = app.Name + (app.Installed ? "" : "  (not installed)");
                cb.Foreground = app.Installed ? Brushes.WhiteSmoke : Brushes.Gray;
            }
            int installed = _debloatApps.Count(a => a.Installed);
            SetStatus(installed + " of " + _debloatApps.Count + " listed apps are installed.");
        }

        private async void RemoveDebloat_Click(object sender, RoutedEventArgs e)
        {
            var selected = new List<DebloatApp>();
            foreach (CheckBox cb in DebloatPanel.Children)
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

        private async void MainTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_servicesRefreshed && ServicesTab.IsSelected)
            {
                _servicesRefreshed = true;
                await RefreshServiceStatesAsync();
            }
            if (PresetsTab != null && PresetsTab.IsSelected)
            {
                RefreshPresetRatings();
            }
        }

        private void BuildServicesList()
        {
            ServicesPanel.Children.Clear();
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
                    Foreground = Brushes.Gray,
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
                cb.Foreground = def.StartType == "Disabled" ? Brushes.Gray : Brushes.WhiteSmoke;
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
            var btn = (Button)sender;
            btn.IsEnabled = false;
            SpeedLabel.Text = "Downloading Speedtest CLI (Ookla)...";
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
                    SpeedLabel.Text =
                        "ISP: " + parts[3] + "\n" +
                        "Ping: " + parts[0] + " ms\n" +
                        "Download: " + parts[1] + " Mbps\n" +
                        "Upload: " + parts[2] + " Mbps\n" +
                        wifi +
                        (url != null ? "\nFull results: " + url : "");
                }
                else
                {
                    SpeedLabel.Text = "Speed test failed" + (error != null ? ": " + error + "." : ".") +
                        " Check your connection or try speedtest.net in your browser.";
                }
            }
            catch (Exception ex)
            {
                SpeedLabel.Text = "Speed test failed: " + ex.Message;
            }
            btn.IsEnabled = true;
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
            ToolsOutput.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + text + "\r\n");
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
                foreach (var line in (r.Output ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var parts = line.Split('|');
                    if (parts.Length < 4) continue;
                    int idx;
                    if (!int.TryParse(parts[0], out idx)) continue;
                    _startupEntries.Add(new StartupEntry
                    {
                        Index = idx,
                        Name = parts[1],
                        Command = parts[2],
                        Location = parts[3]
                    });
                    string cmd = parts[2];
                    if (cmd.Length > 80) cmd = cmd.Substring(0, 80) + "...";
                    ToolsLog(parts[0] + " | " + parts[1] + " | " + cmd);
                }
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
            UpdateInfo info = await _updates.CheckForUpdatesAsync();
            if (info.Available)
            {
                UpdateLabel.Text = "Update available: v" + info.Version;
                await PromptAndInstallUpdateAsync(info);
            }
            else
            {
                UpdateLabel.Text = info.Message;
            }
            SetStatus("Ready.");
        }

        private async Task CheckForUpdatesOnLaunchAsync()
        {
            try
            {
                UpdateInfo info = await _updates.CheckForUpdatesAsync();
                if (info.Available)
                    await PromptAndInstallUpdateAsync(info);
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
                string tmp = Path.Combine(Path.GetTempPath(), "SarahsToolkitSetup_update.exe");
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
                Application.Current.Shutdown();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not download the update:\n" + ex.Message,
                    "Sarah's Toolkit", MessageBoxButton.OK, MessageBoxImage.Warning);
                SetStatus("Ready.");
            }
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
            await DevUpdateCheckAsync();
            await DevPayloadCheckAsync();
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
                DevLogLine((bad == 0 ? "PASS" : "FAIL") + ": tweaks.json — " +
                    tweaks.Count + " tweaks, " + bad + " invalid");
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
                    string.IsNullOrWhiteSpace(p.Name) || p.Tweaks == null);
                int dangling = presets
                    .SelectMany(p => p.Tweaks ?? Enumerable.Empty<string>())
                    .Count(id => !tweakIds.Contains(id));
                DevLogLine(((bad == 0 && dangling == 0) ? "PASS" : "FAIL") +
                    ": presets.json — " + presets.Count + " presets, " +
                    bad + " invalid, " + dangling + " dangling tweak refs");
            }
            catch (Exception ex) { DevLogLine("FAIL: presets.json — " + ex.Message); }
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
                    byte[] bytes = Convert.FromBase64String(await client.GetStringAsync(dlUrl));
                    bool mz = bytes.Length > 2 && bytes[0] == 'M' && bytes[1] == 'Z';
                    DevLogLine((mz ? "PASS" : "FAIL") + ": payload decoded, " +
                        bytes.Length + " bytes, exe header " + (mz ? "OK" : "BAD"));
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

        // ---------- Helpers ----------

        private void SetStatus(string text)
        {
            StatusText.Text = text;
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
