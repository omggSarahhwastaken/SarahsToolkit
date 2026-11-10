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
        // Optional PowerShell for tweaks that aren't a plain registry value:
        // power-plan settings (powercfg), per-adapter settings (Nagle, adapter
        // power saving), hibernation and 8.3 filenames (fsutil). Apply/Revert
        // scripts run after the registry ops (if any). A Check script takes
        // precedence over Check: exit code 0 means Applied, anything else
        // means NotApplied. A script that fails (non-zero exit) throws.
        public string ApplyScript { get; set; }
        public string RevertScript { get; set; }
        public string CheckScript { get; set; }
    }

    public enum TweakState
    {
        Applied,
        NotApplied,
        Unknown
    }
}
