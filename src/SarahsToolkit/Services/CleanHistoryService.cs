using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace SarahsToolkit.Services
{
    /// <summary>
    /// Every clean (manual Quick/Full or scheduled) is recorded here so
    /// the Schedule page can show "Freed X this week" and "Freed Y all
    /// time". One JSON file under %LocalAppData%\SarahsToolkit, next to
    /// settings.json — never in the install dir, which the installer
    /// wipes on every update. History is nice-to-have, so a corrupt file
    /// just starts fresh instead of ever breaking a clean.
    /// </summary>
    public class CleanRecord
    {
        public DateTime Timestamp { get; set; }
        public long BytesFreed { get; set; }
        public string Kind { get; set; } = "";
    }

    public class CleanHistoryService
    {
        private readonly string _path;
        private List<CleanRecord> _records = new List<CleanRecord>();

        public CleanHistoryService()
            : this(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SarahsToolkit", "clean_history.json"))
        {
        }

        public CleanHistoryService(string path)
        {
            _path = path;
            Load();
        }

        public IReadOnlyList<CleanRecord> Records => _records;

        /// <summary>Freed in the last 7 days (rolling week).</summary>
        public long FreedThisWeek =>
            _records.Where(r => r.Timestamp >= DateTime.Now.AddDays(-7))
                    .Sum(r => r.BytesFreed);

        public long FreedAllTime => _records.Sum(r => r.BytesFreed);

        public CleanRecord LastClean => _records.Count == 0 ? null : _records[_records.Count - 1];

        public void Record(long bytesFreed, string kind)
        {
            if (bytesFreed < 0) bytesFreed = 0;
            _records.Add(new CleanRecord
            {
                Timestamp = DateTime.Now,
                BytesFreed = bytesFreed,
                Kind = kind ?? ""
            });
            // Cap the file: the last 200 cleans are plenty for the stats.
            if (_records.Count > 200)
                _records.RemoveRange(0, _records.Count - 200);
            Save();
        }

        private void Load()
        {
            try
            {
                if (!File.Exists(_path)) return;
                var loaded = JsonSerializer.Deserialize<List<CleanRecord>>(
                    File.ReadAllText(_path));
                if (loaded != null) _records = loaded;
            }
            catch
            {
                // Corrupt history starts fresh; never break a clean over it.
                _records = new List<CleanRecord>();
            }
        }

        private void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path));
                var tmp = _path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(_records,
                    new JsonSerializerOptions { WriteIndented = true }));
                if (File.Exists(_path)) File.Replace(tmp, _path, null);
                else File.Move(tmp, _path);
            }
            catch
            {
                // History must never break a clean.
            }
        }
    }
}
