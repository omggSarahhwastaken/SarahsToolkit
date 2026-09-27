namespace SarahsToolkit.Models
{
    public class DebloatApp
    {
        public string Id { get; set; }   // Appx package name pattern
        public string Name { get; set; } // Friendly display name
        public bool Installed { get; set; }
    }
}
