using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SarahsToolkit.Services
{
    public class PerfSample
    {
        public string Game = "";      // friendly name, empty when no known game is running
        public double CpuPct;
        public double RamUsedGb;
        public double RamTotalGb;
        public int TempC = -1;        // -1 means the temperature sensor was unavailable
    }

    /// <summary>
    /// Watches for known game processes and logs CPU / RAM / temperature
    /// while one is running. The log is a rolling 1 MB file: when it would
    /// exceed the cap, the oldest entries are trimmed automatically.
    /// </summary>
    public class PerformanceTrackerService
    {
        public const long MaxLogBytes = 1024 * 1024; // 1 MB hard cap

        private static readonly (string proc, string name)[] GameProcs =
        {
            ("FortniteClient-Win64-Shipping", "Fortnite"),
            ("RobloxPlayerBeta", "Roblox"),
            ("VRChat", "VRChat"),
            ("DCS", "DCS World"),
            ("GTA5", "GTA V"),
        };

        public string LogPath
        {
            get
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "SarahsToolkit");
                return Path.Combine(dir, "perf.log");
            }
        }

        public Task<PerfSample> SampleAsync()
        {
            return Task.Run(() =>
            {
                var s = new PerfSample();
                var sb = new StringBuilder();
                sb.Append("$procs = @(");
                sb.Append(string.Join(",", GameProcs.Select(g => "'" + g.proc + "'")));
                sb.Append("); ");
                sb.Append("$hit = Get-Process -Name $procs -ErrorAction SilentlyContinue | Select-Object -First 1; ");
                sb.Append("$game = ''; ");
                sb.Append("if ($hit) { $pn = $hit.ProcessName; ");
                sb.Append("$map = @{");
                sb.Append(string.Join(";", GameProcs.Select(g =>
                    "'" + g.proc + "'='" + g.name + "'")));
                sb.Append("}; $game = $map[$pn] }; ");
                // Minecraft Java runs as javaw (shared with other Java apps),
                // so only count it when its window title says Minecraft.
                sb.Append("if (-not $game) { $mc = Get-Process -Name 'javaw' -ErrorAction SilentlyContinue | ");
                sb.Append("Where-Object { $_.MainWindowTitle -match 'Minecraft' } | Select-Object -First 1; ");
                sb.Append("if ($mc) { $game = 'Minecraft Java' } }; ");
                sb.Append("$cpu = [math]::Round((Get-Counter '\\Processor(_Total)\\% Processor Time' ");
                sb.Append("-SampleInterval 1 -MaxSamples 1 -ErrorAction SilentlyContinue).CounterSamples[0].CookedValue, 1); ");
                sb.Append("$os = Get-CimInstance Win32_OperatingSystem -ErrorAction SilentlyContinue; ");
                sb.Append("$temp = -1; try { ");
                sb.Append("$tz = Get-CimInstance MSAcpi_ThermalZoneTemperature -Namespace root/wmi -ErrorAction Stop; ");
                sb.Append("if ($tz) { $temp = [math]::Round((($tz | Measure-Object CurrentTemperature -Maximum).Maximum / 10) - 273.15) } ");
                sb.Append("} catch { }; ");
                sb.Append("Write-Output ('GAME=' + $game); ");
                sb.Append("Write-Output ('CPU=' + $cpu); ");
                sb.Append("Write-Output ('RAMUSED=' + [math]::Round(($os.TotalVisibleMemorySize - $os.FreePhysicalMemory) / 1MB, 1)); ");
                sb.Append("Write-Output ('RAMTOTAL=' + [math]::Round($os.TotalVisibleMemorySize / 1MB, 1)); ");
                sb.Append("Write-Output ('TEMP=' + $temp); ");
                PowerShellResult r = PowerShellRunner.RunScript(sb.ToString(), 2);
                foreach (var line in (r.Output ?? "").Split(
                    new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string t = line.Trim();
                    int eq = t.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = t.Substring(0, eq), val = t.Substring(eq + 1);
                    switch (key)
                    {
                        case "GAME": s.Game = val; break;
                        case "CPU": double.TryParse(val, out double c); s.CpuPct = c; break;
                        case "RAMUSED": double.TryParse(val, out double ru); s.RamUsedGb = ru; break;
                        case "RAMTOTAL": double.TryParse(val, out double rt); s.RamTotalGb = rt; break;
                        case "TEMP": int.TryParse(val, out int tp); s.TempC = tp; break;
                    }
                }
                return s;
            });
        }

        public void AppendLog(PerfSample s)
        {
            string temp = s.TempC >= 0 ? s.TempC + "C" : "n/a";
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") +
                " | " + s.Game +
                " | CPU " + s.CpuPct.ToString("0") + "%" +
                " | RAM " + s.RamUsedGb.ToString("0.0") + "/" + s.RamTotalGb.ToString("0.0") + " GB" +
                " | Temp " + temp;
            string path = LogPath;
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var fi = new FileInfo(path);
            byte[] bytes = Encoding.UTF8.GetBytes(line + "\r\n");
            if (fi.Exists && fi.Length + bytes.Length > MaxLogBytes)
            {
                // Rolling cap: drop the oldest lines until we're back under 70%.
                var lines = File.ReadAllLines(path, Encoding.UTF8).ToList();
                long size = fi.Length;
                long target = (long)(MaxLogBytes * 0.7);
                int drop = 0;
                while (drop < lines.Count && size > target)
                {
                    size -= Encoding.UTF8.GetByteCount(lines[drop]) + 2;
                    drop++;
                }
                lines = lines.Skip(drop).ToList();
                lines.Add(line);
                File.WriteAllLines(path, lines, Encoding.UTF8);
            }
            else
            {
                File.AppendAllText(path, line + "\r\n", Encoding.UTF8);
            }
        }

        public void ClearLog()
        {
            try { File.Delete(LogPath); } catch { }
        }
    }
}
