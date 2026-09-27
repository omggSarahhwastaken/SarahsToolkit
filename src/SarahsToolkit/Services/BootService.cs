using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SarahsToolkit.Services;

namespace SarahsToolkit.Services
{
    public class BootStats
    {
        public double LatestSec { get; set; } = double.NaN; // most recent boot duration
        public double AvgSec { get; set; } = double.NaN;    // average of samples
        public int SampleCount { get; set; }
    }

    /// <summary>
    /// Reads boot performance from the Diagnostics-Performance event log
    /// (event 100 carries BootDuration in ms) and the last boot time from WMI.
    /// Everything is best-effort: missing logs degrade to NaN, never throw.
    /// </summary>
    public sealed class BootService
    {
        private static readonly Regex BootDurationRe =
            new Regex(@"Name=""BootDuration"">(\d+)<", RegexOptions.Compiled);

        public Task<BootStats> GetBootStatsAsync(int maxSamples = 10)
        {
            return Task.Run(() =>
            {
                var stats = new BootStats();
                var samples = new List<(DateTime when, double sec)>();
                try
                {
                    var query = new EventLogQuery(
                        "Microsoft-Windows-Diagnostics-Performance/Operational",
                        PathType.LogName,
                        "*[System[(EventID=100)]]");
                    using (var reader = new EventLogReader(query))
                    {
                        EventRecord rec;
                        int read = 0;
                        while (read < maxSamples * 3 && (rec = reader.ReadEvent()) != null)
                        {
                            read++;
                            using (rec)
                            {
                                if (rec.TimeCreated == null) continue;
                                string xml;
                                try { xml = rec.ToXml(); }
                                catch { continue; }
                                var m = BootDurationRe.Match(xml);
                                if (!m.Success) continue;
                                if (!long.TryParse(m.Groups[1].Value, out long ms)) continue;
                                samples.Add((rec.TimeCreated.Value, ms / 1000.0));
                                if (samples.Count >= maxSamples) break;
                            }
                        }
                    }
                }
                catch { /* log unavailable (old Windows, permissions) */ }

                if (samples.Count > 0)
                {
                    var ordered = samples.OrderByDescending(s => s.when).ToList();
                    stats.LatestSec = ordered[0].sec;
                    stats.AvgSec = ordered.Average(s => s.sec);
                    stats.SampleCount = ordered.Count;
                }
                return stats;
            });
        }

        public Task<DateTime> GetLastBootTimeAsync()
        {
            return Task.Run(() =>
            {
                try
                {
                    var r = PowerShellRunner.RunScript(
                        "(Get-CimInstance Win32_OperatingSystem -ErrorAction SilentlyContinue).LastBootUpTime",
                        1);
                    string t = (r.Output ?? "").Trim();
                    if (DateTime.TryParse(t, out DateTime dt)) return dt;
                }
                catch { }
                return DateTime.MinValue;
            });
        }
    }
}
