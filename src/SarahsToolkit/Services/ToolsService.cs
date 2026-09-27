using System.Threading.Tasks;

namespace SarahsToolkit.Services
{
    public class ToolsService
    {
        public Task<PowerShellResult> SetDnsAsync(string[] ips)
        {
            string list = "'" + string.Join("','", ips) + "'";
            string script =
                "Get-NetAdapter | Where-Object { $_.Status -eq 'Up' } | ForEach-Object { " +
                "Set-DnsClientServerAddress -InterfaceIndex $_.ifIndex -ServerAddresses @(" + list + ") -ErrorAction SilentlyContinue }; " +
                "Clear-DnsClientCache; Write-Output 'DNS updated'";
            return Task.Run(() => PowerShellRunner.RunScript(script, 2));
        }

        public Task<PowerShellResult> AutoSelectDnsAsync()
        {
            string script =
                "$providers = @(" +
                "@{ Name='Cloudflare'; IPs=@('1.1.1.1','1.0.0.1') }," +
                "@{ Name='Google'; IPs=@('8.8.8.8','8.8.4.4') }," +
                "@{ Name='Quad9'; IPs=@('9.9.9.9','149.112.112.112') }" +
                "); " +
                "$best = $null; $bestMs = [double]::MaxValue; " +
                "foreach ($p in $providers) { " +
                "$r = Test-Connection -ComputerName $p.IPs[0] -Count 10 -ErrorAction SilentlyContinue; " +
                "if ($r) { $ms = ($r | Measure-Object -Property ResponseTime -Average).Average; " +
                "Write-Output ($p.Name + '=' + [math]::Round($ms) + 'ms'); " +
                "if ($ms -lt $bestMs) { $bestMs = $ms; $best = $p } } " +
                "else { Write-Output ($p.Name + '=unreachable') } }; " +
                "if ($best) { " +
                "Get-NetAdapter | Where-Object { $_.Status -eq 'Up' } | ForEach-Object { " +
                "Set-DnsClientServerAddress -InterfaceIndex $_.ifIndex -ServerAddresses $best.IPs -ErrorAction SilentlyContinue }; " +
                "Clear-DnsClientCache; " +
                "Write-Output ('DNS auto-selected: ' + $best.Name + ' (' + [math]::Round($bestMs) + ' ms)') " +
                "} else { Write-Output 'DNS auto-select failed: no provider reachable' }";
            return Task.Run(() => PowerShellRunner.RunScript(script, 10));
        }

        public Task<PowerShellResult> ReTrimAsync()
        {
            return Task.Run(() => PowerShellRunner.RunScript(
                "Optimize-Volume -DriveLetter C -ReTrim -ErrorAction SilentlyContinue; Write-Output 'ReTrim done'", 10));
        }

        public Task<PowerShellResult> NetworkRescueAsync()
        {
            string script =
                "Write-Output 'Flushing DNS cache...'; " +
                "Clear-DnsClientCache; ipconfig /flushdns | Out-Null; " +
                "Write-Output 'Resetting Winsock...'; " +
                "netsh winsock reset | Out-Null; " +
                "Write-Output 'Resetting TCP/IP stack...'; " +
                "netsh int ip reset | Out-Null; " +
                "Write-Output 'Renewing DHCP lease...'; " +
                "ipconfig /release | Out-Null; ipconfig /renew | Out-Null; " +
                "Write-Output 'Network rescue done. Reboot recommended.'";
            return Task.Run(() => PowerShellRunner.RunScript(script, 5));
        }
    }
}
