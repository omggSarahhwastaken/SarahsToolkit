using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

        public SettingsService()
        {
            FolderPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SarahsToolkit");
            SettingsPath = Path.Combine(FolderPath, "settings.json");
            BackupPath = Path.Combine(FolderPath, "settings.json.bak");
        }

        public void Load()
        {
            LoadedOk = false;
            HadFile = false;
            try
            {
                if (File.Exists(SettingsPath))
                {
                    HadFile = true;
                    var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath));
                    if (s != null) { Settings = s; LoadedOk = true; }
                }
                // Main file missing or unreadable (e.g. a torn write from a
                // previous run being killed mid-save): try the backup before
                // giving up and falling back to defaults.
                if (!LoadedOk && File.Exists(BackupPath))
                {
                    HadFile = true;
                    var b = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(BackupPath));
                    if (b != null) { Settings = b; LoadedOk = true; }
                }
            }
            catch { /* corrupted file: fall back to defaults */ }
            Sanitize();
        }

        public void Save()
        {
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
                // Last resort: a plain overwrite beats silently losing the change.
                try
                {
                    File.WriteAllText(SettingsPath, JsonSerializer.Serialize(Settings,
                        new JsonSerializerOptions { WriteIndented = true }));
                }
                catch { }
            }
        }

        public void Reset()
        {
            Settings = new AppSettings();
            Save();
            LoadedOk = true;
            HadFile = true;
        }

        private void Sanitize()
        {
            var s = Settings;
            if (s.GuiScale < 0.8 || s.GuiScale > 1.5) s.GuiScale = 1.0;
            if (s.DefaultPage != "Home" && s.DefaultPage != "Last") s.DefaultPage = "Home";
            if (string.IsNullOrWhiteSpace(s.LastPage)) s.LastPage = "Home";
            if (s.AppliedTweaks != null)
                s.AppliedTweaks = s.AppliedTweaks
                    .Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().ToList();
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
        // Tweak ledger: IDs of tweaks the user applied through the app.
        // This is what makes your toggles survive updates: on launch the app
        // compares this list against the live registry and offers to re-apply
        // anything that got reverted elsewhere (e.g. by a Windows update).
        // Null = recorded by an older version; backfilled once from live state.
        public List<string> AppliedTweaks { get; set; }
    }
}
