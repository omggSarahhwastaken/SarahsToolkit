using System.Collections.Generic;

namespace SarahsToolkit.Models
{
    public class StartupEntry
    {
        public int Index { get; set; }
        public string Name { get; set; }
        public string Command { get; set; }
        public string Location { get; set; }
        // Every location this Name+Command was found in (registry keys,
        // startup folders, scheduled tasks). Disabling hits all of them,
        // so a duplicate entry can't resurrect the app.
        public List<string> Locations { get; set; } = new List<string>();
    }
}
