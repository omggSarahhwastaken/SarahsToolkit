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

        // The Ookla CLI lives in %TEMP% now and is only downloaded when
        // it is missing or broken — re-downloading it on every test made
        // each run wait on the download and fail outright when the CDN
        // hiccupped. A --version check proves the cached copy still runs.
        public Task<PowerShellResult> EnsureSpeedTestCliAsync()
        {
            var sb = new StringBuilder();
            sb.Append("$workDir = Join-Path $env:TEMP 'sarahs_toolkit_speedtest'; ");
            sb.Append("$zip = Join-Path $workDir 'speedtest.zip'; ");
            sb.Append("$exe = Join-Path $workDir 'speedtest.exe'; ");
            sb.Append("try { ");
            sb.Append("if (Test-Path $exe) { ");
            sb.Append("& $exe --version 2>$null | Out-Null; ");
            sb.Append("if ($LASTEXITCODE -eq 0) { Write-Output 'OK|cached' } else { Remove-Item $exe -Force -ErrorAction SilentlyContinue } }; ");
            sb.Append("if (-not (Test-Path $exe)) { ");
            sb.Append("New-Item -ItemType Directory -Path $workDir -Force | Out-Null; ");
            sb.Append("Invoke-WebRequest -Uri 'https://install.speedtest.net/app/cli/ookla-speedtest-1.2.0-win64.zip' -OutFile $zip -UseBasicParsing -ErrorAction Stop; ");
            sb.Append("Expand-Archive -Path $zip -DestinationPath $workDir -Force -ErrorAction Stop; ");
            sb.Append("if (-not (Test-Path $exe)) { throw 'speedtest.exe missing after extraction' }; ");
            sb.Append("Remove-Item $zip -Force -ErrorAction SilentlyContinue; ");
            sb.Append("Write-Output 'OK|downloaded' } ");
            sb.Append("} catch { Write-Output ('ERROR|' + $_.Exception.Message) } ");
            return Task.Run(() => PowerShellRunner.RunScript(sb.ToString(), 2));
        }

        public Task<PowerShellResult> SpeedTestCliAsync()
        {
            var sb = new StringBuilder();
            sb.Append("$workDir = Join-Path $env:TEMP 'sarahs_toolkit_speedtest'; ");
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
            sb.Append("if (-not (Test-Path $exe)) { throw 'Speedtest CLI is missing — try running the test again' }; ");
            sb.Append("$out = & $exe --accept-license --accept-gdpr --format=json --progress=no 2>$null; ");
            // The CLI prints one JSON object per line, and can also print
            // log lines (type "log") next to the result — joining them all
            // into one string made ConvertFrom-Json fail on any extra line.
            // Parse each line on its own and keep the result object.
            sb.Append("$res = $null; $errMsg = $null; ");
            sb.Append("foreach ($line in @($out)) { ");
            sb.Append("try { $o = $line | ConvertFrom-Json -ErrorAction Stop; ");
            sb.Append("if ($o.type -eq 'result') { $res = $o } ");
            sb.Append("elseif ($o.type -eq 'log' -and $o.level -eq 'error' -and $o.message) { $errMsg = $o.message } } catch { } }; ");
            sb.Append("if (-not $res -or -not $res.download -or -not $res.upload) { if (-not $errMsg) { $errMsg = 'no usable result returned' }; throw ('Speedtest error: ' + $errMsg) }; ");
            sb.Append("$down = [Math]::Round($res.download.bandwidth * 8 / 1e6, 1); ");
            sb.Append("$up = [Math]::Round($res.upload.bandwidth * 8 / 1e6, 1); ");
            sb.Append("$ping = [Math]::Round($res.ping.latency, 1); ");
            sb.Append("Write-Output ('RESULT|' + $ping + '|' + $down + '|' + $up + '|' + $res.isp); ");
            sb.Append("if ($res.result.url) { Write-Output ('URL|' + $res.result.url) } ");
            sb.Append("} catch { Write-Output ('ERROR|' + $_.Exception.Message) } ");
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
