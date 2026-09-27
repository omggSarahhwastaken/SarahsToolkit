using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using SarahsToolkit.Models;

namespace SarahsToolkit.Services
{
    /// <summary>
    /// Upcoming game releases for the right-rail feed. Reads the bundled
    /// Data/game_releases.json, drops anything already released, soonest first.
    /// </summary>
    public class GameFeedService
    {
        private readonly string _jsonPath;

        public GameFeedService()
        {
            _jsonPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", "game_releases.json");
        }

        public List<GameRelease> LoadUpcoming()
        {
            try
            {
                var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var all = JsonSerializer.Deserialize<List<GameRelease>>(
                    File.ReadAllText(_jsonPath), opts) ?? new List<GameRelease>();
                DateTime today = DateTime.Today;
                return all.Where(g => !string.IsNullOrWhiteSpace(g.Title) && g.Date.Date >= today)
                          .OrderBy(g => g.Date)
                          .ToList();
            }
            catch
            {
                return new List<GameRelease>();
            }
        }

        public static string RelativeLabel(DateTime date)
        {
            int days = (date.Date - DateTime.Today).Days;
            if (days <= 0) return "Today";
            if (days == 1) return "Tomorrow";
            if (days < 14) return "In " + days + " days";
            if (days < 60) return "In " + days / 7 + " weeks";
            int months = days / 30;
            return "In " + months + (months == 1 ? " month" : " months");
        }
    }
}
