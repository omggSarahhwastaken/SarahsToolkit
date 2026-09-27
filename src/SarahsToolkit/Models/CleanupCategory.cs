using System.Collections.Generic;

namespace SarahsToolkit.Models
{
    public class CleanupCategory
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public bool QuickClean { get; set; }
        public List<string> Paths { get; set; } = new List<string>();
        public string Special { get; set; } // "recyclebin" or null
        public List<string> StopServices { get; set; } = new List<string>();
        public List<string> StartServices { get; set; } = new List<string>();
    }
}
