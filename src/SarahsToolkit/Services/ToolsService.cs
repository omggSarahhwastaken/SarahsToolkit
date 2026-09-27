using System.Text;
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

        public Task<PowerShellResult> SpeedTestCliAsync()
        {
            var sb = new StringBuilder();
            sb.Append("$workDir = Join-Path $env:TEMP 'sarahs_toolkit_speedtest'; ");
            sb.Append("$zip = Join-Path $workDir 'speedtest.zip'; ");
            sb.Append("$exe = Join-Path $workDir 'speedtest.exe'; ");
            sb.Append("try { ");
            sb.Append("$wfMsg = 'Wired connection (no active WiFi)'; ");
            sb.Append("try { ");
            sb.Append("$wfLine = (netsh wlan show interfaces 2>$null) -match '^\\s*Signal' | Select-Object -First 1; ");
            sb.Append("if ($wfLine -match '(\\d+)\\s*%') { $p = [int]$Matches[1]; ");
            sb.Append("$g = 'Weak'; if ($p -ge 80) { $g = 'Excellent' } elseif ($p -ge 60) { $g = 'Good' } elseif ($p -ge 40) { $g = 'Fair' }; ");
            sb.Append("$wfMsg = 'WiFi signal: ' + $p + '% (' + $g + ')'; } ");
            sb.Append("} catch { }; ");
            sb.Append("Write-Output ('WIFI|' + $wfMsg); ");
            sb.Append("if (Test-Path $workDir) { Remove-Item $workDir -Recurse -Force -ErrorAction SilentlyContinue }; ");
            sb.Append("New-Item -ItemType Directory -Path $workDir -Force | Out-Null; ");
            sb.Append("Write-Output 'PROGRESS|Downloading official Speedtest CLI (Ookla)...'; ");
            sb.Append("Invoke-WebRequest -Uri 'https://install.speedtest.net/app/cli/ookla-speedtest-1.2.0-win64.zip' -OutFile $zip -UseBasicParsing -ErrorAction Stop; ");
            sb.Append("Expand-Archive -Path $zip -DestinationPath $workDir -Force -ErrorAction Stop; ");
            sb.Append("if (-not (Test-Path $exe)) { throw 'speedtest.exe missing after extraction' }; ");
            sb.Append("Write-Output 'PROGRESS|Finding best server and running test (about 30 seconds)...'; ");
            sb.Append("$out = & $exe --accept-license --accept-gdpr --format=json --progress=no 2>$null; ");
            sb.Append("$res = ($out -join \"`n\") | ConvertFrom-Json -ErrorAction Stop; ");
            sb.Append("$down = [Math]::Round($res.download.bandwidth * 8 / 1e6, 1); ");
            sb.Append("$up = [Math]::Round($res.upload.bandwidth * 8 / 1e6, 1); ");
            sb.Append("$ping = [Math]::Round($res.ping.latency, 1); ");
            sb.Append("Write-Output ('RESULT|' + $ping + '|' + $down + '|' + $up + '|' + $res.server.name + ' (' + $res.server.location + ')|' + $res.isp); ");
            sb.Append("if ($res.result.url) { Write-Output ('URL|' + $res.result.url) } ");
            sb.Append("} catch { Write-Output ('ERROR|' + $_.Exception.Message) } ");
            sb.Append("finally { if (Test-Path $workDir) { Remove-Item $workDir -Recurse -Force -ErrorAction SilentlyContinue } }; ");
            return Task.Run(() => PowerShellRunner.RunScript(sb.ToString(), 5));
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
