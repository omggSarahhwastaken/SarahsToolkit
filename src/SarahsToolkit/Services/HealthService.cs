using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;
using SarahsToolkit.Models;

namespace SarahsToolkit.Services
{
    // System health: battery report, storage health, Windows Update status
    // and one-click maintenance (ReTrim, component cleanup, SFC).
    // Everything runs on background threads; failures degrade to
    // "unavailable" instead of breaking the page.
    public sealed class HealthService
    {
        // ---------- Battery ----------

        public Task<BatteryInfo> GetBatteryInfoAsync()
        {
            return Task.Run(() =>
            {
                var info = new BatteryInfo();
                string xmlPath = Path.Combine(Path.GetTempPath(),
                    "stk_battery_" + Guid.NewGuid().ToString("N") + ".xml");
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = "powercfg.exe",
                        Arguments = "/batteryreport /xml /output \"" + xmlPath + "\"",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };
                    using (var p = Process.Start(psi))
                    {
                        if (p == null) return info;
                        p.WaitForExit(30000);
                    }
                    if (!File.Exists(xmlPath)) return info;
                    var doc = XDocument.Load(xmlPath);
                    var battery = doc.Descendants("Battery").FirstOrDefault();
                    if (battery == null) return info; // desktop, no battery
                    info.HasBattery = true;
                    info.DesignCapacityMwh = ParseLong(battery.Element("DesignCapacity")?.Value);
                    info.FullChargeCapacityMwh = ParseLong(battery.Element("FullChargeCapacity")?.Value);
                    info.CycleCount = ParseLong(battery.Element("CycleCount")?.Value, -1);
                    if (info.DesignCapacityMwh > 0 && info.FullChargeCapacityMwh > 0)
                        info.HealthPercent = (double)info.FullChargeCapacityMwh /
                                             info.DesignCapacityMwh * 100.0;
                }
                catch { /* best-effort */ }
                finally
                {
                    try { if (File.Exists(xmlPath)) File.Delete(xmlPath); } catch { }
                }

                // Current charge level + power state via WMI (fast).
                try
                {
                    var r = PowerShellRunner.RunScript(
                        "$b = Get-CimInstance Win32_Battery -ErrorAction SilentlyContinue | Select-Object -First 1; " +
                        "if ($b) { \"$($b.EstimatedChargeRemaining)|$($b.BatteryStatus)\" }", 1);
                    var parts = (r.Output ?? "").Trim().Split('|');
                    if (parts.Length == 2)
                    {
                        if (int.TryParse(parts[0].Trim(), out int pct)) info.ChargePercent = pct;
                        info.PowerState = BatteryStatusText(parts[1].Trim());
                    }
                }
                catch { }
                return info;
            });
        }

        private static string BatteryStatusText(string code)
        {
            switch (code)
            {
                case "1": return "Discharging";
                case "2": return "On AC power";
                case "3": return "Fully charged";
                case "4": return "Low";
                case "5": return "Critical";
                case "6": return "Charging";
                case "7": return "Charging (high)";
                case "8": return "Charging (low)";
                case "9": return "Charging (critical)";
                case "11": return "Partially charged";
                default: return "";
            }
        }

        private static long ParseLong(string s, long fallback = 0)
        {
            return long.TryParse((s ?? "").Trim(), out long v) ? v : fallback;
        }

        // ---------- Storage health ----------

        public Task<List<DiskHealthInfo>> GetDiskHealthAsync()
        {
            return Task.Run(() =>
            {
                var list = new List<DiskHealthInfo>();
                try
                {
                    string script =
                        "$disks = Get-PhysicalDisk -ErrorAction SilentlyContinue\n" +
                        "foreach ($d in $disks) {\n" +
                        "    $c = $d | Get-StorageReliabilityCounter -ErrorAction SilentlyContinue\n" +
                        "    $wear = ''; $temp = ''\n" +
                        "    if ($c) { $wear = $c.Wear; $temp = $c.Temperature }\n" +
                        "    @($d.FriendlyName, $d.MediaType, $d.HealthStatus, $d.Size, $wear, $temp) -join \"`t\"\n" +
                        "}";
                    var r = PowerShellRunner.RunScript(script, 2);
                    foreach (var line in (r.Output ?? "").Split(
                        new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        var p = line.Split('\t');
                        if (p.Length < 4) continue;
                        var d = new DiskHealthInfo
                        {
                            Name = p[0].Trim(),
                            MediaType = p[1].Trim(),
                            HealthStatus = p[2].Trim()
                        };
                        if (long.TryParse(p[3].Trim(), out long size)) d.SizeBytes = size;
                        if (p.Length > 4 && int.TryParse(p[4].Trim(), out int wear)) d.WearPercent = wear;
                        if (p.Length > 5 && int.TryParse(p[5].Trim(), out int temp)) d.TemperatureC = temp;
                        if (!string.IsNullOrWhiteSpace(d.Name)) list.Add(d);
                    }
                }
                catch { /* best-effort */ }
                return list;
            });
        }

        // ---------- Windows Update ----------

        public Task<WindowsUpdateInfo> GetWindowsUpdateInfoAsync()
        {
            return Task.Run(() =>
            {
                var info = new WindowsUpdateInfo();
                try
                {
                    string script =
                        "try {\n" +
                        "    $s = New-Object -ComObject Microsoft.Update.Session\n" +
                        "    $r = $s.CreateUpdateSearcher().Search('IsInstalled=0 and IsHidden=0')\n" +
                        "    $r.Updates.Count\n" +
                        "} catch { 'ERR' }";
                    var r = PowerShellRunner.RunScript(script, 5);
                    if (int.TryParse((r.Output ?? "").Trim(), out int n))
                    {
                        info.PendingCount = n;
                        info.Checked = true;
                    }
                }
                catch { /* best-effort */ }
                return info;
            });
        }

        // ---------- Maintenance actions ----------

        public Task<MaintenanceResult> RunRetrimAsync()
        {
            return Task.Run(() =>
            {
                string script =
                    "$letters = Get-Partition -ErrorAction SilentlyContinue | Where-Object { $_.DriveLetter } | ForEach-Object {\n" +
                    "    $pd = $_ | Get-Disk -ErrorAction SilentlyContinue | Get-PhysicalDisk -ErrorAction SilentlyContinue\n" +
                    "    if ($pd -and $pd.MediaType -eq 'SSD') { $_.DriveLetter }\n" +
                    "} | Sort-Object -Unique\n" +
                    "if (-not $letters) { 'NO_SSD' }\n" +
                    "else {\n" +
                    "    foreach ($l in $letters) {\n" +
                    "        try { Optimize-Volume -DriveLetter $l -ReTrim -ErrorAction Stop | Out-Null; \"OK:$l\" }\n" +
                    "        catch { \"FAIL:$l\" }\n" +
                    "    }\n" +
                    "}";
                var r = PowerShellRunner.RunScript(script, 10);
                var lines = (r.Output ?? "").Split(
                    new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                var done = new List<string>();
                var failed = new List<string>();
                foreach (var line in lines)
                {
                    var t = line.Trim();
                    if (t.StartsWith("OK:")) done.Add(t.Substring(3));
                    else if (t.StartsWith("FAIL:")) failed.Add(t.Substring(5));
                    else if (t == "NO_SSD")
                        return new MaintenanceResult { Success = true, Summary = "No SSDs found — nothing to trim." };
                }
                if (done.Count == 0 && failed.Count == 0)
                    return new MaintenanceResult { Success = false, Summary = "Couldn't run ReTrim. Is the drive an SSD?" };
                string summary = "Trimmed " + string.Join(", ", done) +
                    (failed.Count > 0 ? "; failed: " + string.Join(", ", failed) : "") + ".";
                return new MaintenanceResult { Success = failed.Count == 0, Summary = summary };
            });
        }

        public Task<MaintenanceResult> RunComponentCleanupAsync()
        {
            return Task.Run(() =>
            {
                var r = PowerShellRunner.RunScript(
                    "DISM /Online /Cleanup-Image /StartComponentCleanup", 30);
                string all = ((r.Output ?? "") + "\n" + (r.Error ?? "")).Trim();
                if (r.ExitCode == 0 && all.IndexOf("successfully",
                    StringComparison.OrdinalIgnoreCase) >= 0)
                    return new MaintenanceResult
                    {
                        Success = true,
                        Summary = "Component store cleaned up successfully."
                    };
                if (r.ExitCode == 99)
                    return new MaintenanceResult { Success = false, Summary = "Timed out after 30 minutes." };
                return new MaintenanceResult
                {
                    Success = false,
                    Summary = "DISM reported a problem. " + FirstLine(all)
                };
            });
        }

        public Task<MaintenanceResult> RunSfcAsync()
        {
            return Task.Run(() =>
            {
                var r = PowerShellRunner.RunScript("sfc /scannow", 30);
                string all = ((r.Output ?? "") + "\n" + (r.Error ?? "")).Trim();
                if (all.IndexOf("did not find any integrity violations",
                    StringComparison.OrdinalIgnoreCase) >= 0)
                    return new MaintenanceResult
                    {
                        Success = true,
                        Summary = "No integrity violations found — system files are healthy."
                    };
                if (all.IndexOf("successfully repaired",
                    StringComparison.OrdinalIgnoreCase) >= 0)
                    return new MaintenanceResult
                    {
                        Success = true,
                        Summary = "Found corrupt files and repaired them. A reboot is recommended."
                    };
                if (all.IndexOf("unable to fix",
                    StringComparison.OrdinalIgnoreCase) >= 0)
                    return new MaintenanceResult
                    {
                        Success = false,
                        Summary = "Found corrupt files it couldn't fix — run DISM RestoreHealth, then SFC again."
                    };
                if (r.ExitCode == 99)
                    return new MaintenanceResult { Success = false, Summary = "Timed out after 30 minutes." };
                return new MaintenanceResult
                {
                    Success = false,
                    Summary = "Scan didn't complete cleanly. " + FirstLine(all)
                };
            });
        }

        private static string FirstLine(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "";
            var line = s.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                        .FirstOrDefault() ?? "";
            return line.Length > 160 ? line.Substring(0, 160) + "…" : line;
        }
    }
}
