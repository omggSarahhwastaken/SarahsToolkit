using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using SarahsToolkit.Models;

namespace SarahsToolkit.Services
{
    /// <summary>
    /// System diagnostics and repair tools, mirroring Sarah's Optimizer's
    /// menu options. Results are shown in the app; nothing phones home.
    /// </summary>
    public class DiagnosticsService
    {
        public Task<PowerShellResult> SpecsCheckAsync()
        {
            string script =
                "$cpu = (Get-CimInstance Win32_Processor -ErrorAction SilentlyContinue | Select-Object -First 1).Name; " +
                "$gpus = Get-CimInstance Win32_VideoController -ErrorAction SilentlyContinue | Where-Object { $_.Name -notmatch 'Virtual|Basic Display|Remote' } | ForEach-Object { $_.Name }; " +
                "$cs = Get-CimInstance Win32_ComputerSystem -ErrorAction SilentlyContinue; " +
                "$ram = [math]::Round($cs.TotalPhysicalMemory / 1GB); " +
                "$os = Get-CimInstance Win32_OperatingSystem -ErrorAction SilentlyContinue; " +
                "$c = Get-PSDrive C -ErrorAction SilentlyContinue; " +
                "Write-Output ('CPU: ' + $cpu); " +
                "foreach ($g in $gpus) { Write-Output ('GPU: ' + $g) }; " +
                "Write-Output ('RAM: ' + $ram + ' GB'); " +
                "Write-Output ('OS: ' + $os.Caption + ' (build ' + $os.BuildNumber + ')'); " +
                "Write-Output ('Drive C: ' + [math]::Round($c.Free / 1GB, 1) + ' GB free of ' + [math]::Round(($c.Used + $c.Free) / 1GB, 1) + ' GB')";
            return Task.Run(() => PowerShellRunner.RunScript(script, 2));
        }

        public Task<PowerShellResult> SpaceAnalyzerAsync()
        {
            string script =
                "$targets = @(" +
                "@{ Name='User Temp'; Path=$env:TEMP }," +
                "@{ Name='Windows Temp'; Path='C:\\Windows\\Temp' }," +
                "@{ Name='Windows Update downloads'; Path='C:\\Windows\\SoftwareDistribution\\Download' }," +
                "@{ Name='Delivery Optimization'; Path='C:\\Windows\\SoftwareDistribution\\DeliveryOptimization' }," +
                "@{ Name='Prefetch'; Path='C:\\Windows\\Prefetch' }," +
                "@{ Name='CBS logs'; Path='C:\\Windows\\Logs\\CBS' }," +
                "@{ Name='Chrome cache'; Path=\"$env:LOCALAPPDATA\\Google\\Chrome\\User Data\\Default\\Cache\" }," +
                "@{ Name='Edge cache'; Path=\"$env:LOCALAPPDATA\\Microsoft\\Edge\\User Data\\Default\\Cache\" }," +
                "@{ Name='Discord cache'; Path=\"$env:APPDATA\\discord\\Cache\" }," +
                "@{ Name='Spotify cache'; Path=\"$env:APPDATA\\Spotify\\Storage\" }," +
                "@{ Name='NVIDIA DXCache'; Path=\"$env:LOCALAPPDATA\\NVIDIA\\DXCache\" }," +
                "@{ Name='Steam shadercache'; Path='C:\\Program Files (x86)\\Steam\\steamapps\\shadercache' }" +
                "); " +
                "$results = foreach ($t in $targets) { " +
                "$size = 0; " +
                "if (Test-Path -LiteralPath $t.Path) { " +
                "try { foreach ($f in [System.IO.Directory]::EnumerateFiles($t.Path, '*', 'AllDirectories')) { try { $size += (Get-Item -LiteralPath $f -Force -ErrorAction SilentlyContinue).Length } catch {} } } catch {} }; " +
                "[pscustomobject]@{ Name=$t.Name; MB=[math]::Round($size / 1MB, 1); Path=$t.Path } }; " +
                "$results | Sort-Object MB -Descending | Select-Object -First 10 | ForEach-Object { Write-Output ($_.Name + ': ' + $_.MB + ' MB  [' + $_.Path + ']') }";
            return Task.Run(() => PowerShellRunner.RunScript(script, 10));
        }

        public Task<PowerShellResult> DriveHealthAsync()
        {
            string script =
                "Get-PhysicalDisk -ErrorAction SilentlyContinue | ForEach-Object { " +
                "Write-Output ($_.FriendlyName + ' | ' + $_.MediaType + ' | Health: ' + $_.HealthStatus + ' | Status: ' + $_.OperationalStatus) }";
            return Task.Run(() => PowerShellRunner.RunScript(script, 2));
        }

        public Task<PowerShellResult> SfcScanAsync()
        {
            return Task.Run(() => PowerShellRunner.RunScript("sfc /scannow", 30));
        }

        public Task<PowerShellResult> ListStartupAsync()
        {
            string script =
                "$i = 0; " +
                "Get-CimInstance Win32_StartupCommand -ErrorAction SilentlyContinue | ForEach-Object { " +
                "Write-Output ($i.ToString() + '|' + $_.Name + '|' + $_.Command + '|' + $_.Location); " +
                "$i++ }";
            return Task.Run(() => PowerShellRunner.RunScript(script, 2));
        }

        public Task<PowerShellResult> DisableStartupAsync(List<StartupEntry> entries)
        {
            var sb = new StringBuilder();
            sb.Append("$reg = 'HKCU:\\SOFTWARE\\SarahsToolkit\\DisabledStartup'; ");
            sb.Append("New-Item -Path $reg -Force | Out-Null; ");
            foreach (var e in entries)
            {
                string name = (e.Name ?? "").Replace("'", "''");
                string cmd = (e.Command ?? "").Replace("'", "''");
                string loc = (e.Location ?? "").Replace("'", "''");
                sb.Append("$nm = '" + name + "'; ");
                sb.Append("$loc = '" + loc + "'; ");
                sb.Append("$rp = $loc -replace '^HKLM', 'HKLM:' -replace '^HKCU', 'HKCU:'; ");
                sb.Append("New-Item -Path \"$reg\\$nm\" -Force -ErrorAction SilentlyContinue | Out-Null; ");
                sb.Append("Set-ItemProperty -Path \"$reg\\$nm\" -Name 'Name' -Value $nm -ErrorAction SilentlyContinue; ");
                sb.Append("Set-ItemProperty -Path \"$reg\\$nm\" -Name 'Command' -Value '" + cmd + "' -ErrorAction SilentlyContinue; ");
                sb.Append("Set-ItemProperty -Path \"$reg\\$nm\" -Name 'Location' -Value $loc -ErrorAction SilentlyContinue; ");
                sb.Append("if ($loc -like 'HKLM*' -or $loc -like 'HKCU*') { Remove-ItemProperty -Path $rp -Name $nm -ErrorAction SilentlyContinue; Write-Output ('Disabled: ' + $nm) } ");
                sb.Append("else { Write-Output ('Skipped (startup-folder shortcut, remove manually): ' + $nm) }; ");
            }
            return Task.Run(() => PowerShellRunner.RunScript(sb.ToString(), 2));
        }

        public Task<PowerShellResult> ShaderSweepAsync()
        {
            string script =
                "$paths = @(" +
                "\"$env:LOCALAPPDATA\\NVIDIA\\DXCache\"," +
                "\"$env:LOCALAPPDATA\\NVIDIA\\GLCache\"," +
                "\"$env:APPDATA\\NVIDIA\\ComputeCache\"," +
                "\"$env:LOCALAPPDATA\\AMD\\DxCache\"," +
                "\"$env:LOCALAPPDATA\\AMD\\GLCache\"," +
                "\"$env:LOCALAPPDATA\\Intel\\ShaderCache\"," +
                "\"$env:LOCALAPPDATA\\D3DSCache\"," +
                "\"$env:PROGRAMDATA\\NVIDIA Corporation\\NV_Cache\"," +
                "'C:\\Program Files (x86)\\Steam\\steamapps\\shadercache'" +
                "); " +
                "$n = 0; " +
                "foreach ($p in $paths) { " +
                "if (Test-Path -LiteralPath $p) { " +
                "Get-ChildItem -LiteralPath $p -Force -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue; " +
                "$n++ } }; " +
                "Write-Output (\"Shader sweep done (\" + $n + \" locations cleared). Reboot recommended.\")";
            return Task.Run(() => PowerShellRunner.RunScript(script, 5));
        }

        public Task<PowerShellResult> UpdateFixerAsync()
        {
            string script =
                "Write-Output 'Resetting BITS transfer queue...'; " +
                "bitsadmin /reset /allusers | Out-Null; " +
                "$svcs = @('bits', 'wuauserv', 'cryptsvc', 'msiserver'); " +
                "foreach ($s in $svcs) { Stop-Service -Name $s -Force -ErrorAction SilentlyContinue }; " +
                "foreach ($d in @('C:\\Windows\\SoftwareDistribution', 'C:\\Windows\\System32\\catroot2')) { " +
                "if (Test-Path -LiteralPath $d) { Rename-Item -LiteralPath $d -NewName ((Split-Path $d -Leaf) + '.old') -ErrorAction SilentlyContinue } }; " +
                "foreach ($s in $svcs) { Start-Service -Name $s -ErrorAction SilentlyContinue }; " +
                "Write-Output 'Windows Update components reset. Open Windows Update and check for updates.'";
            return Task.Run(() => PowerShellRunner.RunScript(script, 10));
        }

        public Task<PowerShellResult> TroubleshootAsync()
        {
            string script =
                "Write-Output '=== Event log errors (last 2 hours, top 3) ==='; " +
                "try { " +
                "$errs = Get-WinEvent -FilterHashtable @{LogName='System','Application'; Level=1,2; StartTime=(Get-Date).AddHours(-2)} -ErrorAction SilentlyContinue | Group-Object ProviderName | Sort-Object Count -Descending | Select-Object -First 3; " +
                "if ($errs) { foreach ($e in $errs) { Write-Output ($e.Name + ': ' + $e.Count + ' errors') } } else { Write-Output 'No errors in the last 2 hours.' } " +
                "} catch { Write-Output 'Event log check skipped.' }; " +
                "Write-Output '=== Network ==='; " +
                "if (Test-Connection -ComputerName 1.1.1.1 -Count 2 -Quiet -ErrorAction SilentlyContinue) { Write-Output 'Internet: OK' } " +
                "else { Write-Output 'Internet: FAILED - flushing DNS and resetting Winsock...'; Clear-DnsClientCache; ipconfig /flushdns | Out-Null; netsh winsock reset | Out-Null; Write-Output 'Network stack reset. Reboot recommended.' }; " +
                "Write-Output '=== Drive health ==='; " +
                "Get-PhysicalDisk -ErrorAction SilentlyContinue | ForEach-Object { Write-Output ($_.FriendlyName + ': ' + $_.HealthStatus) }; " +
                "Write-Output '=== Crash history ==='; " +
                "$bsod = (Get-WinEvent -FilterHashtable @{LogName='System'; Id=1001} -ErrorAction SilentlyContinue | Measure-Object).Count; " +
                "$up = (Get-WinEvent -FilterHashtable @{LogName='System'; Id=41; ProviderName='Microsoft-Windows-Kernel-Power'} -ErrorAction SilentlyContinue | Measure-Object).Count; " +
                "Write-Output ('Blue screen events: ' + $bsod + ', unexpected shutdowns: ' + $up); " +
                "Write-Output 'Auto-troubleshoot complete.'";
            return Task.Run(() => PowerShellRunner.RunScript(script, 10));
        }

        public Task<PowerShellResult> PackageUpdaterAsync()
        {
            string script =
                "Write-Output 'Upgrading installed packages with winget (10 minute limit)...'; " +
                "$p = Start-Process -FilePath 'winget' -ArgumentList 'upgrade --all --accept-package-agreements --accept-source-agreements --disable-interactivity --silent' -PassThru -WindowStyle Hidden; " +
                "if (-not $p.WaitForExit(600000)) { try { $p.Kill() } catch {}; Write-Output 'winget hit the 10 minute limit and was stopped (run Package Updater again later).' } " +
                "else { Write-Output ('winget finished (exit code ' + $p.ExitCode + ').') }";
            return Task.Run(() => PowerShellRunner.RunScript(script, 12));
        }

        public Task<PowerShellResult> StoreUpdaterAsync()
        {
            string script =
                "Add-Type -AssemblyName System.Runtime.WindowsRuntime; " +
                "$asTask = ([System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object { $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1' })[0]; " +
                "function Await-Op($op, $t) { try { $task = $asTask.MakeGenericMethod($op.GetType().GetGenericArguments()).Invoke($null, @($op)); if ($task.Wait($t)) { return $task.Result } } catch {}; return $null }; " +
                "$ctx = [Windows.Services.Store.StoreContext]::GetDefault(); " +
                "$upd = Await-Op ($ctx.GetAppAndOptionalStorePackageUpdatesAsync()) 60000; " +
                "if ($upd -and $upd.Count -gt 0) { " +
                "Write-Output ('Found ' + $upd.Count + ' Microsoft Store update(s), installing (10 minute limit)...'); " +
                "$r = Await-Op ($ctx.RequestDownloadAndInstallStorePackageUpdatesAsync($upd)) 600000; " +
                "Write-Output 'Microsoft Store updates finished.' } " +
                "else { Write-Output 'No Microsoft Store updates available.' }";
            return Task.Run(() => PowerShellRunner.RunScript(script, 12));
        }

        public Task<PowerShellResult> SystemMaintenanceAsync()
        {
            string script =
                "Write-Output 'Clearing event logs...'; " +
                "wevtutil el | ForEach-Object { wevtutil cl \"$_\" 2>$null }; " +
                "Write-Output 'Running DISM component cleanup (this takes a while)...'; " +
                "Dism.exe /Online /Cleanup-Image /StartComponentCleanup | Out-Null; " +
                "Write-Output 'System maintenance done.'";
            return Task.Run(() => PowerShellRunner.RunScript(script, 20));
        }
    }
}
