using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using SarahsToolkit.Models;

namespace SarahsToolkit.Services
{
    public class DebloatService
    {
        public List<DebloatApp> LoadApps()
        {
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", "debloat.json");
            var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            return JsonSerializer.Deserialize<List<DebloatApp>>(File.ReadAllText(path), opts)
                   ?? new List<DebloatApp>();
        }

        public Task RefreshInstalledAsync(List<DebloatApp> apps)
        {
            return Task.Run(() =>
            {
                var sb = new StringBuilder();
                sb.AppendLine("$ids = @(" + string.Join(",", apps.Select(a => "'" + a.Id + "'")) + ")");
                sb.AppendLine("foreach ($id in $ids) {");
                sb.AppendLine("  $pat = $id + '*'");
                sb.AppendLine("  $n = @(Get-AppxPackage -Name $pat -AllUsers -ErrorAction SilentlyContinue).Count");
                sb.AppendLine("  Write-Output ($id + '=' + $n)");
                sb.AppendLine("}");
                var result = PowerShellRunner.RunScript(sb.ToString(), 5);
                var map = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                foreach (var line in result.Output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var parts = line.Split(new[] { '=' }, 2);
                    if (parts.Length == 2)
                        map[parts[0].Trim()] = parts[1].Trim() != "0";
                }
                foreach (var app in apps)
                    app.Installed = map.TryGetValue(app.Id, out bool v) && v;
            });
        }

        public Task<PowerShellResult> RemoveAsync(IEnumerable<DebloatApp> selected)
        {
            var ids = selected.Select(a => a.Id).ToList();
            return Task.Run(() =>
            {
                var sb = new StringBuilder();
                sb.AppendLine("$junk = @(" + string.Join(",", ids.Select(i => "'" + i + "'")) + ")");
                sb.AppendLine("$prov = @(Get-AppxProvisionedPackage -Online -ErrorAction SilentlyContinue)");
                sb.AppendLine("foreach ($j in $junk) {");
                sb.AppendLine("  $pat = $j + '*'");
                sb.AppendLine("  $pkgs = @(Get-AppxPackage -Name $pat -AllUsers -ErrorAction SilentlyContinue)");
                sb.AppendLine("  $provMatch = @($prov | Where-Object { $_.DisplayName -like $pat })");
                sb.AppendLine("  try { $pkgs | Remove-AppxPackage -AllUsers -ErrorAction SilentlyContinue } catch { }");
                sb.AppendLine("  try { $provMatch | Remove-AppxProvisionedPackage -Online -ErrorAction SilentlyContinue } catch { }");
                sb.AppendLine("  if (Get-AppxPackage -Name $pat -AllUsers -ErrorAction SilentlyContinue) { Write-Output ('FAIL=' + $j) } else { Write-Output ('OK=' + $j) }");
                sb.AppendLine("}");
                return PowerShellRunner.RunScript(sb.ToString(), 10);
            });
        }
    }
}
