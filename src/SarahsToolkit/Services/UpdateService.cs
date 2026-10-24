using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;

namespace SarahsToolkit.Services
{
    public class UpdateInfo
    {
        public bool Available { get; set; }
        public string Version { get; set; } = "";
        public string LocalVersion { get; set; } = "";
        public string Url { get; set; } = "";
        public string Encoding { get; set; } = "";
        public string Sha256 { get; set; } = "";
        public string Notes { get; set; } = "";
        public string Message { get; set; } = "";
    }

    public class UpdateService
    {
        // Secret gist: toolkit-version.json (version manifest + installer download).
        // Raw gist URLs need no authentication, so this works for everyone.
        private const string ManifestUrl =
            "https://gist.githubusercontent.com/omggSarahhwastaken/13c4313d895248fef6246a1d3bc1c40f/raw/toolkit-version.json";

        public async Task<UpdateInfo> CheckForUpdatesAsync()
        {
            string current = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";
            try
            {
                using (var client = new HttpClient())
                {
                    client.DefaultRequestHeaders.UserAgent.ParseAdd("SarahsToolkit");
                    client.Timeout = TimeSpan.FromSeconds(20);
                    // Cache-buster: gist raw URLs sit behind a CDN for a few minutes.
                    string url = ManifestUrl + "?t=" + DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    string json = await client.GetStringAsync(url);
                    using (var doc = JsonDocument.Parse(json))
                    {
                        var root = doc.RootElement;
                        string latest = root.GetProperty("version").GetString() ?? "";
                        string dl = root.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
                        string enc = root.TryGetProperty("encoding", out var e) ? e.GetString() ?? "" : "";
                        string sha = root.TryGetProperty("sha256", out var h) ? h.GetString() ?? "" : "";
                        string notes = root.TryGetProperty("notes", out var n) ? n.GetString() ?? "" : "";
                        if (string.IsNullOrWhiteSpace(latest) || string.IsNullOrWhiteSpace(dl))
                            return new UpdateInfo
                            {
                                Available = false,
                                Message = "You're on v" + current + ". No releases published yet."
                            };
                        if (!TryParseVersion(latest))
                            return new UpdateInfo
                            {
                                Available = false,
                                Message = "Update check failed: the published version '" + latest +
                                    "' could not be read. You're still on v" + current + "."
                            };
                        if (IsNewer(latest, current))
                            return new UpdateInfo
                            {
                                Available = true,
                                Version = latest,
                                LocalVersion = current,
                                Url = dl,
                                Encoding = enc,
                                Sha256 = sha,
                                Notes = notes
                            };
                        return new UpdateInfo
                        {
                            Available = false,
                            Message = "You're up to date (v" + current + ")."
                        };
                    }
                }
            }
            catch (Exception ex)
            {
                return new UpdateInfo { Available = false, Message = "Update check failed: " + ex.Message };
            }
        }

        private static bool IsNewer(string latest, string current)
        {
            try
            {
                return new Version(Normalize(latest)) > new Version(Normalize(current));
            }
            catch
            {
                return false;
            }
        }

        // True when the manifest version parses as a .NET Version after
        // normalization. A malformed version (e.g. "2.6.0-beta") used to be
        // swallowed by IsNewer and silently never offer the update; callers
        // now surface this as a visible "update check failed" message.
        private static bool TryParseVersion(string v)
        {
            try { var _ = new Version(Normalize(v)); return true; }
            catch { return false; }
        }

        private static string Normalize(string v)
        {
            var parts = new List<string>(v.Trim().TrimStart('v', 'V').Split('.'));
            while (parts.Count < 3) parts.Add("0");
            return string.Join(".", parts.Take(3));
        }
    }
}
