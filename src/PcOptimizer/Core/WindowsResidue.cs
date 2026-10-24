using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace PcOptimizer.Core
{
    public class ComponentStoreInfo
    {
        public long ActualSize;
        public long Backups;
        public long Cache;
        public int ReclaimablePackages;
        public bool Recommended;
        public string LastCleanup = "";

        public long Estimated => Backups + Cache;
    }

    public static class WindowsResidue
    {
        static string SystemExe(string name) => Path.Combine(Environment.SystemDirectory, name);

        // ===== DISM =====

        static (string, int) Dism(string args, Action<string>? onLine = null)
        {
            var psi = new ProcessStartInfo(SystemExe("dism.exe"), args + " /English")
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            var sb = new StringBuilder();
            Process proc;
            try { proc = Process.Start(psi)!; }
            catch (System.ComponentModel.Win32Exception) { throw new FileNotFoundException(L.T("没有找到 Windows 自带的 DISM（dism.exe）。")); }
            using (var p = proc)
            {
                var err = p.StandardError.ReadToEndAsync();
                var buf = new StringBuilder();
                int c;
                while ((c = p.StandardOutput.Read()) >= 0)
                {
                    if (c == '\r' || c == '\n')
                    {
                        if (buf.Length > 0) { string line = buf.ToString(); sb.AppendLine(line); onLine?.Invoke(line); buf.Clear(); }
                    }
                    else buf.Append((char)c);
                }
                if (buf.Length > 0) sb.AppendLine(buf.ToString());
                p.WaitForExit();
                sb.Append(err.Result);
                return (sb.ToString(), p.ExitCode);
            }
        }

        public static ComponentStoreInfo AnalyzeComponentStore()
        {
            var (output, code) = Dism("/Online /Cleanup-Image /AnalyzeComponentStore");
            if (code != 0) throw new InvalidOperationException(DismError(output, code));
            return ParseAnalyze(output);
        }

        public static void CleanupComponentStore(IProgress<double>? progress = null)
        {
            var (output, code) = Dism("/Online /Cleanup-Image /StartComponentCleanup", line =>
            {
                var m = Regex.Match(line, @"(\d+(?:\.\d+)?)%");
                if (m.Success && double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double pct)) progress?.Report(pct);
            });
            OpLog.Write("WINSXS_CLEANUP", "exit=" + code);
            if (code != 0) throw new InvalidOperationException(DismError(output, code));
        }

        static string DismError(string output, int code)
        {
            string? msg = output.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("Error:", StringComparison.OrdinalIgnoreCase));
            if (unchecked((uint)code) == 740 || output.Contains("Elevated permissions are required")) return L.T("需要管理员权限。");
            return L.F("DISM 返回 {0}", code) + (msg != null ? L.T("：") + msg : "");
        }

        public static ComponentStoreInfo ParseAnalyze(string output)
        {
            var info = new ComponentStoreInfo();
            string Field(string name)
            {
                var m = Regex.Match(output, "^\\s*" + Regex.Escape(name) + "\\s*:\\s*(.+?)\\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
                return m.Success ? m.Groups[1].Value : "";
            }
            info.ActualSize = ParseSize(Field("Actual Size of Component Store"));
            info.Backups = ParseSize(Field("Backups and Disabled Features"));
            info.Cache = ParseSize(Field("Cache and Temporary Data"));
            int.TryParse(Field("Number of Reclaimable Packages"), out info.ReclaimablePackages);
            info.Recommended = Field("Component Store Cleanup Recommended").StartsWith("Yes", StringComparison.OrdinalIgnoreCase);
            info.LastCleanup = Field("Date of Last Cleanup");
            return info;
        }

        public static long ParseSize(string text)
        {
            var m = Regex.Match(text, @"([\d.,]+)\s*(bytes|KB|MB|GB|TB)", RegexOptions.IgnoreCase);
            if (!m.Success) return 0;
            if (!double.TryParse(m.Groups[1].Value.Replace(",", ""), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double v)) return 0;
            switch (m.Groups[2].Value.ToUpperInvariant())
            {
                case "KB": v *= 1024; break;
                case "MB": v *= 1024 * 1024; break;
                case "GB": v *= 1024L * 1024 * 1024; break;
                case "TB": v *= 1024L * 1024 * 1024 * 1024; break;
            }
            return (long)v;
        }
    }
}
