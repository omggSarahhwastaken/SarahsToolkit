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
    public class ServiceOptimizerService
    {
        public List<ServiceDefinition> LoadDefinitions()
        {
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", "services.json");
            var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            return JsonSerializer.Deserialize<List<ServiceDefinition>>(File.ReadAllText(path), opts)
                   ?? new List<ServiceDefinition>();
        }

        public Task RefreshStatesAsync(List<ServiceDefinition> defs)
        {
            return Task.Run(() =>
            {
                var sb = new StringBuilder();
                sb.AppendLine("$svcs = @(" + string.Join(",", defs.Select(d => "'" + d.Name + "'")) + ")");
                sb.AppendLine("foreach ($s in $svcs) {");
                sb.AppendLine("  $svc = Get-Service -Name $s -ErrorAction SilentlyContinue");
                sb.AppendLine("  if ($svc) { Write-Output ($s + '|' + $svc.Status + '|' + $svc.StartType) }");
                sb.AppendLine("  else { Write-Output ($s + '|Missing|Missing') }");
                sb.AppendLine("}");
                var result = PowerShellRunner.RunScript(sb.ToString(), 5);
                var map = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
                foreach (var line in result.Output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var parts = line.Split(new[] { '|' }, 3);
                    if (parts.Length == 3)
                        map[parts[0].Trim()] = new[] { parts[1].Trim(), parts[2].Trim() };
                }
                foreach (var d in defs)
                {
                    if (map.TryGetValue(d.Name, out var v))
                    {
                        d.Status = v[0];
                        d.StartType = v[1];
                    }
                }
            });
        }

        public Task<PowerShellResult> DisableAsync(IEnumerable<ServiceDefinition> selected)
        {
            var names = selected.Select(d => d.Name).ToList();
            return Task.Run(() => PowerShellRunner.RunScript(BuildScript(names, "Disabled"), 5));
        }

        public Task<PowerShellResult> RestoreAsync(IEnumerable<ServiceDefinition> selected)
        {
            var names = selected.Select(d => d.Name).ToList();
            return Task.Run(() => PowerShellRunner.RunScript(BuildScript(names, "Manual"), 5));
        }

        private static string BuildScript(List<string> names, string startupType)
        {
            var sb = new StringBuilder();
            sb.AppendLine("$svcs = @(" + string.Join(",", names.Select(n => "'" + n + "'")) + ")");
            sb.AppendLine("$n = 0");
            sb.AppendLine("foreach ($s in $svcs) {");
            sb.AppendLine("  if (Get-Service -Name $s -ErrorAction SilentlyContinue) {");
            sb.AppendLine("    try {");
            sb.AppendLine("      Set-Service -Name $s -StartupType " + startupType + " -ErrorAction Stop");
            if (startupType == "Disabled")
                sb.AppendLine("      Stop-Service -Name $s -Force -ErrorAction SilentlyContinue");
            sb.AppendLine("      $n++");
            sb.AppendLine("    } catch { }");
            sb.AppendLine("  }");
            sb.AppendLine("}");
            sb.AppendLine("Write-Output ('Done=' + $n)");
            return sb.ToString();
        }
    }
}
