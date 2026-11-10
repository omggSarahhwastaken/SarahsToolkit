using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace SarahsToolkit.Services
{
    /// <summary>
    /// One recorded "before" snapshot for a power tweak, so reverting
    /// restores the user's own plan and values — not assumed Balanced
    /// defaults. One entry per tweak id, written on first apply (never
    /// overwritten while present) and deleted after a successful restore.
    /// Stored beside settings.json.
    /// For a plan-scoped setting the scheme it was captured on is
    /// recorded too, so a restore writes the values back into that plan
    /// even if the active plan has since changed (e.g. a preset switched
    /// plans after the setting was applied).
    ///
    /// v2.7.6: the first version shelled out to powercfg.exe directly and
    /// parsed a targeted "powercfg /q SCHEME_CURRENT sub setting" query.
    /// On the user's PC that query returned no "Current AC/DC Power
    /// Setting Index" lines, so the capture threw and the whole preset
    /// apply failed ("Could not read the current power setting values").
    /// Capture now reads the registry first, falls back to parsing the
    /// full-scheme dump through the same PowerShell runner the check
    /// scripts use, and — most importantly — a capture failure is no
    /// longer fatal: TweakService.Apply swallows it and the tweak still
    /// applies, with revert falling back to the static revert script.
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

        /// <summary>
        /// Records the current plan/values if no backup exists yet. Throws
        /// when the values cannot be read at all — the caller treats that
        /// as "no backup" and applies the tweak anyway (see TweakService).
        /// </summary>
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
                if (!TryReadRegistryValues(scheme, guids.Item1, guids.Item2, out ac, out dc))
                    GetSettingValuesFromDump(guids.Item1, guids.Item2, out ac, out dc);
                backup.AcValue = ac;
                backup.DcValue = dc;
            }
            all[tweakId] = backup;
            SaveAll(all);
        }

        /// <summary>
        /// Restores the recorded values via the PowerShell runner (the same
        /// path the apply/revert scripts use). Returns true when restored
        /// (backup cleared), false when there is no backup or the restore
        /// failed — the caller then falls back to the static revert script.
        /// </summary>
        public static bool TryRestore(string tweakId)
        {
            PowerBackup backup;
            if (!LoadAll().TryGetValue(tweakId, out backup)) return false;
            try
            {
                string script;
                Tuple<string, string> guids;
                if (SettingGuids.TryGetValue(tweakId, out guids))
                {
                    if (backup.AcValue == null || backup.DcValue == null) return false;
                    script =
                        "$ErrorActionPreference = 'Stop'; " +
                        "powercfg /setacvalueindex " + backup.SchemeGuid + " " + guids.Item1 + " " + guids.Item2 + " " + backup.AcValue.Value + "; " +
                        "if ($LASTEXITCODE -ne 0) { throw 'powercfg failed restoring the plugged-in value.' }; " +
                        "powercfg /setdcvalueindex " + backup.SchemeGuid + " " + guids.Item1 + " " + guids.Item2 + " " + backup.DcValue.Value + "; " +
                        "if ($LASTEXITCODE -ne 0) { throw 'powercfg failed restoring the on-battery value.' }; " +
                        // Re-apply the plan if it is the active one, so the
                        // restored values take effect immediately.
                        "if ((powercfg /getactivescheme) -match '" + backup.SchemeGuid + "') { " +
                        "powercfg /setactive " + backup.SchemeGuid + "; " +
                        "if ($LASTEXITCODE -ne 0) { throw 'powercfg failed applying the plan.' } }";
                }
                else
                {
                    script =
                        "$ErrorActionPreference = 'Stop'; " +
                        "powercfg /setactive " + backup.SchemeGuid + "; " +
                        "if ($LASTEXITCODE -ne 0) { throw 'Could not switch back to the previous power plan.' }";
                }
                var r = PowerShellRunner.RunScript(script, 1);
                if (r.ExitCode != 0) return false;
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

        // Active scheme: registry first, powercfg fallback.
        private static string GetActiveSchemeGuid()
        {
            try
            {
                using (var key = Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes", false))
                {
                    object v = key == null ? null : key.GetValue("ActivePowerScheme");
                    string guid = v == null ? null : v.ToString();
                    if (!string.IsNullOrWhiteSpace(guid)) return guid.Trim();
                }
            }
            catch { /* fall through to powercfg */ }
            string script =
                "$m = [regex]::Match((powercfg /getactivescheme), '([0-9a-fA-F-]{36})'); " +
                "if (-not $m.Success) { throw 'Could not read the active power plan.' }; " +
                "Write-Output $m.Groups[1].Value";
            var r = PowerShellRunner.RunScript(script, 1);
            if (r.ExitCode != 0)
                throw new InvalidOperationException("Could not read the active power plan.");
            string outGuid = (r.Output ?? "").Trim();
            if (outGuid.Length < 36)
                throw new InvalidOperationException("Could not read the active power plan.");
            return outGuid;
        }

        // Power plan settings are stored per scheme in the registry; a
        // direct read avoids powercfg output parsing entirely. Returns
        // false when the scheme never had an explicit value for this
        // setting (the key is absent), so the caller can fall back to
        // parsing the powercfg dump.
        private static bool TryReadRegistryValues(string scheme, string subgroup, string setting, out int ac, out int dc)
        {
            ac = 0;
            dc = 0;
            try
            {
                using (var key = Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes\" + scheme + @"\" + subgroup + @"\" + setting, false))
                {
                    if (key == null) return false;
                    object a = key.GetValue("ACSettingIndex");
                    object d = key.GetValue("DCSettingIndex");
                    if (a == null || d == null) return false;
                    ac = Convert.ToInt32(a);
                    dc = Convert.ToInt32(d);
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        // Parses the full-scheme dump ("powercfg /q SCHEME_CURRENT") and
        // reads the AC/DC lines from the section for this setting. The
        // targeted query ("powercfg /q SCHEME_CURRENT sub setting")
        // returned no index lines on the user's PC, so the dump is the
        // fallback — the setting's section is found by its GUID.
        private static void GetSettingValuesFromDump(string subgroup, string setting, out int ac, out int dc)
        {
            string script =
                "$dump = powercfg /q SCHEME_CURRENT; " +
                "$lines = $dump -split \"`r?`n\"; " +
                "$in = $false; $ac = $null; $dc = $null; " +
                "foreach ($l in $lines) { " +
                "if ($l -match 'Power Setting GUID:\\s*" + setting + "') { $in = $true; continue }; " +
                "if ($in -and $l -match 'Power Setting GUID:|Subgroup GUID:') { break }; " +
                "if ($in) { " +
                "$m = [regex]::Match($l, 'Current AC Power Setting Index:\\s*0x([0-9a-fA-F]+)'); " +
                "if ($m.Success) { $ac = [Convert]::ToInt32($m.Groups[1].Value, 16) }; " +
                "$m = [regex]::Match($l, 'Current DC Power Setting Index:\\s*0x([0-9a-fA-F]+)'); " +
                "if ($m.Success) { $dc = [Convert]::ToInt32($m.Groups[1].Value, 16) } } }; " +
                "if ($null -eq $ac -or $null -eq $dc) { throw 'Could not read the current power setting values.' }; " +
                "Write-Output ('AC=' + $ac); Write-Output ('DC=' + $dc)";
            var r = PowerShellRunner.RunScript(script, 1);
            if (r.ExitCode != 0)
                throw new InvalidOperationException("Could not read the current power setting values.");
            ac = 0;
            dc = 0;
            bool gotAc = false, gotDc = false;
            foreach (var line in (r.Output ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string t = line.Trim();
                if (t.StartsWith("AC=", StringComparison.OrdinalIgnoreCase))
                    gotAc = int.TryParse(t.Substring(3), out ac);
                else if (t.StartsWith("DC=", StringComparison.OrdinalIgnoreCase))
                    gotDc = int.TryParse(t.Substring(3), out dc);
            }
            if (!gotAc || !gotDc)
                throw new InvalidOperationException("Could not read the current power setting values.");
        }
    }
}
