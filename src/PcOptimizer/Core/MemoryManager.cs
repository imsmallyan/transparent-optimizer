using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace PcOptimizer.Core
{
    public class AppGroup : INotifyPropertyChanged
    {
        public string Name { get; set; } = "";
        public string Path { get; set; } = "";
        public List<int> Pids { get; } = new List<int>();
        public long Memory { get; set; }
        public bool HasWindow { get; set; }
        public StartupItem? Startup { get; set; }

        public string MemoryText => FsUtil.HumanSize(Memory);
        public string Initial => string.IsNullOrEmpty(Name) ? "?" : Name.Substring(0, 1).ToUpperInvariant();
        public bool IsBackground => !HasWindow;
        public bool HasStartup => Startup != null;
        public bool StartupEnabled => Startup?.Enabled == true;
        public string StartupText => Startup == null ? "" : Startup.Enabled ? L.T("开机自启") : L.T("自启已关闭");
        public string Meta => (Pids.Count > 1 ? L.F("{0} 个进程 · ", Pids.Count) : "") + Path;

        bool _checked;
        public bool Checked { get => _checked; set { _checked = value; Raise(nameof(Checked)); } }

        string _status = "";
        public string Status { get => _status; set { _status = value; Raise(nameof(Status)); } }

        public void StartupChanged()
        {
            Raise(nameof(StartupEnabled));
            Raise(nameof(StartupText));
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        void Raise(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    public class EndResult
    {
        public int Closed;
        public int Killed;
        public List<string> Failed = new List<string>();
    }

    public static class MemoryManager
    {
        static readonly HashSet<string> Protected = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "MsMpEng", "NisSrv", "MpDefenderCoreService", "MsSense", "SecurityHealthService",
            "explorer", "dwm", "csrss", "winlogon", "sihost", "ctfmon", "fontdrvhost", "svchost",
        };

        public static List<AppGroup> List()
        {
            var self = Process.GetCurrentProcess();
            int session = self.SessionId;
            string windir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

            var groups = new Dictionary<string, AppGroup>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in Process.GetProcesses())
            {
                using (p)
                {
                    try
                    {
                        if (p.Id == self.Id || p.Id <= 4 || p.SessionId != session) continue;
                        if (Protected.Contains(p.ProcessName)) continue;
                        string? path = ImagePath(p.Id);
                        if (string.IsNullOrEmpty(path)) continue;
                        if (FsUtil.IsWithin(path!, windir)) continue;

                        if (!groups.TryGetValue(path!, out var g))
                        {
                            g = new AppGroup { Path = path!, Name = DisplayName(path!) };
                            groups[path!] = g;
                        }
                        g.Pids.Add(p.Id);
                        long ws = p.WorkingSet64;
                        g.Memory += ws > 0 ? ws : WorkingSet(p.Id);
                        if (p.MainWindowHandle != IntPtr.Zero) g.HasWindow = true;
                    }
                    catch (Exception) { }
                }
            }

            List<StartupItem> startup;
            try { startup = StartupManager.ListItems(); }
            catch (Exception) { startup = new List<StartupItem>(); }
            foreach (var g in groups.Values)
            {
                var matches = startup.Where(s => string.Equals(s.TargetPath, g.Path, StringComparison.OrdinalIgnoreCase)).ToList();
                g.Startup = matches.FirstOrDefault(s => s.Enabled) ?? matches.FirstOrDefault();
            }
            return groups.Values.OrderByDescending(g => g.Memory).ToList();
        }

        public static EndResult End(AppGroup g, int waitMs = 3000)
        {
            var r = new EndResult();
            var procs = new List<Process>();
            foreach (int pid in g.Pids)
            {
                try
                {
                    var p = Process.GetProcessById(pid);
                    if (!string.Equals(ImagePath(pid), g.Path, StringComparison.OrdinalIgnoreCase)) { p.Dispose(); continue; }
                    procs.Add(p);
                }
                catch (ArgumentException) { }
            }

            foreach (var p in procs)
            {
                try { if (!p.HasExited && p.MainWindowHandle != IntPtr.Zero) p.CloseMainWindow(); }
                catch (Exception) { }
            }
            var deadline = DateTime.UtcNow.AddMilliseconds(waitMs);
            foreach (var p in procs)
            {
                try
                {
                    int left = (int)Math.Max(0, (deadline - DateTime.UtcNow).TotalMilliseconds);
                    if (p.WaitForExit(left)) { r.Closed++; continue; }
                    p.Kill();
                    p.WaitForExit(2000);
                    r.Killed++;
                }
                catch (InvalidOperationException) { r.Closed++; }
                catch (Exception ex) { r.Failed.Add(L.F("{0}（进程 {1}）：{2}", g.Name, SafeId(p), ex.Message)); }
                finally { p.Dispose(); }
            }
            OpLog.Write("PROC_END", $"{g.Name}\t{g.Path}\tclosed={r.Closed}\tkilled={r.Killed}\tfailed={r.Failed.Count}");
            return r;
        }

        static string SafeId(Process p) { try { return p.Id.ToString(); } catch (Exception) { return "?"; } }

        static string DisplayName(string path)
        {
            try
            {
                var v = FileVersionInfo.GetVersionInfo(path);
                string d = (v.FileDescription ?? "").Trim();
                if (d.Length > 0 && d.Length <= 60) return d;
                string pn = (v.ProductName ?? "").Trim();
                if (pn.Length > 0 && pn.Length <= 60) return pn;
            }
            catch (Exception) { }
            return System.IO.Path.GetFileNameWithoutExtension(path);
        }

        static long WorkingSet(int pid)
        {
            IntPtr h = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION | NativeMethods.PROCESS_VM_READ, false, pid);
            if (h == IntPtr.Zero) h = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return 0;
            try
            {
                var c = new NativeMethods.PROCESS_MEMORY_COUNTERS();
                uint size = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(NativeMethods.PROCESS_MEMORY_COUNTERS));
                return NativeMethods.GetProcessMemoryInfo(h, out c, size) ? (long)c.WorkingSetSize.ToUInt64() : 0;
            }
            finally { NativeMethods.CloseHandle(h); }
        }

        public static string? ImagePath(int pid)
        {
            IntPtr h = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return null;
            try
            {
                var sb = new StringBuilder(1024);
                int size = sb.Capacity;
                return NativeMethods.QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
            }
            finally { NativeMethods.CloseHandle(h); }
        }
    }
}
