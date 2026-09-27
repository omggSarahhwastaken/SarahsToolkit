using System.Collections.Generic;

namespace SarahsToolkit.Models
{
    public class RegistryOperation
    {
        public string Hive { get; set; }      // "HKCU" or "HKLM"
        public string KeyPath { get; set; }
        public string ValueName { get; set; } // "(Default)" for the default value
        public string Kind { get; set; }      // "DWord" or "String"
        public string Data { get; set; }      // null => delete the value
    }

    public class RegistryCheck
    {
        public string Hive { get; set; }
        public string KeyPath { get; set; }
        public string ValueName { get; set; }
        public string Kind { get; set; }
        public string Expected { get; set; }
    }

    public class TweakDefinition
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public string Category { get; set; }
        public bool RequiresReboot { get; set; }
        public bool Recommended { get; set; } // shows a badge on Optimize; included in "Apply recommended"
        public List<RegistryOperation> Apply { get; set; } = new List<RegistryOperation>();
        public List<RegistryOperation> Revert { get; set; } = new List<RegistryOperation>();
        public RegistryCheck Check { get; set; }
    }

    public enum TweakState
    {
        Applied,
        NotApplied,
        Unknown
    }
}
