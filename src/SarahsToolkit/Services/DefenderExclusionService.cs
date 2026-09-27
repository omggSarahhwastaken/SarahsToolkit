using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace SarahsToolkit.Services
{
    public class DefenderKnownApp
    {
        public string Name { get; set; }
        public string Path { get; set; }
    }

    /// <summary>
    /// Manages Windows Defender (Microsoft Defender Antivirus) path exclusions via
    /// Add/Remove/Get-MpPreference. Excluding trusted app folders stops Defender's
    /// real-time scanning from re-scanning gigabytes of known-safe files on every
    /// launch, which is the main reason MsMpEng.exe sits heavy on RAM.
    /// Requires admin, which the app already has. If Tamper Protection blocks a
    /// change, the real error text is surfaced to the user.
    /// </summary>
    public class DefenderExclusionService
    {
        // Display name -> candidate install folders (first existing one wins).
        private static readonly KeyValuePair<string, string[]>[] KnownApps =
        {
            KV("Discord", @"%LOCALAPPDATA%\Discord", @"%LOCALAPPDATA%\DiscordPTB", @"%LOCALAPPDATA%\DiscordCanary"),
            KV("Roblox", @"%LOCALAPPDATA%\Roblox"),
            KV("Spotify", @"%APPDATA%\Spotify"),
            KV("Opera GX", @"%LOCALAPPDATA%\Programs\Opera GX"),
            KV("Opera", @"%LOCALAPPDATA%\Programs\Opera"),
            KV("Google Chrome", @"%PROGRAMFILES%\Google\Chrome\Application", @"%LOCALAPPDATA%\Google\Chrome\Application"),
            KV("Microsoft Edge", @"%PROGRAMFILES(X86)%\Microsoft\Edge\Application", @"%PROGRAMFILES%\Microsoft\Edge\Application"),
            KV("Mozilla Firefox", @"%PROGRAMFILES%\Mozilla Firefox", @"%PROGRAMFILES(X86)%\Mozilla Firefox"),
            KV("Brave", @"%PROGRAMFILES%\BraveSoftware\Brave-Browser\Application"),
            KV("Vivaldi", @"%LOCALAPPDATA%\Vivaldi\Application"),
            KV("Steam", @"%PROGRAMFILES(X86)%\Steam"),
            KV("Epic Games Launcher", @"%PROGRAMFILES%\Epic Games"),
            KV("GOG Galaxy", @"%PROGRAMFILES(X86)%\GOG Galaxy"),
            KV("EA App", @"%PROGRAMFILES%\Electronic Arts\EA Desktop"),
            KV("Ubisoft Connect", @"%PROGRAMFILES(X86)%\Ubisoft\Ubisoft Game Launcher"),
            KV("Battle.net", @"%PROGRAMFILES(X86)%\Battle.net"),
            KV("Riot Client", @"C:\Riot Games"),
            KV("Minecraft Launcher", @"%PROGRAMFILES(X86)%\Minecraft Launcher"),
            KV("Telegram Desktop", @"%APPDATA%\Telegram Desktop"),
            KV("WhatsApp", @"%LOCALAPPDATA%\WhatsApp"),
            KV("OBS Studio", @"%PROGRAMFILES%\obs-studio"),
            KV("FL Studio", @"%PROGRAMFILES%\Image-Line"),
            KV("LMMS", @"%PROGRAMFILES%\LMMS"),
            KV("MSI Afterburner", @"%PROGRAMFILES(X86)%\MSI Afterburner"),
            KV("RivaTuner Statistics Server", @"%PROGRAMFILES(X86)%\RivaTuner Statistics Server"),
            KV("Unity Hub", @"%PROGRAMFILES%\Unity Hub"),
            KV("Blender", @"%PROGRAMFILES%\Blender Foundation"),
        };

        private static KeyValuePair<string, string[]> KV(string name, params string[] paths)
        {
            return new KeyValuePair<string, string[]>(name, paths);
        }

        /// <summary>Known-safe apps actually installed on this PC.</summary>
        public static List<DefenderKnownApp> DetectKnownApps()
        {
            var found = new List<DefenderKnownApp>();
            foreach (var kv in KnownApps)
            {
                foreach (string raw in kv.Value)
                {
                    string path = Environment.ExpandEnvironmentVariables(raw);
                    if (Directory.Exists(path))
                    {
                        found.Add(new DefenderKnownApp { Name = kv.Key, Path = path });
                        break;
                    }
                }
            }
            return found;
        }

        public static List<string> GetExclusions(out string error)
        {
            error = "";
            var r = PowerShellRunner.RunScript(
                "$p = Get-MpPreference; if ($p.ExclusionPath) { $p.ExclusionPath }", 2);
            if (r.ExitCode != 0)
            {
                error = string.IsNullOrWhiteSpace(r.Error) ? r.Output.Trim() : r.Error.Trim();
                if (string.IsNullOrEmpty(error)) error = "Get-MpPreference failed (exit " + r.ExitCode + ").";
                return new List<string>();
            }
            return r.Output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
        }

        public static bool TryAddExclusions(IEnumerable<string> paths, out string error)
        {
            error = "";
            var list = paths.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
            if (list.Count == 0) { error = "Nothing to exclude."; return false; }
            string ps = "Add-MpPreference -ExclusionPath " +
                string.Join(", ", list.Select(p => "'" + p.Replace("'", "''") + "'"));
            var r = PowerShellRunner.RunScript(ps, 2);
            if (r.ExitCode != 0)
            {
                error = string.IsNullOrWhiteSpace(r.Error) ? r.Output.Trim() : r.Error.Trim();
                if (string.IsNullOrEmpty(error)) error = "Add-MpPreference failed (exit " + r.ExitCode + ").";
                if (error.IndexOf("access", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    error.IndexOf("0x80070005", StringComparison.OrdinalIgnoreCase) >= 0)
                    error += " Try turning off Tamper Protection in Windows Security, then try again.";
                return false;
            }
            return true;
        }

        public static bool TryRemoveExclusion(string path, out string error)
        {
            error = "";
            string ps = "Remove-MpPreference -ExclusionPath '" + path.Replace("'", "''") + "'";
            var r = PowerShellRunner.RunScript(ps, 2);
            if (r.ExitCode != 0)
            {
                error = string.IsNullOrWhiteSpace(r.Error) ? r.Output.Trim() : r.Error.Trim();
                if (string.IsNullOrEmpty(error)) error = "Remove-MpPreference failed (exit " + r.ExitCode + ").";
                return false;
            }
            return true;
        }

        public static string Normalize(string path)
        {
            return path.Trim().TrimEnd('\\', '/').ToUpperInvariant();
        }
    }
}
