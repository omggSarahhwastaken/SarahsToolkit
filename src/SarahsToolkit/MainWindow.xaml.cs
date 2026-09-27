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
using System.Windows.Shapes;
using System.Windows.Threading;
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
        private bool _dashboardLoaded = false;
        private readonly GameFeedService _games = new GameFeedService();
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

            DrawGauge(0);
            SetStatus("Ready.");
            await RefreshDebloatInstalledAsync();

            // Silent update check on every launch: only speaks up if an update exists.
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
        }

        // ---------- Navigation ----------

        private async void Nav_Checked(object sender, RoutedEventArgs e)
        {
            var rb = sender as RadioButton;
            if (rb == null || rb.IsChecked != true) return;
            // Fires during InitializeComponent before the pages exist.
            if (PageDashboard == null || PageHost == null) return;

            PageDashboard.Visibility = Visibility.Collapsed;
            PageCleanup.Visibility = Visibility.Collapsed;
            PageDebloat.Visibility = Visibility.Collapsed;
            PageServices.Visibility = Visibility.Collapsed;
            PageOptimize.Visibility = Visibility.Collapsed;
            PagePresets.Visibility = Visibility.Collapsed;
            PageCustomize.Visibility = Visibility.Collapsed;
            PageNetwork.Visibility = Visibility.Collapsed;
            PageTools.Visibility = Visibility.Collapsed;
            PageDev.Visibility = Visibility.Collapsed;
            PageAbout.Visibility = Visibility.Collapsed;

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
                case "NavTools":
                    PageTools.Visibility = Visibility.Visible;
                    PageTitle.Text = "Tools";
                    PageSubtitle.Text = "Diagnostics and system utilities";
                    RefreshMemoryLabel();
                    RefreshDefenderPanel();
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
            }
        }

        // ---------- Dashboard ----------

        private void DashTimer_Tick(object sender, EventArgs e)
        {
            if (WindowState == WindowState.Minimized) return;
            if (PageDashboard == null || PageDashboard.Visibility != Visibility.Visible) return;
            _ = LoadDashboardAsync(quiet: true);
        }

        private async Task LoadDashboardAsync(bool quiet = false)
        {
            if (_dashboardLoading) return;
            _dashboardLoading = true;
            try
            {
                if (!quiet)
                {
                    SetStatus("Reading system info...");
                    DashCpu.Text = "Reading…";
                    DashGpu.Text = "Reading…";
                }
                var snap = await _diag.GetDashboardSnapshotAsync();

                DashCpu.Text = string.IsNullOrWhiteSpace(snap.Cpu) ? "Unknown" : snap.Cpu;
                DashGpu.Text = string.IsNullOrWhiteSpace(snap.Gpu) ? "Unknown" : snap.Gpu;

                if (snap.RamTotalGb > 0)
                {
                    double used = Math.Max(0, snap.RamTotalGb - snap.RamFreeGb);
                    double pct = used / snap.RamTotalGb * 100;
                    DashRam.Text = used.ToString("0.0") + " / " + snap.RamTotalGb.ToString("0.0") + " GB used";
                    DashRamBar.Value = pct;
                    DashRamSub.Text = snap.RamFreeGb.ToString("0.0") + " GB free";
                    SetDot(DashRamDot, pct >= 90 ? DotColor.Red : pct >= 75 ? DotColor.Amber : DotColor.Green);
                }
                else
                {
                    DashRam.Text = "Unknown";
                    SetDot(DashRamDot, DotColor.Gray);
                }

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
                            toggle.ToolTip = "Could not read current state: " + tw.Description;
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
            // Re-sync the Optimize/Customize toggles and the ratings.
            BuildTweakTab(OptimizePanel, new[] { "Privacy", "Gaming", "Performance" });
            BuildTweakTab(CustomizePanel, new[] { "Theme", "Taskbar", "Explorer", "Start" });
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
            long bytesFreed = 0, filesDeleted = 0;
            string currentCat = null;
            long catStartBytes = 0, catStartFiles = 0;
            var perCat = new List<Tuple<string, long, long>>();

            var progress = new Progress<CleanupProgress>(p =>
            {
                if (p.Category == "Done")
                {
                    bytesFreed = p.BytesFreed;
                    filesDeleted = p.FilesDeleted;
                    ProgressStage.Text = "Finishing…";
                    return;
                }
                if (p.Category != currentCat)
                {
                    if (currentCat != null)
                        perCat.Add(Tuple.Create(currentCat, bytesFreed - catStartBytes, filesDeleted - catStartFiles));
                    currentCat = p.Category;
                    catStartBytes = bytesFreed;
                    catStartFiles = filesDeleted;
                    ProgressStage.Text = "Cleaning " + p.Category + "…";
                }
                bytesFreed = p.BytesFreed;
                filesDeleted = p.FilesDeleted;
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
                    perCat.Add(Tuple.Create(currentCat, bytesFreed - catStartBytes, filesDeleted - catStartFiles));

                CleanProgress.Value = 100;
                ProgressPct.Text = "100%";
                ProgressStage.Text = "Done";
                ProgressFile.Text = "";
                CleanLog.Items.Add("=== Done: " + FormatBytes(bytesFreed) + " freed ===");

                // Post-clean summary, per category.
                SummaryList.Children.Clear();
                var muted = (Brush)FindResource("DkMutedBrush");
                foreach (var pc in perCat)
                {
                    var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    row.Children.Add(new TextBlock { Text = pc.Item1, TextWrapping = TextWrapping.Wrap });
                    var amt = new TextBlock
                    {
                        Text = FormatBytes(pc.Item2) + " · " + pc.Item3 + " files",
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
            var muted = (Brush)FindResource("DkMutedBrush");
            var normal = (Brush)FindResource("DkTextBrush");
            int i = 0;
            foreach (var app in _debloatApps)
            {
                if (i >= DebloatPanel.Children.Count) break;
                var cb = (CheckBox)DebloatPanel.Children[i++];
                cb.IsEnabled = app.Installed;
                cb.IsChecked = false;
                cb.Content = app.Name + (app.Installed ? "" : "  (not installed)");
                cb.Foreground = app.Installed ? normal : muted;
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
            if (apps.Count == 0)
            {
                DefenderAppsPanel.Children.Add(new TextBlock
                {
                    Text = "No known apps detected on this PC.",
                    Foreground = (Brush)FindResource("DkMutedBrush")
                });
            }
            foreach (var app in apps)
            {
                bool already = excluded.Contains(DefenderExclusionService.Normalize(app.Path));
                var cb = new CheckBox
                {
                    Tag = app.Path,
                    Margin = new Thickness(0, 2, 0, 2),
                    VerticalAlignment = VerticalAlignment.Center
                };
                var label = new StackPanel { Orientation = Orientation.Horizontal };
                label.Children.Add(new TextBlock { Text = app.Name, FontWeight = FontWeights.SemiBold });
                label.Children.Add(new TextBlock
                {
                    Text = "  " + CensorUserName(app.Path),
                    Foreground = (Brush)FindResource("DkMutedBrush"),
                    TextTrimming = TextTrimming.CharacterEllipsis
                });
                if (already)
                {
                    label.Children.Add(new TextBlock
                    {
                        Text = "  (excluded)",
                        Foreground = (Brush)FindResource("DkAccentBrush")
                    });
                    cb.IsChecked = true;
                    cb.IsEnabled = false;
                }
                else
                {
                    cb.IsChecked = true;
                }
                cb.Content = label;
                DefenderAppsPanel.Children.Add(cb);
            }

            DefenderExclusionsList.Items.Clear();
            foreach (string p in exclusions)
                DefenderExclusionsList.Items.Add(CensorUserName(p));
            DefenderStatusLabel.Text = apps.Count == 0
                ? "No known apps found. You can still add folders manually below."
                : "Found " + apps.Count + " known app(s). Uncheck any you don't want excluded.";
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
