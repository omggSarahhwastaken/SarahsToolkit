using System.Threading.Tasks;

namespace SarahsToolkit.Services
{
    public class ToolsService
    {
        private static readonly string[] ServiceNames = new[]
        {
            "DiagTrack", "dmwappushservice", "MapsBroker", "RetailDemo", "Fax",
            "WMPNetworkSvc", "wisvc", "Spooler", "RemoteRegistry", "WerSvc",
            "seclogon", "CertPropSvc", "SCardSvr", "ScDeviceEnum", "SCPolicySvc",
            "TrkWks", "CscService", "MSiSCSI", "NetTcpPortSharing", "vds",
            "WebClient", "Wecsvc", "wcncsvc", "PNRPSvc", "p2psvc", "p2pimsvc",
            "shpamsvc", "AxInstSV", "AppMgmt", "SmsRouter", "PhoneSvc", "SEMgrSvc"
        };

        public Task<PowerShellResult> SetDnsAsync(string[] ips)
        {
            string list = "'" + string.Join("','", ips) + "'";
            string script =
                "Get-NetAdapter | Where-Object { $_.Status -eq 'Up' } | ForEach-Object { " +
                "Set-DnsClientServerAddress -InterfaceIndex $_.ifIndex -ServerAddresses @(" + list + ") -ErrorAction SilentlyContinue }; " +
                "Clear-DnsClientCache; Write-Output 'DNS updated'";
            return Task.Run(() => PowerShellRunner.RunScript(script, 2));
        }

        public Task<PowerShellResult> DisableServicesAsync()
        {
            string list = "'" + string.Join("','", ServiceNames) + "'";
            string script =
                "$svcs = @(" + list + "); $n = 0; " +
                "foreach ($s in $svcs) { $svc = Get-Service -Name $s -ErrorAction SilentlyContinue; " +
                "if ($svc) { try { Set-Service -Name $s -StartupType Disabled -ErrorAction Stop; " +
                "Stop-Service -Name $s -Force -ErrorAction SilentlyContinue; $n++ } catch { } } }; " +
                "Write-Output ('Disabled=' + $n)";
            return Task.Run(() => PowerShellRunner.RunScript(script, 5));
        }

        public Task<PowerShellResult> EnableServicesAsync()
        {
            string list = "'" + string.Join("','", ServiceNames) + "'";
            string script =
                "$svcs = @(" + list + "); $n = 0; " +
                "foreach ($s in $svcs) { if (Get-Service -Name $s -ErrorAction SilentlyContinue) { " +
                "try { Set-Service -Name $s -StartupType Manual -ErrorAction Stop; $n++ } catch { } } }; " +
                "Write-Output ('Restored=' + $n)";
            return Task.Run(() => PowerShellRunner.RunScript(script, 5));
        }

        public Task<PowerShellResult> ReTrimAsync()
        {
            return Task.Run(() => PowerShellRunner.RunScript(
                "Optimize-Volume -DriveLetter C -ReTrim -ErrorAction SilentlyContinue; Write-Output 'ReTrim done'", 10));
        }

        public Task<PowerShellResult> NetworkRescueAsync()
        {
            return Task.Run(() => PowerShellRunner.RunScript(
                "Clear-DnsClientCache; ipconfig /flushdns | Out-Null; Write-Output 'DNS cache flushed'", 2));
        }
    }
}
