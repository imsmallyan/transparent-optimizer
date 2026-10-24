using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Net;

namespace PcOptimizer.Core
{
    public class PortInfo
    {
        public string Name = "";
        public string Note = "";
        public bool Risky;
    }

    public struct ListenSocket
    {
        public string Protocol;   // TCP / UDP
        public IPAddress Address;
        public int Port;
        public int Pid;
        public ListenSocket(string protocol, IPAddress address, int port, int pid) { Protocol = protocol; Address = address; Port = port; Pid = pid; }
    }

    public class ListenPort : INotifyPropertyChanged
    {
        public string Protocol { get; set; } = "TCP";
        public int Port { get; set; }
        public int Pid { get; set; }
        public string ProcessName { get; set; } = "";
        public string Path { get; set; } = "";
        public string Services { get; set; } = "";
        public bool IsSystem { get; set; }
        public List<IPAddress> Addresses { get; } = new List<IPAddress>();
        public PortInfo? Info { get; set; }

        public bool Exposed => Addresses.Any(a => !IPAddress.IsLoopback(a));
        public bool Risky => Exposed && Info?.Risky == true;
        public string Group => Exposed ? L.T("其他电脑能连上的端口") : L.T("只有本机能连的端口");

        public string PortText => Protocol + " " + Port;
        public string ServiceName => Info?.Name ?? L.T("未知用途");
        public string Note => Info?.Note ?? "";
        public bool HasNote => Note.Length > 0;

        public string Meta
        {
            get
            {
                string who = ProcessName + (Services.Length > 0 ? "（" + Services + "）" : "") + (Pid > 0 ? " · PID " + Pid : "");
                return who + L.T(" · 监听 ") + string.Join(L.T("、"), Addresses.Select(Ports.AddressText).Distinct());
            }
        }

        bool _blocked;
        public bool Blocked { get => _blocked; set { _blocked = value; Raise(nameof(Blocked)); Raise(nameof(CanBlock)); Raise(nameof(CanUnblock)); } }
        public bool CanControl => Path.Length > 0 && !IsSystem;
        public bool CanBlock => CanControl && !Blocked;
        public bool CanUnblock => CanControl && Blocked;

        public event PropertyChangedEventHandler? PropertyChanged;
        void Raise(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    public static class Ports
    {
        public static string AddressText(IPAddress a)
        {
            if (a.Equals(IPAddress.Any)) return L.T("所有 IPv4 地址");
            if (a.Equals(IPAddress.IPv6Any)) return L.T("所有 IPv6 地址");
            return a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? "[" + a + "]" : a.ToString();
        }

        public static List<ListenPort> Merge(IEnumerable<ListenSocket> sockets)
        {
            var map = new Dictionary<(string, int, int), ListenPort>();
            foreach (var s in sockets)
            {
                var key = (s.Protocol, s.Port, s.Pid);
                if (!map.TryGetValue(key, out var p))
                    map[key] = p = new ListenPort { Protocol = s.Protocol, Port = s.Port, Pid = s.Pid, Info = Describe(s.Protocol, s.Port) };
                if (!p.Addresses.Contains(s.Address)) p.Addresses.Add(s.Address);
            }
            return map.Values
                .OrderByDescending(p => p.Exposed)
                .ThenByDescending(p => p.Risky)
                .ThenBy(p => p.Protocol)
                .ThenBy(p => p.Port)
                .ToList();
        }

        static PortInfo I(string name, string note, bool risky = false) => new PortInfo { Name = name, Note = note, Risky = risky };

        public static PortInfo? Describe(string protocol, int port)
        {
            bool tcp = protocol == "TCP";
            if (tcp)
            {
                switch (port)
                {
                    case 21: return I(L.T("FTP 文件传输"), L.T("明文传输账号密码。不用的话建议关掉提供 FTP 的软件。"), true);
                    case 22: return I(L.T("SSH 远程登录"), L.T("别人知道账号密码就能登录这台电脑。不用的话可以在“可选功能”里卸载 OpenSSH 服务器。"), true);
                    case 23: return I(L.T("Telnet 远程登录"), L.T("明文传输，已经很少用。不用的话建议关掉。"), true);
                    case 80: case 8080: case 8000: case 8888: return I(L.T("网页服务"), L.T("有程序在这台电脑上提供网页，通常是开发工具、NAS 客户端或打印机管理之类。"));
                    case 443: case 8443: return I(L.T("加密网页服务（HTTPS）"), L.T("有程序在这台电脑上提供加密网页。"));
                    case 135: return I(L.T("Windows 远程调用（RPC）"), L.T("Windows 自带的系统服务，很多功能依赖它，不要关。"));
                    case 139: return I(L.T("旧版文件共享（NetBIOS）"), L.T("早期 Windows 的文件共享方式。不需要共享文件的话，可以在网卡属性里关掉“NetBIOS over TCP/IP”。"), true);
                    case 445: return I(L.T("文件和打印机共享（SMB）"), L.T("局域网共享文件夹和打印机用的。在公共网络（咖啡店、酒店）下，Windows 防火墙默认会挡住它。"), true);
                    case 2869: return I(L.T("网络发现（UPnP）"), L.T("Windows 用来发现局域网里的设备，在专用网络下才会开放。"));
                    case 3389: return I(L.T("远程桌面"), L.T("别人知道账号密码就能远程控制这台电脑。不用的话在“设置 → 系统 → 远程桌面”里关掉。"), true);
                    case 5040: return I(L.T("Windows 设备连接服务"), L.T("Windows 自带服务，用于手机连接、跨设备共享等。"));
                    case 5357: return I(L.T("网络发现（WSD）"), L.T("Windows 用来让局域网里的电脑和打印机互相发现。"));
                    case 5900: case 5800: return I(L.T("VNC 远程控制"), L.T("别人连上就能看到并控制屏幕。不用的话建议关掉 VNC 软件。"), true);
                    case 5985: case 5986: return I(L.T("PowerShell 远程管理（WinRM）"), L.T("管理员可以远程在这台电脑上执行命令。家用电脑一般不需要。"), true);
                    case 7680: return I(L.T("传递优化"), L.T("Windows 更新从局域网其他电脑下载更新时用的。可以在“设置 → Windows 更新 → 高级选项 → 传递优化”里关掉。"));
                    case 1433: return I(L.T("SQL Server 数据库"), L.T("数据库对外开放时，请确认设置了强密码。"), true);
                    case 3306: return I(L.T("MySQL 数据库"), L.T("数据库对外开放时，请确认设置了强密码。"), true);
                    case 5432: return I(L.T("PostgreSQL 数据库"), L.T("数据库对外开放时，请确认设置了强密码。"), true);
                    case 6379: return I(L.T("Redis 数据库"), L.T("Redis 默认没有密码，对外开放很危险。"), true);
                    case 27017: return I(L.T("MongoDB 数据库"), L.T("数据库对外开放时，请确认开启了登录验证。"), true);
                }
                if (port >= 49664 && port <= 49690) return I(L.T("Windows 系统服务（动态端口）"), L.T("Windows 系统服务通过远程调用（RPC）使用的端口，一般不用管。"));
            }
            else
            {
                switch (port)
                {
                    case 53: return I(L.T("域名解析（DNS）"), L.T("有程序在这台电脑上提供域名解析。"));
                    case 67: case 68: return I(L.T("自动获取 IP 地址（DHCP）"), L.T("连网时获取 IP 地址用的。"));
                    case 123: return I(L.T("时间同步（NTP）"), L.T("Windows 用来同步时钟。"));
                    case 137: case 138: return I(L.T("旧版文件共享（NetBIOS）"), L.T("早期 Windows 的电脑名查找和共享方式。不需要共享文件的话，可以在网卡属性里关掉“NetBIOS over TCP/IP”。"), true);
                    case 500: case 4500: return I(L.T("VPN（IPsec）"), L.T("系统的 VPN 和 IPsec 服务使用的端口。"));
                    case 1900: return I(L.T("局域网设备发现（SSDP / UPnP）"), L.T("用来发现局域网里的电视、音箱、路由器等设备。"));
                    case 3702: return I(L.T("网络发现（WS-Discovery）"), L.T("Windows 用来让局域网里的电脑和打印机互相发现。"));
                    case 5050: return I(L.T("Windows 设备连接服务"), L.T("Windows 自带服务，用于手机连接、跨设备共享等。"));
                    case 5353: return I(L.T("局域网名称发现（mDNS）"), L.T("用来发现局域网里的打印机、投屏设备等，浏览器和系统都会用。"));
                    case 5355: return I(L.T("局域网名称解析（LLMNR）"), L.T("Windows 在没有 DNS 时查找局域网电脑名用的。"));
                }
            }
            return null;
        }
    }
}
