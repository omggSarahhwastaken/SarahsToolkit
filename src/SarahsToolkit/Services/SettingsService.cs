using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace SarahsToolkit.Services
{
    /// <summary>
    /// App settings: a small JSON file under %LocalAppData%\SarahsToolkit.
    /// No registry, no logs — one file, loaded at launch and saved on change.
    /// Writes are atomic (temp file + replace) with a .bak of the previous
    /// good file, so killing the app mid-save can never blank the config.
    /// </summary>
    public class SettingsService
    {
        public string FolderPath { get; }
        public string SettingsPath { get; }
        public string BackupPath { get; }
        public AppSettings Settings { get; private set; } = new AppSettings();
        // True when the last Load() parsed an existing file (or its backup).
        public bool LoadedOk { get; private set; }
        // True when a settings file existed at Load() time, even if unreadable.
        public bool HadFile { get; private set; }
        // True when a file existed but neither it nor the backup could be
        // read. While set, Save() refuses to write: a broken load followed by
        // any save used to cement defaults over the user's real config and
        // destroy the only good backup in the process.
        private bool _brokenConfig;

        public SettingsService()
        {
            FolderPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SarahsToolkit");
            SettingsPath = Path.Combine(FolderPath, "settings.json");
            BackupPath = Path.Combine(FolderPath, "settings.json.bak");
        }

        /// <summary>Same service pointed at an explicit folder (Dev round-trip check).</summary>
        public SettingsService(string folderPath)
        {
            FolderPath = folderPath;
            SettingsPath = Path.Combine(FolderPath, "settings.json");
            BackupPath = Path.Combine(FolderPath, "settings.json.bak");
        }

        public void Load()
        {
            LoadedOk = false;
            HadFile = false;
            _brokenConfig = false;
            bool mainOk = false;
            if (File.Exists(SettingsPath))
            {
                HadFile = true;
                try
                {
                    var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath));
                    if (s != null) { Settings = s; mainOk = true; }
                }
                catch { /* corrupt or locked: fall through to the backup */ }
            }
            // The main file can be unreadable for transient reasons (a torn
            // write from a killed save, an AV lock, a full disk). A failure
            // here must NEVER skip the backup — that was the config wiper.
            if (!mainOk && File.Exists(BackupPath))
            {
                HadFile = true;
                try
                {
                    var b = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(BackupPath));
                    if (b != null) { Settings = b; LoadedOk = true; }
                }
                catch { /* backup unreadable too */ }
            }
            else if (mainOk)
            {
                LoadedOk = true;
            }
            _brokenConfig = HadFile && !LoadedOk;
            Sanitize();
        }

        public void Save()
        {
            // Refuse to cement defaults over a config we failed to read.
            if (_brokenConfig) return;
            try
            {
                Sanitize();
                Directory.CreateDirectory(FolderPath);
                var json = JsonSerializer.Serialize(Settings,
                    new JsonSerializerOptions { WriteIndented = true });
                var tmp = SettingsPath + ".tmp";
                File.WriteAllText(tmp, json);
                if (File.Exists(SettingsPath))
                {
                    try { if (File.Exists(BackupPath)) File.Delete(BackupPath); } catch { }
                    File.Replace(tmp, SettingsPath, BackupPath);
                }
                else
                {
                    if (File.Exists(BackupPath)) { try { File.Delete(BackupPath); } catch { } }
                    File.Move(tmp, SettingsPath);
                }
            }
            catch
            {
                // If the atomic write failed, leave the existing files alone.
                // The old plain-overwrite fallback could tear the main file
                // mid-write and that torn file is what started this whole saga.
            }
        }

        public void Reset()
        {
            Settings = new AppSettings();
            _brokenConfig = false;
            LoadedOk = true;
            HadFile = true;
            Save();
        }

        /// <summary>Writes the current settings to a user-chosen file (export).</summary>
        public void ExportTo(string path)
        {
            // A broken config has nothing safe to export: Save() refuses to
            // write while _brokenConfig is set, so copying the on-disk file
            // would hand the user the corrupt file they are trying to escape.
            if (_brokenConfig)
                throw new InvalidOperationException("Settings are unreadable (the settings file is corrupt), so there is nothing safe to export.");
            Save(); // make sure the file on disk is current first
            if (!File.Exists(SettingsPath))
                throw new InvalidOperationException("No settings file exists yet to export.");
            File.Copy(SettingsPath, path, true);
        }

        /// <summary>
        /// Reads settings from a user-chosen file (import). The file must parse
        /// as AppSettings; a bad file throws and the live settings are untouched.
        /// On success the imported settings become live and are saved (with the
        /// usual .bak of the previous config kept by Save()).
        /// </summary>
        public void ImportFrom(string path)
        {
            AppSettings imported;
            string raw;
            try
            {
                raw = File.ReadAllText(path);
                imported = JsonSerializer.Deserialize<AppSettings>(raw);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("That file could not be read as settings: " + ex.Message);
            }
            if (imported == null)
                throw new InvalidOperationException("That file is not a Sarah's Toolkit settings file.");
            // A JSON object with no settings fields at all ({}, a version
            // manifest, a tweaks file) deserializes to all-defaults — and
            // importing that would silently wipe the user's real config.
            // Require at least one recognised settings property to be present.
            if (!LooksLikeSettingsFile(raw))
                throw new InvalidOperationException("That file doesn't look like a Sarah's Toolkit settings export — it has none of the settings fields.");
            Settings = imported;
            _brokenConfig = false;
            LoadedOk = true;
            HadFile = true;
            Save();
        }

        private static readonly string[] SettingsFieldNames =
        {
            "GuiScale", "MinimizeToTray", "CheckUpdatesOnLaunch", "StartWithWindows",
            "DefaultPage", "LastPage", "TempFahrenheit", "LogPerformance", "IdleTempC",
            "BootBaselinePreset", "BootBaselineAvgSec", "BootBaselineBootId", "BootBaselineDate"
        };

        private static bool LooksLikeSettingsFile(string raw)
        {
            try
            {
                using (var doc = JsonDocument.Parse(raw))
                {
                    if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;
                    foreach (var prop in doc.RootElement.EnumerateObject())
                    {
                        foreach (var name in SettingsFieldNames)
                        {
                            if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
                                return true;
                        }
                    }
                }
            }
            catch
            {
                return false;
            }
            return false;
        }

        private void Sanitize()
        {
            var s = Settings;
            if (s.GuiScale < 0.8 || s.GuiScale > 1.5) s.GuiScale = 1.0;
            if (s.DefaultPage != "Home" && s.DefaultPage != "Last") s.DefaultPage = "Home";
            if (string.IsNullOrWhiteSpace(s.LastPage)) s.LastPage = "Home";
        }
    }

    public class AppSettings
    {
        public double GuiScale { get; set; } = 1.0;
        public bool MinimizeToTray { get; set; } = true;
        public bool CheckUpdatesOnLaunch { get; set; } = true;
        public bool StartWithWindows { get; set; } = false;
        public string DefaultPage { get; set; } = "Home"; // Home or Last
        public string LastPage { get; set; } = "Home";
        public bool TempFahrenheit { get; set; } = false;
        public bool LogPerformance { get; set; } = true;
        // Idle temperature baseline (Celsius, -1 = not recorded). Recorded on
        // demand from the Tools page; gaming temps are shown against it.
        public int IdleTempC { get; set; } = -1;
        // Measured boot impact: snapshot taken when a preset is applied, so a
        // later boot can be compared against it.
        public string BootBaselinePreset { get; set; } = "";
        public double BootBaselineAvgSec { get; set; } = -1;
        public string BootBaselineBootId { get; set; } = ""; // LastBootUpTime ticks at apply time
        public string BootBaselineDate { get; set; } = "";
        // Accent colour (hex, e.g. #8B5CF6): buttons, toggles, nav indicator,
        // gauge. Applied at startup and whenever the picker changes it.
        public string AccentColor { get; set; } = "#8B5CF6";
        // Remembered window geometry (0 / false = never saved yet).
        public double WindowWidth { get; set; } = 0;
        public double WindowHeight { get; set; } = 0;
        public double WindowLeft { get; set; } = 0;
        public double WindowTop { get; set; } = 0;
        public bool WindowMaximized { get; set; } = false;
        public bool WindowGeometrySaved { get; set; } = false;
    }
}
