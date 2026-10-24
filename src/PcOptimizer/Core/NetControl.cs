using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;

namespace PcOptimizer.Core
{
    public class NetApp : INotifyPropertyChanged
    {
        public string Name { get; set; } = "";
        public string Path { get; set; } = "";
        public List<int> Pids { get; } = new List<int>();
        public int Established { get; set; }
        public int Listening { get; set; }
        public int Udp { get; set; }
        public List<string> Remotes { get; } = new List<string>();
        public bool IsSystem { get; set; }

        bool _blocked;
        public bool Blocked { get => _blocked; set { _blocked = value; Raise(nameof(Blocked)); Raise(nameof(CanBlock)); Raise(nameof(CanUnblock)); } }
        long _limitKBps;
        public long LimitKBps { get => _limitKBps; set { _limitKBps = value; Raise(nameof(LimitKBps)); Raise(nameof(LimitText)); Raise(nameof(HasLimit)); } }

        public bool CanControl => Path.Length > 0;
        public bool CanBlock => CanControl && !Blocked;
        public bool CanUnblock => CanControl && Blocked;
        public bool HasLimit => LimitKBps > 0;
        public string LimitText => LimitKBps > 0 ? L.F("上传限速 {0} KB/s", LimitKBps) : "";
        public string Initial => string.IsNullOrEmpty(Name) ? "?" : Name.Substring(0, 1).ToUpperInvariant();
        public int Total => Established + Listening + Udp;

        public string Meta
        {
            get
            {
                var parts = new List<string>();
                if (Established > 0) parts.Add(L.F("{0} 个连接", Established));
                if (Listening > 0) parts.Add(L.F("{0} 个端口在监听", Listening));
                if (Udp > 0) parts.Add(L.F("{0} 个 UDP 端口", Udp));
                if (parts.Count == 0) parts.Add(Blocked ? L.T("未运行") : L.T("当前没有连接"));
                string s = string.Join(" · ", parts);
                if (Remotes.Count > 0)
                    s += L.T(" · 连到 ") + string.Join(L.T("、"), Remotes.Take(3)) + (Remotes.Count > 3 ? L.F(" 等 {0} 个地址", Remotes.Count) : "");
                return s;
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        void Raise(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    public static class NetControl
    {
        public const string RulePrefix = "透明优化助手-禁止联网";
        const string QosPrefix = "PcOpt-";

        public static List<NetApp> List()
        {
            var apps = new Dictionary<string, NetApp>(StringComparer.OrdinalIgnoreCase);
            var pidKey = new Dictionary<int, string>();
            string windir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

            NetApp AppOf(int pid)
            {
                if (!pidKey.TryGetValue(pid, out var key))
                {
                    var d = Describe(pid);
                    key = d.Key;
                    pidKey[pid] = key;
                    if (!apps.ContainsKey(key))
                        apps[key] = new NetApp { Name = d.Name, Path = d.Path, IsSystem = d.IsSystem };
                }
                var a = apps[key];
                if (!a.Pids.Contains(pid)) a.Pids.Add(pid);
                return a;
            }

            foreach (var c in TcpRows())
            {
                var a = AppOf(c.Pid);
                if (c.State == 2) a.Listening++;                         // LISTEN
                else if (c.State == 5)                                    // ESTABLISHED
                {
                    a.Established++;
                    if (!IPAddress.IsLoopback(c.Remote) && !a.Remotes.Contains(c.RemoteText)) a.Remotes.Add(c.RemoteText);
                }
            }
            foreach (int pid in UdpPids()) AppOf(pid).Udp++;

            foreach (var (path, _) in BlockedRules())
            {
                if (!apps.TryGetValue(path, out var a))
                    apps[path] = a = new NetApp { Name = DisplayName(path), Path = path, IsSystem = FsUtil.IsWithin(path, windir) };
                a.Blocked = true;
            }
            return apps.Values
                .OrderByDescending(a => a.Blocked)
                .ThenByDescending(a => a.Established)
                .ThenByDescending(a => a.Total)
                .ToList();
        }

        public static PidInfo Describe(int pid)
        {
            string windir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string? path = pid > 4 ? MemoryManager.ImagePath(pid) : null;
            if (path != null) return new PidInfo(path, DisplayName(path), path, FsUtil.IsWithin(path, windir));
            string name;
            try { using (var p = Process.GetProcessById(pid)) name = p.ProcessName; }
            catch (Exception) { name = pid == 4 ? "System" : L.F("进程 {0}", pid); }
            if (pid == 0) name = L.T("系统（已断开、等待回收的连接）");
            return new PidInfo("pid:" + name, name, "", true);
        }

        // Connections that have (or are opening, or are closing) a session with a remote address.
        // LISTEN and TIME_WAIT are left out: TIME_WAIT rows no longer have an owning process.
        public static List<RemoteConn> ActiveRemotes()
        {
            var list = new List<RemoteConn>();
            foreach (var c in TcpRows())
            {
                if (c.Pid <= 0 || c.State < 3 || c.State > 10) continue;
                if (IPAddress.IsLoopback(c.Remote) || c.Remote.Equals(IPAddress.Any) || c.Remote.Equals(IPAddress.IPv6Any)) continue;
                list.Add(new RemoteConn(c.Pid, c.RemoteText));
            }
            return list;
        }

        static string DisplayName(string path)
        {
            try
            {
                var v = FileVersionInfo.GetVersionInfo(path);
                string d = (v.FileDescription ?? "").Trim();
                if (d.Length > 0 && d.Length <= 60) return d;
            }
            catch (Exception) { }
            return System.IO.Path.GetFileNameWithoutExtension(path);
        }

        struct TcpRow { public int Pid; public int State; public IPAddress Remote; public string RemoteText; }

        static IEnumerable<TcpRow> TcpRows()
        {
            foreach (var row in ReadTable(NativeMethods.AF_INET, true))
            {
                var ip = new IPAddress(BitConverter.ToUInt32(row, 12));
                int port = Port(row, 16);
                yield return new TcpRow { State = BitConverter.ToInt32(row, 0), Pid = BitConverter.ToInt32(row, 20), Remote = ip, RemoteText = $"{ip}:{port}" };
            }
            foreach (var row in ReadTable(NativeMethods.AF_INET6, true))
            {
                // MIB_TCP6ROW_OWNER_PID: localAddr[16], localScope, localPort, remoteAddr[16], remoteScope, remotePort, state, pid
                var bytes = new byte[16];
                Array.Copy(row, 24, bytes, 0, 16);
                var ip = new IPAddress(bytes);
                int port = Port(row, 44);
                yield return new TcpRow { State = BitConverter.ToInt32(row, 48), Pid = BitConverter.ToInt32(row, 52), Remote = ip, RemoteText = $"[{ip}]:{port}" };
            }
        }

        static IEnumerable<int> UdpPids()
        {
            foreach (var row in ReadTable(NativeMethods.AF_INET, false)) yield return BitConverter.ToInt32(row, 8);    // addr, port, pid
            foreach (var row in ReadTable(NativeMethods.AF_INET6, false)) yield return BitConverter.ToInt32(row, 24);  // addr[16], scope, port, pid
        }

        public static List<ListenPort> ListeningPorts(bool includeUdp)
        {
            var sockets = new List<ListenSocket>();
            foreach (var row in ReadTable(NativeMethods.AF_INET, true))
                if (BitConverter.ToInt32(row, 0) == 2)
                    sockets.Add(new ListenSocket("TCP", new IPAddress(BitConverter.ToUInt32(row, 4)), Port(row, 8), BitConverter.ToInt32(row, 20)));
            foreach (var row in ReadTable(NativeMethods.AF_INET6, true))
                if (BitConverter.ToInt32(row, 48) == 2)
                    sockets.Add(new ListenSocket("TCP", V6(row, 0), Port(row, 20), BitConverter.ToInt32(row, 52)));
            if (includeUdp)
            {
                foreach (var row in ReadTable(NativeMethods.AF_INET, false))
                    sockets.Add(new ListenSocket("UDP", new IPAddress(BitConverter.ToUInt32(row, 0)), Port(row, 4), BitConverter.ToInt32(row, 8)));
                foreach (var row in ReadTable(NativeMethods.AF_INET6, false))
                    sockets.Add(new ListenSocket("UDP", V6(row, 0), Port(row, 20), BitConverter.ToInt32(row, 24)));
            }

            var ports = Ports.Merge(sockets);
            string windir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            var services = ServicesByPid();
            var blocked = new HashSet<string>(BlockedRules().Select(r => r.Path), StringComparer.OrdinalIgnoreCase);
            var names = new Dictionary<int, (string, string)>();
            foreach (var p in ports)
            {
                if (!names.TryGetValue(p.Pid, out var n))
                {
                    string? path = p.Pid > 4 ? MemoryManager.ImagePath(p.Pid) : null;
                    string name;
                    if (path != null) name = DisplayName(path);
                    else if (p.Pid == 4) name = L.T("Windows 内核（System）");
                    else if (p.Pid == 0) name = L.T("系统");
                    else
                    {
                        try { using (var proc = Process.GetProcessById(p.Pid)) name = proc.ProcessName; }
                        catch (Exception) { name = L.F("进程 {0}", p.Pid); }
                    }
                    names[p.Pid] = n = (name, path ?? "");
                }
                p.ProcessName = n.Item1;
                p.Path = n.Item2;
                p.IsSystem = p.Path.Length == 0 || FsUtil.IsWithin(p.Path, windir);
                if (services.TryGetValue(p.Pid, out var svc)) p.Services = svc;
                p.Blocked = p.Path.Length > 0 && blocked.Contains(p.Path);
            }
            return ports;
        }

        static IPAddress V6(byte[] row, int offset)
        {
            var bytes = new byte[16];
            Array.Copy(row, offset, bytes, 0, 16);
            return new IPAddress(bytes);
        }

        static Dictionary<int, string> ServicesByPid()
        {
            var d = new Dictionary<int, List<string>>();
            try
            {
                using (var searcher = new System.Management.ManagementObjectSearcher("SELECT DisplayName, ProcessId FROM Win32_Service WHERE State = 'Running'"))
                    foreach (System.Management.ManagementObject o in searcher.Get())
                        using (o)
                        {
                            int pid = Convert.ToInt32(o["ProcessId"]);
                            string name = (o["DisplayName"] as string ?? "").Trim();
                            if (pid <= 0 || name.Length == 0) continue;
                            if (!d.TryGetValue(pid, out var list)) d[pid] = list = new List<string>();
                            list.Add(name);
                        }
            }
            catch (Exception) { }
            return d.ToDictionary(kv => kv.Key, kv => string.Join(L.T("、"), kv.Value.Take(2)) + (kv.Value.Count > 2 ? L.F(" 等 {0} 个服务", kv.Value.Count) : ""));
        }

        static int Port(byte[] row, int offset) => (row[offset] << 8) | row[offset + 1];

        static List<byte[]> ReadTable(int af, bool tcp)
        {
            int rowSize = tcp ? (af == NativeMethods.AF_INET ? 24 : 56) : (af == NativeMethods.AF_INET ? 12 : 28);
            var rows = new List<byte[]>();
            int size = 0;
            for (int attempt = 0; attempt < 5; attempt++)
            {
                IntPtr buf = size > 0 ? Marshal.AllocHGlobal(size) : IntPtr.Zero;
                try
                {
                    uint rc = tcp
                        ? NativeMethods.GetExtendedTcpTable(buf, ref size, false, af, NativeMethods.TCP_TABLE_OWNER_PID_ALL, 0)
                        : NativeMethods.GetExtendedUdpTable(buf, ref size, false, af, NativeMethods.UDP_TABLE_OWNER_PID, 0);
                    if (rc == NativeMethods.ERROR_INSUFFICIENT_BUFFER) continue;
                    if (rc != 0 || buf == IntPtr.Zero) return rows;
                    int count = Marshal.ReadInt32(buf);
                    for (int i = 0; i < count; i++)
                    {
                        var row = new byte[rowSize];
                        Marshal.Copy(buf + 4 + i * rowSize, row, 0, rowSize);
                        rows.Add(row);
                    }
                    return rows;
                }
                finally { if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf); }
            }
            return rows;
        }

        static dynamic Policy()
        {
            var t = Type.GetTypeFromProgID("HNetCfg.FwPolicy2") ?? throw new NotSupportedException(L.T("这台电脑上没有 Windows 防火墙组件"));
            return Activator.CreateInstance(t)!;
        }

        public static bool? FirewallOn()
        {
            try
            {
                dynamic p = Policy();
                int current = p.CurrentProfileTypes;
                foreach (int prof in new[] { 1, 2, 4 })
                    if ((current & prof) != 0 && !(bool)p.FirewallEnabled[prof]) return false;
                return true;
            }
            catch (Exception) { return null; }
        }

        public static List<(string Path, string Name)> BlockedRules()
        {
            var list = new List<(string, string)>();
            try
            {
                dynamic p = Policy();
                foreach (dynamic r in p.Rules)
                {
                    string name = r.Name ?? "";
                    string app = r.ApplicationName ?? "";
                    if (name.StartsWith(RulePrefix, StringComparison.Ordinal) && app.Length > 0 &&
                        !list.Any(x => string.Equals(x.Item1, app, StringComparison.OrdinalIgnoreCase)))
                        list.Add((app, name));
                }
            }
            catch (Exception) { }
            return list;
        }

        public static void Block(string path, string appName)
        {
            dynamic p = Policy();
            var ruleType = Type.GetTypeFromProgID("HNetCfg.FWRule")!;
            foreach (int dir in new[] { 2, 1 })
            {
                dynamic r = Activator.CreateInstance(ruleType)!;
                r.Name = L.F("{0}（{1}）- {2}", RulePrefix, (dir == 2 ? L.T("出站") : L.T("入站")), appName);
                r.Description = L.T("由透明优化助手添加。删除这条规则即恢复联网。");
                r.ApplicationName = path;
                r.Direction = dir;
                r.Action = 0;
                r.Profiles = 0x7FFFFFFF;
                r.Enabled = true;
                p.Rules.Add(r);
            }
            OpLog.Write("NET_BLOCK", path);
        }

        public static int Unblock(string path)
        {
            dynamic p = Policy();
            var names = new List<string>();
            foreach (dynamic r in p.Rules)
            {
                string name = r.Name ?? "";
                string app = r.ApplicationName ?? "";
                if (name.StartsWith(RulePrefix, StringComparison.Ordinal) && string.Equals(app, path, StringComparison.OrdinalIgnoreCase))
                    names.Add(name);
            }
            foreach (var n in names.Distinct()) p.Rules.Remove(n);
            OpLog.Write("NET_UNBLOCK", $"{path}\trules={names.Count}");
            return names.Count;
        }

        static string QosName(string path)
        {
            using (var md5 = System.Security.Cryptography.MD5.Create())
            {
                var h = md5.ComputeHash(Encoding.UTF8.GetBytes(path.ToLowerInvariant()));
                return QosPrefix + BitConverter.ToString(h, 0, 4).Replace("-", "");
            }
        }

        static string Quote(string s) => "'" + s.Replace("'", "''") + "'";

        public static Dictionary<string, long> Limits()
        {
            var d = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            string? output = PowerShell.Run(
                $"Get-NetQosPolicy -ErrorAction SilentlyContinue | Where-Object {{ $_.Name -like {Quote(QosPrefix + "*")} }} | " +
                "ForEach-Object { $_.AppPathName + \"`t\" + $_.ThrottleRateAction }", out _);
            foreach (var line in (output ?? "").Split('\n'))
            {
                var parts = line.Trim().Split('\t');
                if (parts.Length == 2 && long.TryParse(parts[1], out long bits) && bits > 0)
                    d[parts[0]] = bits / 8 / 1024;
            }
            return d;
        }

        public static void SetLimit(string path, long kbps)
        {
            string name = Quote(QosName(path));
            string script = $"Remove-NetQosPolicy -Name {name} -Confirm:$false -ErrorAction SilentlyContinue; ";
            if (kbps > 0)
                script += $"New-NetQosPolicy -Name {name} -AppPathNameMatchCondition {Quote(path)} " +
                          $"-ThrottleRateActionBitsPerSecond {kbps * 1024 * 8} -ErrorAction Stop | Out-Null";
            PowerShell.Run(script, out string err);
            if (err.Trim().Length > 0) throw new InvalidOperationException(err.Trim());
            OpLog.Write(kbps > 0 ? "NET_LIMIT" : "NET_UNLIMIT", $"{path}\t{kbps} KB/s");
        }
    }
}
