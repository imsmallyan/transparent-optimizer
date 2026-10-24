using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace PcOptimizer.Core
{
    public enum HealthLevel { Good, Warn, Bad, Unknown }

    public class VolumeInfo
    {
        public string Letter = "";       // "C"
        public string Label = "";
        public string Format = "";       // NTFS / FAT32 / exFAT …
        public long Total, Free;
        public string Text => $"{Letter}:" + (Label.Length > 0 ? " " + Label : "") +
                              (Format.Length > 0 ? " · " + Format : "") +
                              (Total > 0 ? " · " + L.F("{0} 可用，共 {1}", FsUtil.HumanSize(Free), FsUtil.HumanSize(Total)) : "");
    }

    public class PhysicalDiskInfo
    {
        public string Number = "";
        public string Model = "";
        public string MediaType = "";      // SSD / HDD / Unspecified
        public string BusType = "";        // NVMe / SATA / USB …
        public long Size;
        public string Health = "";         // Healthy / Warning / Unhealthy
        public string Operational = "";    // OK / Degraded …
        public string Firmware = "";
        public string PartitionStyle = ""; // GPT / MBR
        public long? PowerOnHours;
        public int? Temperature, TemperatureMax;
        public int? Wear;
        public long? ReadErrors, WriteErrors, ReadUncorrected, WriteUncorrected;
        public long? StartStopCycles;
        public bool CountersRead;
        public List<VolumeInfo> Volumes = new List<VolumeInfo>();

        public HealthLevel Level => DiskHealth.Assess(this).Level;
        public IList<string> Reasons => DiskHealth.Assess(this).Reasons;
        public string Title => Model.Length > 0 ? Model : L.F("硬盘 {0}", Number);
        public string LevelText => Level == HealthLevel.Good ? L.T("良好") : Level == HealthLevel.Warn ? L.T("需留意")
                                 : Level == HealthLevel.Bad ? L.T("有问题") : L.T("未知");
        public bool IsGood => Level == HealthLevel.Good;
        public bool IsWarn => Level == HealthLevel.Warn || Level == HealthLevel.Unknown;
        public bool IsBad => Level == HealthLevel.Bad;
        public string ReasonText => string.Join("\n", Reasons);
        public bool HasReasons => Reasons.Count > 0;

        public string Summary
        {
            get
            {
                var parts = new List<string>();
                string type = DiskHealth.MediaText(MediaType);
                if (type.Length > 0) parts.Add(type);
                if (BusType.Length > 0 && BusType != "Unknown") parts.Add(BusType);
                if (Size > 0) parts.Add(FsUtil.HumanSize(Size));
                if (PartitionStyle.Length > 0 && PartitionStyle != "RAW") parts.Add(PartitionStyle);
                if (Firmware.Length > 0) parts.Add(L.F("固件 {0}", Firmware));
                return string.Join(" · ", parts);
            }
        }

        public List<(string Name, string Value)> Facts
        {
            get
            {
                var f = new List<(string, string)>();
                if (PowerOnHours.HasValue) f.Add((L.T("通电时间"), DiskHealth.HoursText(PowerOnHours.Value)));
                if (StartStopCycles.HasValue && StartStopCycles > 0) f.Add((L.T("开关机次数"), StartStopCycles.Value.ToString("N0", CultureInfo.InvariantCulture)));
                if (Temperature.HasValue && Temperature > 0)
                    f.Add((L.T("温度"), Temperature + " °C" + (TemperatureMax.HasValue && TemperatureMax > 0 ? L.F("（上限 {0} °C）", TemperatureMax) : "")));
                if (Wear.HasValue) f.Add((L.T("已用寿命"), Wear + "%"));
                if (ReadErrors.HasValue || WriteErrors.HasValue)
                    f.Add((L.T("读写错误"), L.F("读 {0} · 写 {1}", ReadErrors ?? 0, WriteErrors ?? 0)));
                return f;
            }
        }
        public string FactsText => Facts.Count == 0
            ? (CountersRead ? L.T("这块硬盘没有提供通电时间、温度等记录。") : L.T("通电时间、温度等记录需要管理员权限才能读取。"))
            : string.Join("\n", Facts.Select(x => x.Name + L.T("：") + x.Value));
        public string VolumesText => Volumes.Count == 0 ? L.T("没有分配盘符的分区") : string.Join("\n", Volumes.Select(v => v.Text));
    }

    public static class DiskHealth
    {
        const string Script =
            "$ErrorActionPreference='SilentlyContinue'; " +
            "Get-PhysicalDisk | Sort-Object { [int]$_.DeviceId } | ForEach-Object { $d=$_; " +
            "$r = $d | Get-StorageReliabilityCounter; " +
            "$k = Get-Disk -Number $d.DeviceId; " +
            "$p = (Get-Partition -DiskNumber $d.DeviceId | Where-Object { $_.DriveLetter } | ForEach-Object { $_.DriveLetter }) -join ''; " +
            "'DISK' + \"`t\" + (@($d.DeviceId, $d.FriendlyName, $d.MediaType, $d.BusType, $d.Size, $d.HealthStatus, ($d.OperationalStatus -join ','), " +
            "$d.FirmwareVersion, $k.PartitionStyle, [bool]$r, $r.PowerOnHours, $r.Temperature, $r.TemperatureMax, $r.Wear, " +
            "$r.ReadErrorsTotal, $r.WriteErrorsTotal, $r.ReadErrorsUncorrected, $r.WriteErrorsUncorrected, $r.StartStopCycleCount, $p) -join \"`t\") }";

        public static List<PhysicalDiskInfo> List()
        {
            string? output = PowerShell.Run(Script, out _, 60000);
            var disks = Parse(output ?? "");
            var volumes = Volumes();
            foreach (var d in disks)
                d.Volumes = d.Volumes.Select(v => volumes.FirstOrDefault(x => x.Letter == v.Letter) ?? v).ToList();
            return disks;
        }

        static List<VolumeInfo> Volumes()
        {
            var list = new List<VolumeInfo>();
            foreach (var d in DriveInfo.GetDrives())
            {
                try
                {
                    if (!d.IsReady) continue;
                    list.Add(new VolumeInfo
                    {
                        Letter = d.Name.Substring(0, 1).ToUpperInvariant(), Label = d.VolumeLabel == d.Name ? "" : d.VolumeLabel ?? "",
                        Format = d.DriveFormat ?? "", Total = d.TotalSize, Free = d.TotalFreeSpace,
                    });
                }
                catch (Exception) { }
            }
            return list;
        }

        static long? Long(string s) => long.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long v) ? v : (long?)null;
        static int? Int(string s) => int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : (int?)null;

        public static List<PhysicalDiskInfo> Parse(string output)
        {
            var list = new List<PhysicalDiskInfo>();
            foreach (var raw in output.Split('\n'))
            {
                var f = raw.TrimEnd('\r').Split('\t');
                if (f.Length < 21 || f[0] != "DISK") continue;
                var d = new PhysicalDiskInfo
                {
                    Number = f[1].Trim(), Model = f[2].Trim(), MediaType = f[3].Trim(), BusType = f[4].Trim(),
                    Size = Long(f[5]) ?? 0, Health = f[6].Trim(), Operational = f[7].Trim(), Firmware = f[8].Trim(),
                    PartitionStyle = f[9].Trim(),
                    CountersRead = f[10].Trim().Equals("True", StringComparison.OrdinalIgnoreCase),
                    PowerOnHours = Long(f[11]), Temperature = Int(f[12]), TemperatureMax = Int(f[13]), Wear = Int(f[14]),
                    ReadErrors = Long(f[15]), WriteErrors = Long(f[16]), ReadUncorrected = Long(f[17]), WriteUncorrected = Long(f[18]),
                    StartStopCycles = Long(f[19]),
                };
                foreach (char c in f[20].Trim()) d.Volumes.Add(new VolumeInfo { Letter = char.ToUpperInvariant(c).ToString() });
                list.Add(d);
            }
            return list;
        }

        public static string MediaText(string media) =>
            media.Equals("SSD", StringComparison.OrdinalIgnoreCase) ? L.T("固态硬盘（SSD）")
            : media.Equals("HDD", StringComparison.OrdinalIgnoreCase) ? L.T("机械硬盘（HDD）")
            : media.Equals("SCM", StringComparison.OrdinalIgnoreCase) ? "SCM" : "";

        public static string HoursText(long hours)
        {
            string h = L.F("{0} 小时", hours.ToString("N0", CultureInfo.InvariantCulture));
            if (hours < 24 * 30) return h;
            double years = hours / 24.0 / 365;
            return years >= 1 ? h + L.F("（约 {0:0.0} 年）", years) : h + L.F("（约 {0} 个月）", (int)(hours / 24 / 30));
        }

        public static (HealthLevel Level, List<string> Reasons) Assess(PhysicalDiskInfo d)
        {
            var reasons = new List<string>();
            var level = HealthLevel.Good;
            int Rank(HealthLevel l) => l == HealthLevel.Good ? 0 : l == HealthLevel.Unknown ? 1 : l == HealthLevel.Warn ? 2 : 3;
            void Raise(HealthLevel l, string why) { if (Rank(l) > Rank(level)) level = l; reasons.Add(why); }

            if (d.Health.Equals("Unhealthy", StringComparison.OrdinalIgnoreCase))
                Raise(HealthLevel.Bad, L.T("Windows 报告这块硬盘“不正常”。请尽快备份重要文件。"));
            else if (d.Health.Equals("Warning", StringComparison.OrdinalIgnoreCase))
                Raise(HealthLevel.Warn, L.T("Windows 报告这块硬盘有警告。建议备份重要文件。"));
            else if (!d.Health.Equals("Healthy", StringComparison.OrdinalIgnoreCase))
                level = HealthLevel.Unknown;

            if (d.Operational.Length > 0 && !d.Operational.Split(',').All(s => s.Trim().Equals("OK", StringComparison.OrdinalIgnoreCase) || s.Trim().Length == 0))
                Raise(HealthLevel.Warn, L.F("Windows 报告的运行状态是“{0}”。", d.Operational));
            if (d.Wear >= 100) Raise(HealthLevel.Bad, L.T("SSD 的设计寿命已经用完。请尽快备份重要文件，并考虑更换。"));
            else if (d.Wear >= 90) Raise(HealthLevel.Warn, L.F("SSD 的设计寿命已经用掉 {0}%。建议备份重要文件，并考虑更换。", d.Wear));
            if ((d.ReadUncorrected ?? 0) > 0 || (d.WriteUncorrected ?? 0) > 0)
                Raise(HealthLevel.Warn, L.T("出现过无法纠正的读写错误，可能有数据读不出来。建议备份重要文件。"));
            if (d.Temperature > 0 && d.TemperatureMax > 0 && d.Temperature >= d.TemperatureMax)
                Raise(HealthLevel.Warn, L.T("温度已经到了硬盘自己报告的上限，请检查散热。"));
            if (level == HealthLevel.Unknown && reasons.Count == 0)
                reasons.Add(L.T("这块硬盘没有向 Windows 报告健康状态（常见于 U 盘、移动硬盘和部分 RAID）。"));
            return (level, reasons);
        }
    }
}
