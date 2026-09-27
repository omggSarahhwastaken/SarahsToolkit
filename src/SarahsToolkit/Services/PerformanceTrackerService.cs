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
    /// while one is running. The log is a rolling 150 MB file: when it would
    /// exceed the cap, the oldest entries are trimmed automatically — enough
    /// for well over a month of gameplay history.
    /// </summary>
    public class PerformanceTrackerService
    {
        public const long MaxLogBytes = 150L * 1024 * 1024; // 150 MB hard cap
        private readonly object _logLock = new object();

        /// <summary>When true, temperatures display and log in Fahrenheit.</summary>
        public bool Fahrenheit { get; set; }

        public string FormatTemp(int tempC)
        {
            if (tempC < 0) return "n/a";
            if (Fahrenheit)
                return Math.Round(tempC * 9.0 / 5 + 32) + "F";
            return tempC + "C";
        }

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
            string temp = FormatTemp(s.TempC);
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") +
                " | " + s.Game +
                " | CPU " + s.CpuPct.ToString("0") + "%" +
                " | RAM " + s.RamUsedGb.ToString("0.0") + "/" + s.RamTotalGb.ToString("0.0") + " GB" +
                " | Temp " + temp;
            string path = LogPath;
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            // Disk IO happens off the UI thread: a trim can move ~100 MB and
            // must never freeze the app. A lock keeps concurrent trims safe.
            string lineCopy = line, pathCopy = path;
            Task.Run(() =>
            {
                lock (_logLock)
                {
                    try { AppendLogCore(pathCopy, lineCopy); } catch { }
                }
            });
        }

        private void AppendLogCore(string path, string line)
        {
            var fi = new FileInfo(path);
            byte[] bytes = Encoding.UTF8.GetBytes(line + "\r\n");
            if (fi.Exists && fi.Length + bytes.Length > MaxLogBytes)
            {
                // Rolling cap: keep the newest ~70%, drop the oldest.
                // Stream-based (seek + copy the tail) so a large cap doesn't
                // spike memory; the file is never loaded whole.
                long target = (long)(MaxLogBytes * 0.7);
                string tmpPath = path + ".tmp";
                try
                {
                    using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        long start = Math.Max(0, input.Length - target);
                        input.Seek(start, SeekOrigin.Begin);
                        // Align to the next full line so no partial entry survives.
                        if (start > 0)
                        {
                            int b;
                            while ((b = input.ReadByte()) != -1)
                            {
                                if (b == '\n') break;
                            }
                        }
                        using (var output = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None))
                        {
                            input.CopyTo(output);
                            output.Write(bytes, 0, bytes.Length);
                        }
                    }
                    File.Delete(path);
                    File.Move(tmpPath, path);
                }
                catch
                {
                    try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { }
                    // Fall back to a plain append; the trim will be retried next time.
                    File.AppendAllText(path, line + "\r\n", Encoding.UTF8);
                }
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
