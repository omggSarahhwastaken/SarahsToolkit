using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;
using SarahsToolkit.Models;

namespace SarahsToolkit.Services
{
    public class TweakService
    {
        private readonly string _jsonPath;

        // State cache (finding 2): every script-tweak check launches a
        // powershell.exe. Without a cache, one Optimize build + one
        // preset-ratings refresh launched ~29 of them on the UI thread.
        // Entries live 10 s — long enough that a single refresh reads
        // each tweak once, short enough that the toggles never show
        // visibly stale state. Applies/reverts invalidate (below).
        private class CachedState
        {
            public TweakState State { get; set; }
            public string Error { get; set; }
            public DateTime At { get; set; }
        }

        private readonly Dictionary<string, CachedState> _stateCache =
            new Dictionary<string, CachedState>(StringComparer.OrdinalIgnoreCase);
        private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(10);
        // State reads get a short timeout: a hung check must not freeze
        // the UI for a minute per tweak.
        private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(15);

        public TweakService()
        {
            _jsonPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", "tweaks.json");
        }

        public List<TweakDefinition> LoadTweaks()
        {
            var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var json = File.ReadAllText(_jsonPath);
            return JsonSerializer.Deserialize<List<TweakDefinition>>(json, opts) ?? new List<TweakDefinition>();
        }

        public TweakState GetState(TweakDefinition tweak)
        {
            CachedState cached;
            if (_stateCache.TryGetValue(tweak.Id, out cached) &&
                DateTime.UtcNow - cached.At < CacheTtl)
                return cached.State;

            TweakState state;
            string error = "";
            try
            {
                if (!string.IsNullOrWhiteSpace(tweak.CheckScript))
                {
                    var r = RunCheckScript(tweak.CheckScript);
                    // -1 (runner failure) and 99 (timeout) both mean the
                    // state could not be read — Unknown, not NotApplied.
                    if (r.ExitCode == -1 || r.ExitCode == 99)
                    {
                        state = TweakState.Unknown;
                        error = r.Error;
                    }
                    else
                    {
                        state = r.ExitCode == 0 ? TweakState.Applied : TweakState.NotApplied;
                    }
                }
                else
                {
                    var c = tweak.Check;
                    if (c == null)
                    {
                        state = TweakState.Unknown;
                        error = "no check defined for this tweak";
                    }
                    else
                    {
                        string actual = ReadValue(c.Hive, c.KeyPath, c.ValueName, c.Kind);
                        if (actual == null) state = TweakState.NotApplied;
                        else state = string.Equals(actual, c.Expected, StringComparison.OrdinalIgnoreCase)
                            ? TweakState.Applied
                            : TweakState.NotApplied;
                    }
                }
            }
            catch (Exception ex)
            {
                state = TweakState.Unknown;
                error = ex.GetType().Name + ": " + ex.Message;
            }
            _stateCache[tweak.Id] = new CachedState { State = state, Error = error, At = DateTime.UtcNow };
            return state;
        }

        /// <summary>Returns the underlying read error when GetState reports Unknown, else "".</summary>
        public string GetStateError(TweakDefinition tweak)
        {
            // GetState already ran the check and cached its error, so this
            // is a cache hit in the normal path — it must not launch a
            // second PowerShell just to explain an Unknown state.
            CachedState cached;
            if (_stateCache.TryGetValue(tweak.Id, out cached) &&
                DateTime.UtcNow - cached.At < CacheTtl)
                return cached.Error ?? "";
            GetState(tweak);
            if (_stateCache.TryGetValue(tweak.Id, out cached))
                return cached.Error ?? "";
            return "";
        }

        public void Apply(TweakDefinition tweak)
        {
            InvalidateCache(tweak);
            // Record the user's own plan/values before the first apply, so
            // reverting restores those instead of assumed defaults. This is
            // best-effort: a backup makes revert exact, but a failed read
            // must never block the tweak itself (v2.7.5 bug: a powercfg
            // parse failure made the whole preset apply fail). Without a
            // backup, revert falls back to the static revert script.
            if (PowerBackupService.IsPowerTweak(tweak.Id))
            {
                try { PowerBackupService.CaptureIfAbsent(tweak.Id); }
                catch { /* no backup — revert uses the static script */ }
            }
            // Script first, registry second: if the script fails, no
            // registry change has been made yet (no half-applied tweak).
            RunCheckedScript(tweak.ApplyScript);
            foreach (var op in tweak.Apply) WriteOperation(op);
        }

        public void Revert(TweakDefinition tweak)
        {
            InvalidateCache(tweak);
            // A recorded backup restores the user's own plan/values. Without
            // one, fall back to the tweak's static revert script.
            bool restored = PowerBackupService.IsPowerTweak(tweak.Id) &&
                PowerBackupService.TryRestore(tweak.Id);
            if (!restored)
            {
                RunCheckedScript(tweak.RevertScript);
                if (PowerBackupService.IsPowerTweak(tweak.Id))
                    PowerBackupService.Clear(tweak.Id);
            }
            foreach (var op in tweak.Revert) WriteOperation(op);
        }

        private void InvalidateCache(TweakDefinition tweak)
        {
            // A power-tweak apply/revert can change what the other power
            // checks report (the active plan moved), so clear everything.
            if (PowerBackupService.IsPowerTweak(tweak.Id))
                _stateCache.Clear();
            else
                _stateCache.Remove(tweak.Id);
        }

        // Scripts run through the temp-file runner (file deleted afterwards).
        private static PowerShellResult RunScript(string script)
        {
            return PowerShellRunner.RunScript(script, 1);
        }

        private static PowerShellResult RunCheckScript(string script)
        {
            return PowerShellRunner.RunScript(script, CheckTimeout);
        }

        private static void RunCheckedScript(string script)
        {
            if (string.IsNullOrWhiteSpace(script)) return;
            var r = RunScript(script);
            if (r.ExitCode != 0)
                throw new InvalidOperationException(
                    "Script failed (exit " + r.ExitCode + "): " +
                    (string.IsNullOrWhiteSpace(r.Error) ? r.Output : r.Error).Trim());
        }

        private static RegistryKey HiveRoot(string hive)
        {
            return string.Equals(hive, "HKLM", StringComparison.OrdinalIgnoreCase)
                ? Registry.LocalMachine
                : Registry.CurrentUser;
        }

        private static string ReadValue(string hive, string keyPath, string valueName, string kind)
        {
            using (var key = HiveRoot(hive).OpenSubKey(keyPath, false))
            {
                if (key == null) return null;
                string name = valueName == "(Default)" ? "" : valueName;
                object v = key.GetValue(name);
                if (v == null) return null;
                if (string.Equals(kind, "DWord", StringComparison.OrdinalIgnoreCase))
                {
                    // Be tolerant: if the value exists but isn't a clean DWORD (some tools
                    // write these as strings/binary), don't blow up into Unknown — fall back
                    // to the raw form so the toggle stays clickable and Apply can fix it.
                    try { return Convert.ToUInt32(v).ToString(); }
                    catch { return v.ToString(); }
                }
                return v.ToString();
            }
        }

        private static void WriteOperation(RegistryOperation op)
        {
            using (var key = HiveRoot(op.Hive).CreateSubKey(op.KeyPath))
            {
                if (key == null)
                    throw new InvalidOperationException("Cannot open registry key: " + op.KeyPath);
                string name = op.ValueName == "(Default)" ? "" : op.ValueName;
                if (op.Data == null)
                {
                    key.DeleteValue(name, false);
                    return;
                }
                if (string.Equals(op.Kind, "DWord", StringComparison.OrdinalIgnoreCase))
                    key.SetValue(name, unchecked((int)uint.Parse(op.Data)), RegistryValueKind.DWord);
                else
                    key.SetValue(name, op.Data, RegistryValueKind.String);
            }
        }
    }
}
