namespace SarahsToolkit.Models
{
    public class ServiceDefinition
    {
        public string Name { get; set; }         // Windows service name
        public string DisplayName { get; set; } // Friendly name
        public string Description { get; set; }
        public string Status { get; set; } = "Unknown";    // Running, Stopped, Missing...
        public string StartType { get; set; } = "Unknown"; // Automatic, Manual, Disabled...
    }
}
