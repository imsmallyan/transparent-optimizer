using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Net;

namespace PcOptimizer.Core
{
    public readonly struct RemoteConn
    {
        public readonly int Pid;
        public readonly string Remote;
        public RemoteConn(int pid, string remote) { Pid = pid; Remote = remote; }
    }

    public readonly struct PidInfo
    {
        public readonly string Key, Name, Path;
        public readonly bool IsSystem;
        public PidInfo(string key, string name, string path, bool isSystem) { Key = key; Name = name; Path = path; IsSystem = isSystem; }
    }

    public class SeenApp : INotifyPropertyChanged
    {
        public const int MaxRemotes = 100;

        public string Key { get; set; } = "";
        public string Name { get; set; } = "";
        public string Path { get; set; } = "";
        public bool IsSystem { get; set; }
        public DateTime FirstSeen { get; set; }
        public DateTime LastSeen { get; set; }
        public List<string> Remotes { get; } = new List<string>();

        bool _blocked;
        public bool Blocked { get => _blocked; set { _blocked = value; Raise(nameof(Blocked)); Raise(nameof(CanBlock)); Raise(nameof(CanUnblock)); } }
        public bool CanControl => Path.Length > 0;
        public bool CanBlock => CanControl && !Blocked;
        public bool CanUnblock => CanControl && Blocked;
        public string Initial => string.IsNullOrEmpty(Name) ? "?" : Name.Substring(0, 1).ToUpperInvariant();

        public string Meta
        {
            get
            {
                string time = FirstSeen == LastSeen
                    ? L.F("{0} 联网", FirstSeen.ToString("HH:mm:ss"))
                    : L.F("{0} – {1} 联网", FirstSeen.ToString("HH:mm:ss"), LastSeen.ToString("HH:mm:ss"));
                return time + L.T(" · 连到 ") + string.Join(L.T("、"), Remotes.Take(3)) +
                       (Remotes.Count > 3 ? L.F(" 等 {0} 个地址", Remotes.Count) : "");
            }
        }

        public string RemotesText => string.Join("\n", Remotes) + (Remotes.Count >= MaxRemotes ? "\n" + L.F("（只保留前 {0} 个）", MaxRemotes) : "");

        internal void Touched() { Raise(nameof(Meta)); Raise(nameof(RemotesText)); }

        public event PropertyChangedEventHandler? PropertyChanged;
        void Raise(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    // Collects every program that had a remote connection while recording runs.
    // The caller reads the connection table on a timer; this class only merges the samples.
    public class NetRecorder
    {
        readonly Dictionary<string, SeenApp> _apps = new Dictionary<string, SeenApp>(StringComparer.OrdinalIgnoreCase);
        Dictionary<int, string> _pidKey = new Dictionary<int, string>();

        public List<SeenApp> Apps { get; } = new List<SeenApp>();
        public int Samples { get; private set; }

        // Process ids are reused by Windows, so a pid is only trusted while it stays in consecutive samples.
        public List<int> UnknownPids(IEnumerable<RemoteConn> conns) =>
            conns.Select(c => c.Pid).Distinct().Where(pid => !_pidKey.ContainsKey(pid)).ToList();

        // Returns the programs seen for the first time in this sample.
        public List<SeenApp> Add(DateTime now, IList<RemoteConn> conns, IDictionary<int, PidInfo> described, ICollection<string>? blockedPaths = null)
        {
            Samples++;
            var added = new List<SeenApp>();
            var pidKey = new Dictionary<int, string>();
            var touched = new HashSet<SeenApp>();
            foreach (var c in conns)
            {
                if (!pidKey.TryGetValue(c.Pid, out var key))
                {
                    if (!_pidKey.TryGetValue(c.Pid, out key))
                    {
                        if (!described.TryGetValue(c.Pid, out var info)) continue;
                        key = info.Key;
                        if (!_apps.ContainsKey(key))
                        {
                            var app = new SeenApp
                            {
                                Key = key, Name = info.Name, Path = info.Path, IsSystem = info.IsSystem, FirstSeen = now, LastSeen = now,
                                Blocked = info.Path.Length > 0 && blockedPaths != null && blockedPaths.Contains(info.Path),
                            };
                            _apps[key] = app;
                            Apps.Add(app);
                            added.Add(app);
                        }
                    }
                    pidKey[c.Pid] = key;
                }
                var a = _apps[key];
                a.LastSeen = now;
                if (a.Remotes.Count < SeenApp.MaxRemotes && !a.Remotes.Contains(c.Remote)) a.Remotes.Add(c.Remote);
                touched.Add(a);
            }
            _pidKey = pidKey;
            foreach (var a in touched) a.Touched();
            return added;
        }

        public void Clear()
        {
            _apps.Clear();
            _pidKey.Clear();
            Apps.Clear();
            Samples = 0;
        }
    }

    // Parses the payload of Microsoft-Windows-Kernel-Network events (see NetTrace).
    public static class NetEvents
    {
        public struct Key : IEquatable<Key>
        {
            public int Pid, Ports;
            public ulong D1, D2, S1, S2;
            public bool Equals(Key o) => Pid == o.Pid && Ports == o.Ports && D1 == o.D1 && D2 == o.D2 && S1 == o.S1 && S2 == o.S2;
            public override bool Equals(object? o) => o is Key k && Equals(k);
            public override int GetHashCode() => unchecked(Pid * 397 ^ Ports ^ D1.GetHashCode() * 31 ^ D2.GetHashCode() ^ S1.GetHashCode() * 17 ^ S2.GetHashCode());
        }

        // Kernel-Network event ids (keywords 0x10 IPv4, 0x20 IPv6): TCP send/receive/connect/accept and UDP send/receive.
        // Their payload starts with PID (4), size (4), daddr, saddr, dport (2, network order), sport (2).
        public static int AddressLength(ushort id)
        {
            switch (id)
            {
                case 10: case 11: case 12: case 15: case 42: case 43: return 4;
                case 26: case 27: case 28: case 31: case 58: case 59: return 16;
                default: return 0;
            }
        }

        public static bool TryKey(ushort id, byte[] d, int len, out Key key)
        {
            key = default;
            int a = AddressLength(id);
            if (a == 0 || len < 12 + 2 * a) return false;
            key.Pid = BitConverter.ToInt32(d, 0);
            key.Ports = BitConverter.ToInt32(d, 8 + 2 * a);
            if (a == 4) { key.D1 = BitConverter.ToUInt32(d, 8); key.S1 = BitConverter.ToUInt32(d, 12); }
            else { key.D1 = BitConverter.ToUInt64(d, 8); key.D2 = BitConverter.ToUInt64(d, 16); key.S1 = BitConverter.ToUInt64(d, 24); key.S2 = BitConverter.ToUInt64(d, 32); }
            return true;
        }

        public static bool TryParse(ushort id, byte[] d, int len, ICollection<IPAddress> local, out int pid, out string remote)
        {
            pid = 0; remote = "";
            int a = AddressLength(id);
            if (a == 0 || len < 12 + 2 * a) return false;
            pid = BitConverter.ToInt32(d, 0);
            if (pid <= 0) return false;
            var dst = new IPAddress(d.Skip(8).Take(a).ToArray());
            var src = new IPAddress(d.Skip(8 + a).Take(a).ToArray());
            int dport = d[8 + 2 * a] << 8 | d[9 + 2 * a];
            int sport = d[10 + 2 * a] << 8 | d[11 + 2 * a];
            // daddr is the remote end; the check below only guards against an event that reports it the other way round.
            IPAddress ip = dst; int port = dport;
            if (local.Contains(dst) && !local.Contains(src)) { ip = src; port = sport; }
            if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any)) return false;
            if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
            remote = ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"[{ip}]:{port}" : $"{ip}:{port}";
            return true;
        }
    }
}
