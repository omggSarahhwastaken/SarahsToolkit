using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
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
                "$vc = Get-CimInstance Win32_VideoController -ErrorAction SilentlyContinue | Where-Object { $_.Name -notmatch 'Virtual|Basic Display|Remote' }; " +
                "$real = @($vc | Where-Object { $_.PNPDeviceID -notmatch '^USB' }); " +
                "if ($real.Count -eq 0) { $real = @($vc) }; " +
                "$gpus = $real | ForEach-Object { $_.Name }; " +
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
            // 0 = success, -1978335189 (0x8A15002B) = nothing applicable,
            // -1978335188 (0x8A15002C) = upgrade --all completed with failures.
            string script =
                "Write-Output 'Upgrading installed packages with winget (10 minute limit)...'; " +
                "$p = Start-Process -FilePath 'winget' -ArgumentList 'upgrade --all --accept-package-agreements --accept-source-agreements --disable-interactivity --silent' -PassThru -WindowStyle Hidden; " +
                "if (-not $p.WaitForExit(600000)) { try { $p.Kill() } catch {}; Write-Output 'winget hit the 10 minute limit and was stopped (run Package Updater again later).' } " +
                "elseif ($p.ExitCode -eq 0) { Write-Output 'winget finished successfully.' } " +
                "elseif ($p.ExitCode -eq -1978335189) { Write-Output 'winget finished: no applicable updates found.' } " +
                "else { Write-Output ('winget finished with failures (exit code ' + $p.ExitCode + '). Run ''winget upgrade --all'' in a terminal for details.') }";
            return Task.Run(() => PowerShellRunner.RunScript(script, 12));
        }

        public Task<PowerShellResult> StoreUpdaterAsync()
        {
            // The WinRT StoreContext type is resolved at runtime via GetType:
            // a [Type] literal would die at parse time with "Unable to find
            // type" on PCs where the Store API is unavailable. winget already
            // covers Microsoft Store apps, so this is a best-effort second pass.
            string script =
                "Add-Type -AssemblyName System.Runtime.WindowsRuntime; " +
                "$storeType = [System.Type]::GetType('Windows.Services.Store.StoreContext, Windows.Services.Store, ContentType=WindowsRuntime'); " +
                "if (-not $storeType) { Write-Output 'Microsoft Store updates skipped: the Store API is not available on this PC (winget already covers Store apps).' } " +
                "else { " +
                "$asTask = ([System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object { $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1' })[0]; " +
                "function Await-Op($op, $t) { try { $task = $asTask.MakeGenericMethod($op.GetType().GetGenericArguments()).Invoke($null, @($op)); if ($task.Wait($t)) { return $task.Result } } catch {}; return $null }; " +
                "try { " +
                "$ctx = $storeType.GetMethod('GetDefault').Invoke($null, $null); " +
                "$upd = Await-Op ($ctx.GetAppAndOptionalStorePackageUpdatesAsync()) 60000; " +
                "if ($upd -and $upd.Count -gt 0) { " +
                "Write-Output ('Found ' + $upd.Count + ' Microsoft Store update(s), installing (10 minute limit)...'); " +
                "$r = Await-Op ($ctx.RequestDownloadAndInstallStorePackageUpdatesAsync($upd)) 600000; " +
                "Write-Output 'Microsoft Store updates finished.' } " +
                "else { Write-Output 'No Microsoft Store updates available.' } } " +
                "catch { Write-Output 'Microsoft Store updates skipped: the Store API could not be used on this PC (winget already covers Store apps).' } }";
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

        // ---------- Extra cleanup (moved out of Cleanup: not temp/cache) ----------

        public Task<PowerShellResult> EmptyRecycleBinAsync()
        {
            string script =
                "Clear-RecycleBin -Force -ErrorAction SilentlyContinue; " +
                "Write-Output 'Recycle Bin emptied.'";
            return Task.Run(() => PowerShellRunner.RunScript(script, 2));
        }

        public Task<PowerShellResult> ClearCrashDumpsAsync()
        {
            var sb = new StringBuilder();
            sb.Append("$total = 0; ");
            sb.Append("$targets = @('C:\\Windows\\Minidump', \"$env:PROGRAMDATA\\Microsoft\\Windows\\WER\", \"$env:LOCALAPPDATA\\CrashDumps\", 'C:\\Windows\\MEMORY.DMP'); ");
            sb.Append("foreach ($t in $targets) { ");
            sb.Append("$it = Get-Item $t -Force -ErrorAction SilentlyContinue; ");
            sb.Append("if (-not $it) { continue }; ");
            sb.Append("$size = (Get-ChildItem $t -Recurse -Force -File -ErrorAction SilentlyContinue | Measure-Object -Property Length -Sum).Sum; ");
            sb.Append("if ($size) { $total += $size }; ");
            sb.Append("if ($it.PSIsContainer) { Remove-Item \"$t\\*\" -Recurse -Force -ErrorAction SilentlyContinue } ");
            sb.Append("else { Remove-Item $t -Force -ErrorAction SilentlyContinue } }; ");
            sb.Append("Write-Output ('Crash dumps cleared. Freed: ' + [math]::Round($total / 1MB, 1) + ' MB'); ");
            return Task.Run(() => PowerShellRunner.RunScript(sb.ToString(), 5));
        }

        public Task<PowerShellResult> ClearGameLogsAsync()
        {
            string[] patterns = new string[]
            {
                "$env:LOCALAPPDATA\\Bloxstrap\\Logs",
                "C:\\Program Files (x86)\\Steam\\logs",
                "$env:USERPROFILE\\Saved Games\\DCS\\Logs",
                "$env:LOCALAPPDATA\\FiveM\\FiveM.app\\logs",
                "$env:LOCALAPPDATA\\FiveM\\FiveM.app\\crashes",
                "$env:LOCALAPPDATA\\FiveM\\FiveM.app\\crashometry",
                "$env:LOCALAPPDATA\\Rockstar Games\\GTA V",
                "$env:LOCALAPPDATA\\Rockstar Games\\Launcher\\CrashLogs",
                "$env:USERPROFILE\\Documents\\Rockstar Games\\Social Club",
                "$env:LOCALAPPDATA\\Roblox\\logs",
                "$env:LOCALAPPDATA\\Roblox\\Versions\\version-*\\logs",
                "$env:LOCALAPPDATA\\EpicGamesLauncher\\Saved\\logs",
                "$env:APPDATA\\.minecraft\\logs",
                "$env:LOCALAPPDATA\\Electronic Arts\\EA Desktop\\Logs",
                "$env:PROGRAMDATA\\EA Desktop\\Logs",
                "$env:APPDATA\\Signal\\logs"
            };
            var sb = new StringBuilder();
            sb.Append("$total = 0; $n = 0; ");
            sb.Append("$patterns = @(");
            for (int i = 0; i < patterns.Length; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append("\"" + patterns[i] + "\"");
            }
            sb.Append("); ");
            sb.Append("foreach ($pat in $patterns) { ");
            sb.Append("foreach ($r in (Resolve-Path $pat -ErrorAction SilentlyContinue)) { ");
            sb.Append("$p = $r.Path; ");
            sb.Append("$size = (Get-ChildItem $p -Recurse -Force -File -ErrorAction SilentlyContinue | Measure-Object -Property Length -Sum).Sum; ");
            sb.Append("if ($size) { $total += $size }; ");
            sb.Append("$n++; ");
            sb.Append("Remove-Item \"$p\\*\" -Recurse -Force -ErrorAction SilentlyContinue; } }; ");
            sb.Append("Write-Output ('Game/app logs cleared (' + $n + ' folders). Freed: ' + [math]::Round($total / 1MB, 1) + ' MB'); ");
            return Task.Run(() => PowerShellRunner.RunScript(sb.ToString(), 5));
        }

        public Task<PowerShellResult> RemoveWindowsOldAsync()
        {
            var sb = new StringBuilder();
            sb.Append("if (-not (Test-Path 'C:\\Windows.old')) { Write-Output 'No Windows.old folder found. Nothing to remove.' } ");
            sb.Append("else { ");
            sb.Append("$size = (Get-ChildItem 'C:\\Windows.old' -Recurse -Force -File -ErrorAction SilentlyContinue | Measure-Object -Property Length -Sum).Sum; ");
            sb.Append("takeown /F 'C:\\Windows.old' /A /R /D Y >$null 2>&1; ");
            sb.Append("icacls 'C:\\Windows.old' /grant '*S-1-5-32-544:F' /T /C /Q >$null 2>&1; ");
            sb.Append("Remove-Item 'C:\\Windows.old' -Recurse -Force -ErrorAction SilentlyContinue; ");
            sb.Append("if (Test-Path 'C:\\Windows.old') { Write-Output 'Windows.old could not be fully removed.' } ");
            sb.Append("else { Write-Output ('Windows.old removed. Freed: ' + [math]::Round($size / 1GB, 2) + ' GB') } }; ");
            return Task.Run(() => PowerShellRunner.RunScript(sb.ToString(), 15));
        }

        public Task<PowerShellResult> RemoveDriverLeftoversAsync()
        {
            var sb = new StringBuilder();
            sb.Append("$total = 0; $n = 0; ");
            sb.Append("foreach ($d in @('C:\\AMD', 'C:\\NVIDIA', 'C:\\Intel')) { ");
            sb.Append("if (Test-Path $d) { ");
            sb.Append("$size = (Get-ChildItem $d -Recurse -Force -File -ErrorAction SilentlyContinue | Measure-Object -Property Length -Sum).Sum; ");
            sb.Append("if ($size) { $total += $size }; ");
            sb.Append("$n++; ");
            sb.Append("Remove-Item $d -Recurse -Force -ErrorAction SilentlyContinue; } }; ");
            sb.Append("if ($n -eq 0) { Write-Output 'No driver installer leftover folders found.' } ");
            sb.Append("else { Write-Output ('Removed ' + $n + ' folder(s). Freed: ' + [math]::Round($total / 1GB, 2) + ' GB') }; ");
            return Task.Run(() => PowerShellRunner.RunScript(sb.ToString(), 10));
        }

        public Task<PowerShellResult> DeleteRestorePointsAsync()
        {
            string script =
                "vssadmin delete shadows /all /quiet 2>&1 | Out-Null; " +
                "Write-Output 'System restore points deleted.'";
            return Task.Run(() => PowerShellRunner.RunScript(script, 5));
        }

        public Task<PowerShellResult> ResetStoreCacheAsync()
        {
            var sb = new StringBuilder();
            sb.Append("$wasOpen = @(Get-Process 'WinStore.App' -ErrorAction SilentlyContinue).Count -gt 0; ");
            sb.Append("Start-Process wsreset.exe -Wait; ");
            sb.Append("Start-Sleep -Seconds 2; ");
            sb.Append("if (-not $wasOpen) { Stop-Process -Name 'WinStore.App' -Force -ErrorAction SilentlyContinue }; ");
            sb.Append("Write-Output 'Microsoft Store cache reset done.'; ");
            return Task.Run(() => PowerShellRunner.RunScript(sb.ToString(), 5));
        }

        public Task<PowerShellResult> CrashHistoryAsync()
        {
            var sb = new StringBuilder();
            sb.Append("$evts = Get-WinEvent -FilterHashtable @{ LogName='Application','System'; Level=2 } -MaxEvents 25 -ErrorAction SilentlyContinue | ForEach-Object { ");
            sb.Append("[pscustomobject]@{ Time=$_.TimeCreated; Log=$_.LogName; Source=$_.ProviderName; Id=$_.Id; ");
            sb.Append("Message=$(if ($_.Message) { $_.Message.Substring(0, [Math]::Min(110, $_.Message.Length)) } else { '' }) } }; ");
            sb.Append("if ($evts) { $evts | Format-Table -AutoSize | Out-String -Width 220 | Write-Output } ");
            sb.Append("else { Write-Output 'No recent error events found.' }; ");
            return Task.Run(() => PowerShellRunner.RunScript(sb.ToString(), 3));
        }

        /// <summary>
        /// One-shot system snapshot for the dashboard: CPU, GPU, RAM, disk C:,
        /// uptime, Windows build and pending-reboot state. Single script, parsed
        /// into a DashboardSnapshot.
        /// </summary>
        public Task<DashboardSnapshot> GetDashboardSnapshotAsync()
        {
            return Task.Run(() =>
            {
                var snap = new DashboardSnapshot();
                var sb = new StringBuilder();
                sb.Append("$cpu = (Get-CimInstance Win32_Processor -ErrorAction SilentlyContinue | Select-Object -First 1).Name; ");
                // USB-attached display adapters (PNPDeviceID starts with USB\) are excluded; discrete
                // NVIDIA/AMD GPUs are preferred so the real GPU wins over integrated graphics.
                sb.Append("$vc = Get-CimInstance Win32_VideoController -ErrorAction SilentlyContinue | Where-Object { $_.Name -notmatch 'Virtual|Basic Display|Remote' }; ");
                sb.Append("$real = @($vc | Where-Object { $_.PNPDeviceID -notmatch '^USB' }); ");
                sb.Append("if ($real.Count -eq 0) { $real = @($vc) }; ");
                sb.Append("$pick = @($real | Where-Object { $_.Name -match 'NVIDIA|GeForce|AMD|Radeon' }); ");
                sb.Append("$gpu = $(if ($pick.Count -gt 0) { $pick[0].Name } elseif ($real.Count -gt 0) { $real[0].Name } else { '' }); ");
                sb.Append("$os = Get-CimInstance Win32_OperatingSystem -ErrorAction SilentlyContinue; ");
                sb.Append("$c = Get-PSDrive C -ErrorAction SilentlyContinue; ");
                sb.Append("$up = (Get-Date) - $os.LastBootUpTime; ");
                sb.Append("Write-Output ('CPU=' + $cpu); ");
                sb.Append("Write-Output ('GPU=' + $gpu); ");
                sb.Append("Write-Output ('RAMTOTAL=' + [math]::Round($os.TotalVisibleMemorySize / 1MB, 1)); ");
                sb.Append("Write-Output ('RAMFREE=' + [math]::Round($os.FreePhysicalMemory / 1MB, 1)); ");
                sb.Append("Write-Output ('DISKTOTAL=' + [math]::Round(($c.Used + $c.Free) / 1GB, 1)); ");
                sb.Append("Write-Output ('DISKFREE=' + [math]::Round($c.Free / 1GB, 1)); ");
                sb.Append("Write-Output ('UPTIME=' + $up.Days + 'd ' + $up.Hours + 'h ' + $up.Minutes + 'm'); ");
                sb.Append("Write-Output ('BUILD=' + $os.BuildNumber); ");
                // Only Windows servicing/update keys count as "restart pending". PendingFileRenameOperations
                // is set by ordinary third-party installers and survives Fast Startup shutdowns, so it
                // fires when no Windows update actually needs a reboot.
                sb.Append("$rb = (Test-Path 'HKLM:\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Component Based Servicing\\RebootPending') -or ");
                sb.Append("(Test-Path 'HKLM:\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\WindowsUpdate\\Auto Update\\RebootRequired'); ");
                sb.Append("Write-Output ('REBOOT=' + $(if ($rb) { 'YES' } else { 'NO' })); ");
                PowerShellResult r = PowerShellRunner.RunScript(sb.ToString(), 2);
                foreach (var line in (r.Output ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string t = line.Trim();
                    int eq = t.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = t.Substring(0, eq), val = t.Substring(eq + 1);
                    switch (key)
                    {
                        case "CPU": if (!string.IsNullOrWhiteSpace(val)) snap.Cpu = val; break;
                        case "GPU": if (!string.IsNullOrWhiteSpace(val)) snap.Gpu = val; break;
                        case "RAMTOTAL": double.TryParse(val, out double rt); snap.RamTotalGb = rt; break;
                        case "RAMFREE": double.TryParse(val, out double rf); snap.RamFreeGb = rf; break;
                        case "DISKTOTAL": double.TryParse(val, out double dt); snap.DiskTotalGb = dt; break;
                        case "DISKFREE": double.TryParse(val, out double df); snap.DiskFreeGb = df; break;
                        case "UPTIME": snap.Uptime = val; break;
                        case "BUILD": snap.WindowsBuild = val; break;
                        case "REBOOT": snap.PendingReboot = val == "YES"; break;
                    }
                }
                return snap;
            });
        }

        /// <summary>
        /// Pings Fortnite's official per-region endpoints (the same ones Epic
        /// documents for manual ping testing), Hypixel for Minecraft, and two
        /// DNS baselines with .NET Ping (4 probes per host, 1.2s timeout).
        /// Roblox publishes no ping endpoint and blocks ICMP, so it is
        /// measured with TCP connect timing to www.roblox.com:443 instead.
        /// </summary>
        public async Task<List<PingTargetResult>> PingGameServersAsync()
        {
            var targets = new (string name, string host, int tcpPort)[]
            {
                ("Fortnite NA-East", "ping-nae.ds.on.epicgames.com", 0),
                ("Fortnite NA-Central", "ping-nac.ds.on.epicgames.com", 0),
                ("Fortnite NA-West", "ping-naw.ds.on.epicgames.com", 0),
                ("Fortnite Europe", "ping-eu.ds.on.epicgames.com", 0),
                ("Fortnite Oceania", "ping-oce.ds.on.epicgames.com", 0),
                ("Fortnite Brazil", "ping-br.ds.on.epicgames.com", 0),
                ("Fortnite Asia", "ping-asia.ds.on.epicgames.com", 0),
                ("Fortnite Middle East", "ping-me.ds.on.epicgames.com", 0),
                ("Minecraft Hypixel", "mc.hypixel.net", 0),
                ("Roblox (TCP)", "www.roblox.com", 443),
                ("Baseline Cloudflare", "1.1.1.1", 0),
                ("Baseline Google", "8.8.8.8", 0),
            };
            return await Task.Run(() =>
            {
                var results = new List<PingTargetResult>();
                foreach (var t in targets)
                {
                    var times = new List<long>();
                    int lost = 0;
                    if (t.tcpPort > 0)
                    {
                        for (int i = 0; i < 4; i++)
                        {
                            try
                            {
                                using (var c = new TcpClient())
                                {
                                    var sw = Stopwatch.StartNew();
                                    bool ok = c.ConnectAsync(t.host, t.tcpPort).Wait(2000);
                                    sw.Stop();
                                    if (ok && c.Connected) times.Add(sw.ElapsedMilliseconds);
                                    else lost++;
                                }
                            }
                            catch { lost++; }
                        }
                    }
                    else
                    {
                        using (var p = new Ping())
                        {
                            for (int i = 0; i < 4; i++)
                            {
                                try
                                {
                                    PingReply r = p.Send(t.host, 1200);
                                    if (r.Status == IPStatus.Success) times.Add(r.RoundtripTime);
                                    else lost++;
                                }
                                catch { lost++; }
                            }
                        }
                    }
                    results.Add(new PingTargetResult
                    {
                        Name = t.name,
                        Host = t.tcpPort > 0 ? t.host + ":" + t.tcpPort : t.host,
                        AvgMs = times.Count > 0 ? (long)Math.Round(times.Average()) : -1,
                        LossPct = lost * 25
                    });
                }
                return results;
            });
        }
    }

    public class PingTargetResult
    {
        public string Name = "";
        public string Host = "";
        public long AvgMs = -1; // -1 means no reply
        public int LossPct;
    }
}
