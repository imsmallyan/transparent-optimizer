using System;
using System.Diagnostics;
using System.Text;

namespace PcOptimizer.Core
{
    public static class PowerShell
    {
        public static string? Run(string script, out string error, int timeoutMs = 30000)
        {
            error = "";
            string full = "[Console]::OutputEncoding = [Text.Encoding]::UTF8; $ErrorActionPreference = 'Continue'; " + script;
            var psi = new ProcessStartInfo("powershell.exe",
                "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(full)))
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            };
            try
            {
                using (var p = Process.Start(psi)!)
                {
                    var errTask = p.StandardError.ReadToEndAsync();
                    string output = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(timeoutMs);
                    error = errTask.Result;
                    return output;
                }
            }
            catch (Exception ex) { error = ex.Message; return null; }
        }
    }
}
