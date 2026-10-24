using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Text;
using System.Threading;

namespace PcOptimizer.Core
{
    public class DefenderStatus
    {
        public bool Available;
        public bool ServiceOn, AntivirusOn, RealTimeOn, TamperProtected;
        public string RunningMode = "";     // Normal / Passive Mode / EDR Block Mode
        public DateTime? SignatureUpdated;
        public string SignatureVersion = "";
        public DateTime? QuickScanEnd, FullScanEnd;
        public List<string> OtherAntivirus = new List<string>();

        public bool Passive => RunningMode.IndexOf("Passive", StringComparison.OrdinalIgnoreCase) >= 0;
        public bool CanScan => Available && ServiceOn && AntivirusOn && Defender.MpCmdRunPath() != null;
        public int SignatureAgeDays => SignatureUpdated.HasValue ? Math.Max(0, (int)(DateTime.Now - SignatureUpdated.Value).TotalDays) : 999;
    }

    public class ThreatRecord
    {
        public string Name { get; set; } = "";
        public string Severity { get; set; } = "";
        public bool SevereOrHigh { get; set; }
        public string Status { get; set; } = "";
        public bool Handled { get; set; }
        public DateTime Time { get; set; }
        public string Path { get; set; } = "";
        public string TimeText => Time == default ? "" : Time.ToString("yyyy-MM-dd HH:mm");
    }

    public class ScanResult
    {
        public int ExitCode;
        public bool ThreatsFound => ExitCode == 2;
        public bool Ok => ExitCode == 0 || ExitCode == 2;
        public string Output = "";
    }

    public static class Defender
    {
        const string Ns = @"root\Microsoft\Windows\Defender";

        public static DefenderStatus Status()
        {
            var s = new DefenderStatus();
            try
            {
                using (var searcher = new ManagementObjectSearcher(Ns, "SELECT * FROM MSFT_MpComputerStatus"))
                    foreach (ManagementObject o in searcher.Get())
                    {
                        s.Available = true;
                        s.ServiceOn = Bool(o, "AMServiceEnabled");
                        s.AntivirusOn = Bool(o, "AntivirusEnabled");
                        s.RealTimeOn = Bool(o, "RealTimeProtectionEnabled");
                        s.TamperProtected = Bool(o, "IsTamperProtected");
                        s.RunningMode = Str(o, "AMRunningMode");
                        s.SignatureVersion = Str(o, "AntivirusSignatureVersion");
                        s.SignatureUpdated = Date(o, "AntivirusSignatureLastUpdated");
                        s.QuickScanEnd = Date(o, "QuickScanEndTime");
                        s.FullScanEnd = Date(o, "FullScanEndTime");
                    }
            }
            catch (Exception) { }

            try
            {
                using (var searcher = new ManagementObjectSearcher(@"root\SecurityCenter2", "SELECT displayName, productState FROM AntiVirusProduct"))
                    foreach (ManagementObject o in searcher.Get())
                    {
                        string name = Str(o, "displayName");
                        uint state = Convert.ToUInt32(o["productState"] ?? 0u);
                        bool on = ((state >> 12) & 0xF) == 1;
                        if (on && name.Length > 0 && name.IndexOf("Defender", StringComparison.OrdinalIgnoreCase) < 0)
                            s.OtherAntivirus.Add(name);
                    }
            }
            catch (Exception) { }
            return s;
        }

        public static List<ThreatRecord> Threats()
        {
            var list = new List<ThreatRecord>();
            try
            {
                var names = new Dictionary<ulong, (string Name, uint Severity)>();
                using (var searcher = new ManagementObjectSearcher(Ns, "SELECT ThreatID, ThreatName, SeverityID FROM MSFT_MpThreat"))
                    foreach (ManagementObject o in searcher.Get())
                        names[Convert.ToUInt64(o["ThreatID"])] = (Str(o, "ThreatName"), Convert.ToUInt32(o["SeverityID"] ?? 0u));

                using (var searcher = new ManagementObjectSearcher(Ns, "SELECT * FROM MSFT_MpThreatDetection"))
                    foreach (ManagementObject o in searcher.Get())
                    {
                        ulong id = Convert.ToUInt64(o["ThreatID"]);
                        names.TryGetValue(id, out var t);
                        uint status = Convert.ToUInt32(o["ThreatStatusID"] ?? 0u);
                        var resources = o["Resources"] as string[] ?? new string[0];
                        list.Add(new ThreatRecord
                        {
                            Name = t.Name ?? L.F("威胁 {0}", id),
                            Severity = SeverityText(t.Severity),
                            SevereOrHigh = t.Severity >= 4,
                            Status = StatusText(status),
                            Handled = status >= 2 && status <= 4 || status == 6,
                            Time = Date(o, "InitialDetectionTime") ?? default,
                            Path = string.Join("\n", resources.Select(CleanResource)),
                        });
                    }
            }
            catch (Exception) { }
            return list.OrderByDescending(t => t.Time).ToList();
        }

        /// <summary>“file:_C:\x.exe” → “C:\x.exe”</summary>
        static string CleanResource(string r)
        {
            int i = r.IndexOf(":_", StringComparison.Ordinal);
            return i > 0 && i < 12 ? r.Substring(i + 2) : r;
        }

        static string SeverityText(uint s) =>
            s == 5 ? L.T("严重") : s == 4 ? L.T("高危") : s == 2 ? L.T("中等") : s == 1 ? L.T("低危") : L.T("未知");

        static string StatusText(uint s)
        {
            switch (s)
            {
                case 1: return L.T("已发现 未处理");
                case 2: return L.T("已清除");
                case 3: return L.T("已隔离");
                case 4: return L.T("已删除");
                case 5: return L.T("已允许（你选择了放行）");
                case 6: return L.T("已阻止");
                case 102: return L.T("隔离失败");
                case 103: return L.T("删除失败");
                case 104: return L.T("放行失败");
                case 105: return L.T("已放弃处理");
                case 107: return L.T("阻止失败");
                default: return L.T("状态未知");
            }
        }

        public static string? MpCmdRunPath()
        {
            try
            {
                string platform = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Microsoft\Windows Defender\Platform");
                if (Directory.Exists(platform))
                {
                    var newest = Directory.GetDirectories(platform)
                        .Select(d => (Dir: d, Ver: Version.TryParse(Path.GetFileName(d).Split('-')[0], out var v) ? v : null))
                        .Where(x => x.Ver != null && File.Exists(Path.Combine(x.Dir, "MpCmdRun.exe")))
                        .OrderByDescending(x => x.Ver).FirstOrDefault();
                    if (newest.Dir != null) return Path.Combine(newest.Dir, "MpCmdRun.exe");
                }
            }
            catch (Exception) { }
            string pf = Environment.GetEnvironmentVariable("ProgramW6432") ?? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string old = Path.Combine(pf, @"Windows Defender\MpCmdRun.exe");
            return File.Exists(old) ? old : null;
        }

        public enum ScanKind { Quick = 1, Full = 2, Custom = 3 }

        public static ScanResult Scan(ScanKind kind, string? path, IProgress<string>? output, CancellationToken ct)
        {
            string args = $"-Scan -ScanType {(int)kind}" + (kind == ScanKind.Custom ? $" -File \"{path!.TrimEnd('\\')}\"" : "");
            OpLog.Write("AV_SCAN", kind + (path != null ? "\t" + path : ""));
            var r = Run(args, output, ct, cancelArgs: "-Cancel");
            OpLog.Write("AV_SCAN_END", $"exit={r.ExitCode}");
            return r;
        }

        public static ScanResult UpdateSignatures(IProgress<string>? output, CancellationToken ct) =>
            Run("-SignatureUpdate", output, ct, cancelArgs: null);

        static ScanResult Run(string args, IProgress<string>? output, CancellationToken ct, string? cancelArgs)
        {
            string exe = MpCmdRunPath() ?? throw new InvalidOperationException(L.T("找不到 Microsoft Defender 的命令行工具 MpCmdRun.exe"));
            Encoding enc;
            try { enc = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage); } catch (Exception) { enc = Encoding.Default; }
            var psi = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = enc, StandardErrorEncoding = enc,
            };
            var text = new StringBuilder();
            using (var p = new Process { StartInfo = psi })
            {
                DataReceivedEventHandler onLine = (s, e) =>
                {
                    if (string.IsNullOrWhiteSpace(e.Data)) return;
                    lock (text) text.AppendLine(e.Data);
                    output?.Report(e.Data.Trim());
                };
                p.OutputDataReceived += onLine;
                p.ErrorDataReceived += onLine;
                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                while (!p.WaitForExit(500))
                {
                    if (!ct.IsCancellationRequested) continue;
                    try { p.Kill(); } catch (Exception) { }
                    if (cancelArgs != null)
                        try { using (var c = Process.Start(new ProcessStartInfo(exe, cancelArgs) { UseShellExecute = false, CreateNoWindow = true })) c?.WaitForExit(10000); }
                        catch (Exception) { }
                    ct.ThrowIfCancellationRequested();
                }
                p.WaitForExit();
                lock (text) return new ScanResult { ExitCode = p.ExitCode, Output = text.ToString() };
            }
        }

        public static void OpenSecurityCenter()
        {
            try { Process.Start(new ProcessStartInfo("windowsdefender://threat") { UseShellExecute = true }); }
            catch (Exception) { Process.Start(new ProcessStartInfo("ms-settings:windowsdefender") { UseShellExecute = true }); }
        }

        static bool Bool(ManagementBaseObject o, string p) { try { return o[p] is bool b && b; } catch (Exception) { return false; } }
        static string Str(ManagementBaseObject o, string p) { try { return o[p]?.ToString() ?? ""; } catch (Exception) { return ""; } }
        static DateTime? Date(ManagementBaseObject o, string p)
        {
            try
            {
                var v = o[p];
                if (v is DateTime d) return d;
                if (v is string s && s.Length > 0) return ManagementDateTimeConverter.ToDateTime(s);
            }
            catch (Exception) { }
            return null;
        }
    }
}
