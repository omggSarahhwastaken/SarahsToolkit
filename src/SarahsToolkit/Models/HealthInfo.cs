namespace SarahsToolkit.Models
{
    public class BatteryInfo
    {
        public bool HasBattery { get; set; }
        public double HealthPercent { get; set; } = double.NaN;
        public long DesignCapacityMwh { get; set; }
        public long FullChargeCapacityMwh { get; set; }
        public long CycleCount { get; set; } = -1;
        public int ChargePercent { get; set; } = -1;
        public string PowerState { get; set; } = "";
    }

    public class DiskHealthInfo
    {
        public string Name { get; set; } = "";
        public string MediaType { get; set; } = "";
        public string HealthStatus { get; set; } = "";
        public long SizeBytes { get; set; }
        public int WearPercent { get; set; } = -1;   // SSD wear, -1 = unknown
        public int TemperatureC { get; set; } = -1;  // -1 = unknown
    }

    public class WindowsUpdateInfo
    {
        public bool Checked { get; set; }
        public int PendingCount { get; set; }
    }

    public class MaintenanceResult
    {
        public bool Success { get; set; }
        public string Summary { get; set; } = "";
    }
}
