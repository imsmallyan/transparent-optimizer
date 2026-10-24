using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management;
using System.Security.Principal;
using Microsoft.Win32;

namespace PcOptimizer.Core
{
    public class InfoRow
    {
        public string Icon { get; set; } = "";
        public string Title { get; set; } = "";
        public string Value { get; set; } = "";
    }

    public class DriveRow
    {
        public string Name { get; set; } = "";
        public long Total { get; set; }
        public long Free { get; set; }
        public double UsedPercent => Total > 0 ? 100.0 * (Total - Free) / Total : 0;
        public string Text => L.F("可用 {0} / 共 {1}", FsUtil.HumanSize(Free), FsUtil.HumanSize(Total));
        public bool Low => Total > 0 && (double)Free / Total < 0.1;
    }

    public static class SysInfo
    {
        public static bool IsAdmin()
        {
            try
            {
                using (var id = WindowsIdentity.GetCurrent())
                    return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch (Exception) { return false; }
        }

        static string Reg(string path, string name)
        {
            try
            {
                using (var k = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey(path))
                    return k?.GetValue(name)?.ToString()?.Trim() ?? "";
            }
            catch (Exception) { return ""; }
        }

        public static string WindowsVersion()
        {
            const string p = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";
            string name = Reg(p, "ProductName");
            string build = Reg(p, "CurrentBuildNumber");
            if (int.TryParse(build, out int b) && b >= 22000) name = name.Replace("Windows 10", "Windows 11");
            string disp = Reg(p, "DisplayVersion");
            if (disp == "") disp = Reg(p, "ReleaseId");
            string ubr = Reg(p, "UBR");
            return $"{name} {disp}".Trim() + L.F("（内部版本 {0}{1}）", build, (ubr != "" ? "." + ubr : ""));
        }

        static string Wmi(string cls, string prop)
        {
            try
            {
                using (var s = new ManagementObjectSearcher($"SELECT {prop} FROM {cls}"))
                    return string.Join(L.T("；"), s.Get().Cast<ManagementObject>()
                        .Select(o => o[prop]?.ToString()?.Trim()).Where(v => !string.IsNullOrEmpty(v)));
            }
            catch (Exception) { return ""; }
        }

        public static (ulong Total, ulong Avail)? Memory()
        {
            var m = new NativeMethods.MEMORYSTATUSEX { dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MEMORYSTATUSEX>() };
            return NativeMethods.GlobalMemoryStatusEx(ref m) ? (m.ullTotalPhys, m.ullAvailPhys) : ((ulong, ulong)?)null;
        }

        public static TimeSpan Uptime() => TimeSpan.FromMilliseconds(NativeMethods.GetTickCount64());

        public static List<DriveRow> Drives()
        {
            var list = new List<DriveRow>();
            foreach (var d in DriveInfo.GetDrives())
            {
                try
                {
                    if (!d.IsReady || d.DriveType != DriveType.Fixed) continue;
                    list.Add(new DriveRow { Name = d.Name, Total = d.TotalSize, Free = d.AvailableFreeSpace });
                }
                catch (Exception) { }
            }
            return list;
        }

        public static List<InfoRow> Collect()
        {
            var rows = new List<InfoRow>
            {
                new InfoRow { Icon = "", Title = L.T("操作系统"), Value = WindowsVersion() },
                new InfoRow { Icon = "", Title = L.T("计算机名"), Value = Environment.MachineName },
                new InfoRow { Icon = "", Title = L.T("处理器"), Value = Reg(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString") + L.F("（{0} 线程）", Environment.ProcessorCount) },
            };
            var mem = Memory();
            if (mem.HasValue)
            {
                var (total, avail) = mem.Value;
                rows.Add(new InfoRow { Icon = "", Title = L.T("内存"),
                    Value = L.F("共 {0}，可用 {1}（已用 {2:0}%）", FsUtil.HumanSize((long)total), FsUtil.HumanSize((long)avail), 100.0 * (total - avail) / total) });
            }
            string gpu = Wmi("Win32_VideoController", "Name");
            if (gpu != "") rows.Add(new InfoRow { Icon = "", Title = L.T("显卡"), Value = gpu });
            string board = (Wmi("Win32_BaseBoard", "Manufacturer") + " " + Wmi("Win32_BaseBoard", "Product")).Trim();
            if (board != "") rows.Add(new InfoRow { Icon = "", Title = L.T("主板"), Value = board });
            var up = Uptime();
            rows.Add(new InfoRow { Icon = "", Title = L.T("已开机"), Value = L.F("{0} 天 {1} 小时 {2} 分", up.Days, up.Hours, up.Minutes) });
            rows.Add(new InfoRow { Icon = "", Title = L.T("管理员权限"), Value = IsAdmin() ? L.T("是") : L.T("否（清理系统临时文件、修改“所有用户”启动项需要管理员）") });
            return rows;
        }
    }
}
