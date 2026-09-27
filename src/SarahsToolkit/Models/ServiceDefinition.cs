namespace SarahsToolkit.Models
{
    public class ServiceDefinition
    {
        public string Name { get; set; }         // Windows service name
        public string DisplayName { get; set; } // Friendly name
        public string Description { get; set; } // Plain-English: what it does
        public string Impact { get; set; } = "";// Plain-English: what breaks if disabled
        public string Status { get; set; } = "Unknown";    // Running, Stopped, Missing...
        public string StartType { get; set; } = "Unknown"; // Automatic, Manual, Disabled...
    }
}
