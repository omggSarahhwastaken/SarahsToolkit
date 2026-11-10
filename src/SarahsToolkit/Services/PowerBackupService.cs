using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SarahsToolkit.Services
{
    /// <summary>
    /// One recorded "before" snapshot for a power tweak, so reverting
    /// restores the user's own plan and values — not assumed Balanced
    /// defaults (finding 5). One entry per tweak id, written on first
    /// apply (never overwritten while present) and deleted after a
    /// successful restore. Stored beside settings.json.
    /// For a plan-scoped setting the scheme it was captured on is
    /// recorded too, so a restore writes the values back into that plan
    /// even if the active plan has since changed (e.g. a preset switched
    /// plans after the setting was applied).
    /// </summary>
    public class PowerBackup
    {
        public string SchemeGuid { get; set; }
        public int? AcValue { get; set; } // null for ultimatePerf (scheme-only)
        public int? DcValue { get; set; }
    }

    public static class PowerBackupService
    {
        // tweak id -> (power subgroup GUID, setting GUID). ultimatePerf is
        // scheme-only and is not in this map.
        private static readonly Dictionary<string, Tuple<string, string>> SettingGuids =
            new Dictionary<string, Tuple<string, string>>(StringComparer.OrdinalIgnoreCase)
            {
                { "power.coreParking", Tuple.Create("54533251-82be-4824-96c1-47b60b740d00", "0cc5b647-c1df-4637-891a-dec35c318583") },
                { "power.pcieAspm", Tuple.Create("501a4d13-42af-4429-9fd1-a8218c268e20", "ee12f906-d277-404b-b6da-e5fa1a576df5") },
                { "power.usbSuspend", Tuple.Create("2a737441-1930-4402-8d77-b2bebba308a3", "48e6b7a6-50f5-4782-a5d4-53bb8f07e226") },
                { "power.wifiPerf", Tuple.Create("19cbb8fa-5279-450e-9fac-8a3d5fedd0c1", "12bbebe6-58d6-4636-95bb-3217ef867c1a") },
            };

        public static bool IsPowerTweak(string id)
        {
            return string.Equals(id, "power.ultimatePerf", StringComparison.OrdinalIgnoreCase)
                || SettingGuids.ContainsKey(id ?? "");
        }

        private static string BackupPath
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "SarahsToolkit", "power-backup.json");
            }
        }

        public static bool HasBackup(string tweakId)
        {
            return LoadAll().ContainsKey(tweakId);
        }

        /// <summary>Records the current plan/values if no backup exists yet.</summary>
        public static void CaptureIfAbsent(string tweakId)
        {
            var all = LoadAll();
            if (all.ContainsKey(tweakId)) return;
            string scheme = GetActiveSchemeGuid();
            var backup = new PowerBackup { SchemeGuid = scheme };
            Tuple<string, string> guids;
            if (SettingGuids.TryGetValue(tweakId, out guids))
            {
                int ac, dc;
                GetSettingValues(guids.Item1, guids.Item2, out ac, out dc);
                backup.AcValue = ac;
                backup.DcValue = dc;
            }
            all[tweakId] = backup;
            SaveAll(all);
        }

        /// <summary>
        /// Restores the recorded values. Returns true when restored (backup
        /// cleared), false when there is no backup or the restore failed —
        /// the caller then falls back to the tweak's static revert script.
        /// </summary>
        public static bool TryRestore(string tweakId)
        {
            PowerBackup backup;
            if (!LoadAll().TryGetValue(tweakId, out backup)) return false;
            try
            {
                Tuple<string, string> guids;
                if (SettingGuids.TryGetValue(tweakId, out guids))
                {
                    if (backup.AcValue == null || backup.DcValue == null) return false;
                    RunPowerCfg("/setacvalueindex " + backup.SchemeGuid + " " + guids.Item1 + " " + guids.Item2 + " " + backup.AcValue.Value);
                    RunPowerCfg("/setdcvalueindex " + backup.SchemeGuid + " " + guids.Item1 + " " + guids.Item2 + " " + backup.DcValue.Value);
                    // Re-apply the plan if it is the active one, so the
                    // restored values take effect immediately.
                    if (string.Equals(GetActiveSchemeGuid(), backup.SchemeGuid, StringComparison.OrdinalIgnoreCase))
                        RunPowerCfg("/setactive " + backup.SchemeGuid);
                }
                else
                {
                    RunPowerCfg("/setactive " + backup.SchemeGuid);
                }
            }
            catch
            {
                return false;
            }
            Clear(tweakId);
            return true;
        }

        /// <summary>Clears the backup (after a restore, or a successful fallback revert).</summary>
        public static void Clear(string tweakId)
        {
            var all = LoadAll();
            if (all.Remove(tweakId)) SaveAll(all);
        }

        private static Dictionary<string, PowerBackup> LoadAll()
        {
            try
            {
                if (!File.Exists(BackupPath)) return new Dictionary<string, PowerBackup>();
                var d = JsonSerializer.Deserialize<Dictionary<string, PowerBackup>>(File.ReadAllText(BackupPath));
                return d ?? new Dictionary<string, PowerBackup>();
            }
            catch
            {
                return new Dictionary<string, PowerBackup>();
            }
        }

        private static void SaveAll(Dictionary<string, PowerBackup> all)
        {
            string folder = Path.GetDirectoryName(BackupPath);
            Directory.CreateDirectory(folder);
            string tmp = BackupPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }));
            if (File.Exists(BackupPath)) File.Replace(tmp, BackupPath, null);
            else File.Move(tmp, BackupPath);
        }

        private static string GetActiveSchemeGuid()
        {
            string output = RunPowerCfg("/getactivescheme");
            var m = Regex.Match(output, @"([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})");
            if (!m.Success)
                throw new InvalidOperationException("Could not read the active power plan.");
            return m.Groups[1].Value;
        }

        private static void GetSettingValues(string subgroup, string setting, out int ac, out int dc)
        {
            string output = RunPowerCfg("/q SCHEME_CURRENT " + subgroup + " " + setting);
            var acMatch = Regex.Match(output, @"Current AC Power Setting Index:\s*0x([0-9a-fA-F]+)");
            var dcMatch = Regex.Match(output, @"Current DC Power Setting Index:\s*0x([0-9a-fA-F]+)");
            if (!acMatch.Success || !dcMatch.Success)
                throw new InvalidOperationException("Could not read the current power setting values.");
            ac = Convert.ToInt32(acMatch.Groups[1].Value, 16);
            dc = Convert.ToInt32(dcMatch.Groups[1].Value, 16);
        }

        private static string RunPowerCfg(string args)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powercfg.exe",
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using (var p = Process.Start(psi))
            {
                if (p == null)
                    throw new InvalidOperationException("Could not start powercfg.exe.");
                string output = p.StandardOutput.ReadToEnd();
                string error = p.StandardError.ReadToEnd();
                if (!p.WaitForExit(15000))
                {
                    try { p.Kill(); } catch { }
                    throw new InvalidOperationException("powercfg.exe timed out.");
                }
                if (p.ExitCode != 0)
                    throw new InvalidOperationException("powercfg.exe failed: " + error.Trim());
                return output;
            }
        }
    }
}
