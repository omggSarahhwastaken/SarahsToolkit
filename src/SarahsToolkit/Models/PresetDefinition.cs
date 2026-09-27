using System.Collections.Generic;

namespace SarahsToolkit.Models
{
    public class PresetDefinition
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public List<string> Tweaks { get; set; } = new List<string>();
    }

    // Measured live on the PC: how much of a preset is not yet active.
    public class PresetImpact
    {
        public int Pending { get; set; }    // tweaks that would change something
        public int Measurable { get; set; } // tweaks whose state could be read
        public int Total { get; set; }      // tweak refs that resolved
    }
}
