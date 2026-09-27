using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SarahsToolkit.Models;
using SarahsToolkit.Services;

namespace SarahsToolkit
{
    public partial class MainWindow : Window
    {
        private readonly TweakService _tweaks = new TweakService();
        private readonly CleanupService _cleanup = new CleanupService();
        private readonly DebloatService _debloat = new DebloatService();
        private readonly ToolsService _tools = new ToolsService();
        private readonly UpdateService _updates = new UpdateService();

        private List<TweakDefinition> _tweakDefs = new List<TweakDefinition>();
        private List<DebloatApp> _debloatApps = new List<DebloatApp>();
        private CancellationTokenSource _cleanCts;
        private bool _buildingUi;

        public MainWindow()
        {
            InitializeComponent();
            Loaded += MainWindow_Loaded;
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
                _debloatApps = _debloat.LoadApps();
                BuildDebloatList();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Failed to load debloat list: " + ex.Message,
                    "Sarah's Toolkit", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            SetStatus("Ready.");
            await RefreshDebloatInstalledAsync();
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

        private async void DisableServices_Click(object sender, RoutedEventArgs e)
        {
            var confirm = MessageBox.Show(
                "Disable non-essential background services and telemetry?\n\nPrint Spooler is included - use 'Restore to Manual' if you print.",
                "Sarah's Toolkit", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes) return;
            SetStatus("Disabling services...");
            var r = await _tools.DisableServicesAsync();
            string line = FirstLineStartingWith(r.Output, "Disabled=");
            SetStatus(!string.IsNullOrEmpty(line) ? "Service optimization done (" + line + "). Reboot recommended." : "Service optimization done. Reboot recommended.");
        }

        private async void EnableServices_Click(object sender, RoutedEventArgs e)
        {
            SetStatus("Restoring services...");
            var r = await _tools.EnableServicesAsync();
            string line = FirstLineStartingWith(r.Output, "Restored=");
            SetStatus(!string.IsNullOrEmpty(line) ? "Services restored (" + line + "). Reboot recommended." : "Services restored. Reboot recommended.");
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
            SetStatus("Flushing DNS...");
            var r = await _tools.NetworkRescueAsync();
            SetStatus(r.Output.Contains("flushed") ? "DNS cache flushed." : "Network rescue attempted.");
        }

        private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
        {
            SetStatus("Checking for updates...");
            UpdateLabel.Text = "Checking...";
            UpdateInfo info = await _updates.CheckForUpdatesAsync();
            if (info.Available)
            {
                UpdateLabel.Text = "Update available: " + info.Version;
                var open = MessageBox.Show("Version " + info.Version + " is available.\n\nOpen the release page to download it?",
                    "Sarah's Toolkit", MessageBoxButton.YesNo, MessageBoxImage.Information);
                if (open == MessageBoxResult.Yes)
                    Process.Start(new ProcessStartInfo(info.Url) { UseShellExecute = true });
            }
            else
            {
                UpdateLabel.Text = info.Message;
            }
            SetStatus("Ready.");
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
