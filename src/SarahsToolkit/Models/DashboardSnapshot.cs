namespace SarahsToolkit.Models
{
    public class DashboardSnapshot
    {
        public string Cpu { get; set; } = "?";
        public string Gpu { get; set; } = "?";
        public double RamTotalGb { get; set; }
        public double RamFreeGb { get; set; }
        public double DiskTotalGb { get; set; }
        public double DiskFreeGb { get; set; }
        public string Uptime { get; set; } = "?";
        public string WindowsBuild { get; set; } = "?";
        public bool PendingReboot { get; set; }
    }
}
