using System;
using System.IO;
using System.Text.Json;

namespace SarahsToolkit.Services
{
    /// <summary>
    /// App settings: a small JSON file under %LocalAppData%\SarahsToolkit.
    /// No registry, no logs — one file, loaded at launch and saved on change.
    /// </summary>
    public class SettingsService
    {
        public string FolderPath { get; }
        public string SettingsPath { get; }
        public AppSettings Settings { get; private set; } = new AppSettings();

        public SettingsService()
        {
            FolderPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SarahsToolkit");
            SettingsPath = Path.Combine(FolderPath, "settings.json");
        }

        public void Load()
        {
            try
            {
                if (File.Exists(SettingsPath))
                {
                    var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath));
                    if (s != null) Settings = s;
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
                File.WriteAllText(SettingsPath, json);
            }
            catch { /* settings are best-effort */ }
        }

        public void Reset()
        {
            Settings = new AppSettings();
            Save();
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
    }
}
