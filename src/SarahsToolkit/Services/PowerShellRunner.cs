using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace SarahsToolkit.Services
{
    public class PowerShellResult
    {
        public int ExitCode { get; set; }
        public string Output { get; set; }
        public string Error { get; set; }
    }

    /// <summary>
    /// Runs a PowerShell script from a temp file that is always deleted afterwards,
    /// so no helper artifacts are left on the machine.
    /// </summary>
    public static class PowerShellRunner
    {
        public static PowerShellResult RunScript(string scriptBody, int timeoutMinutes = 10)
        {
            string tempFile = Path.Combine(Path.GetTempPath(),
                "sarahstoolkit_" + Guid.NewGuid().ToString("N") + ".ps1");
            try
            {
                File.WriteAllText(tempFile, scriptBody, Encoding.UTF8);
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = "-NoProfile -ExecutionPolicy Bypass -File \"" + tempFile + "\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                using (var p = Process.Start(psi))
                {
                    if (p == null)
                        return new PowerShellResult { ExitCode = -1, Error = "Failed to start PowerShell." };

                    var output = new StringBuilder();
                    var error = new StringBuilder();
                    p.OutputDataReceived += (s, e) => { if (e.Data != null) output.AppendLine(e.Data); };
                    p.ErrorDataReceived += (s, e) => { if (e.Data != null) error.AppendLine(e.Data); };
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();

                    bool exited = p.WaitForExit(timeoutMinutes * 60 * 1000);
                    if (!exited)
                    {
                        try { p.Kill(); } catch { }
                        return new PowerShellResult { ExitCode = 99, Output = output.ToString(), Error = "Timed out." };
                    }
                    p.WaitForExit(); // drain async output handlers
                    return new PowerShellResult
                    {
                        ExitCode = p.ExitCode,
                        Output = output.ToString(),
                        Error = error.ToString()
                    };
                }
            }
            finally
            {
                try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }
            }
        }
    }
}
