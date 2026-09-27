using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;

namespace SarahsToolkit.Services
{
    public class UpdateInfo
    {
        public bool Available { get; set; }
        public string Version { get; set; }
        public string Url { get; set; }
        public string Message { get; set; }
    }

    public class UpdateService
    {
        private const string ReleasesUrl =
            "https://api.github.com/repos/omggSarahhwastaken/SarahsToolkit/releases/latest";

        public async Task<UpdateInfo> CheckForUpdatesAsync()
        {
            string current = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0";
            try
            {
                using (var client = new HttpClient())
                {
                    client.DefaultRequestHeaders.UserAgent.ParseAdd("SarahsToolkit");
                    var response = await client.GetAsync(ReleasesUrl);
                    if (response.StatusCode == HttpStatusCode.NotFound)
                        return new UpdateInfo
                        {
                            Available = false,
                            Message = "You're on v" + current + ". No releases published yet."
                        };
                    response.EnsureSuccessStatusCode();
                    string json = await response.Content.ReadAsStringAsync();
                    using (var doc = JsonDocument.Parse(json))
                    {
                        var root = doc.RootElement;
                        string tag = root.GetProperty("tag_name").GetString() ?? "";
                        string url = root.GetProperty("html_url").GetString() ?? "";
                        if (IsNewer(tag.TrimStart('v', 'V'), current))
                            return new UpdateInfo { Available = true, Version = tag, Url = url };
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

        private static string Normalize(string v)
        {
            var parts = new List<string>(v.Split('.'));
            while (parts.Count < 3) parts.Add("0");
            return string.Join(".", parts.Take(3));
        }
    }
}
