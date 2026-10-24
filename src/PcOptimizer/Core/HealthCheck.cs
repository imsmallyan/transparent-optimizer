using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace PcOptimizer.Core
{
    public enum CheckLevel { Good, Warn, Bad }

    public class CheckResult
    {
        public string Icon { get; set; } = "";
        public string Title { get; set; } = "";
        public string Detail { get; set; } = "";
        public CheckLevel Level { get; set; }
        public int Penalty { get; set; }
        public string ActionText { get; set; } = "";
        public string ActionPage { get; set; } = "";
    }

    public class HealthReport
    {
        public int Score;
        public List<CheckResult> Items = new List<CheckResult>();
        public List<FoundFile> JunkFiles = new List<FoundFile>();
        public List<CleanTarget> JunkTargets = new List<CleanTarget>();
    }

    public static class HealthCheck
    {
        public static HealthReport Run(IProgress<string>? progress = null, CancellationToken ct = default)
        {
            var r = new HealthReport();
            bool admin = SysInfo.IsAdmin();

            progress?.Report(L.T("正在检查垃圾文件…"));
            r.JunkTargets = Cleaner.DefaultTargets().Where(t => t.DefaultOn && (admin || !t.NeedsAdmin) && t.Roots.Count > 0).ToList();
            r.JunkFiles = Cleaner.Scan(r.JunkTargets, progress, ct);
            long junk = r.JunkFiles.Sum(f => f.Size);
            r.Items.Add(Grade("", L.T("垃圾文件"), junk, 200L << 20, 1L << 30,
                L.F("发现 {0} 可清理文件（{1} 个）", FsUtil.HumanSize(junk), r.JunkFiles.Count), L.T("垃圾文件很少"), 25, L.T("去清理"), "clean"));

            progress?.Report(L.T("正在检查回收站…"));
            var rb = RecycleBin.Query();
            if (rb.HasValue)
                r.Items.Add(Grade("", L.T("回收站"), rb.Value.Bytes, 500L << 20, 5L << 30,
                    L.F("回收站里有 {0} 个项目，占用 {1}", rb.Value.Count, FsUtil.HumanSize(rb.Value.Bytes)), L.T("回收站基本是空的"), 10, L.T("去清理"), "clean"));

            progress?.Report(L.T("正在检查开机启动项…"));
            int enabled = 0, brokenStartup = 0;
            try
            {
                var startupItems = StartupManager.ListItems();
                enabled = startupItems.Count(i => i.Enabled);
                brokenStartup = startupItems.Count(i => i.TargetMissing);
            }
            catch (Exception) { }
            r.Items.Add(Grade("", L.T("开机启动项"), enabled, 8, 15,
                L.F("有 {0} 个程序随开机启动，可能拖慢开机", enabled), L.F("有 {0} 个启动项，数量正常", enabled), 20, L.T("去管理"), "startup"));

            progress?.Report(L.T("正在检查软件残留…"));
            int brokenEntries = 0;
            try { brokenEntries = Programs.List().Count(p => p.IsBroken); } catch (Exception) { }
            int broken = brokenStartup + brokenEntries;
            r.Items.Add(new CheckResult
            {
                Icon = "\uE71D", Title = L.T("软件残留"),
                Level = broken > 0 ? CheckLevel.Warn : CheckLevel.Good, Penalty = broken > 0 ? 10 : 0,
                Detail = broken == 0 ? L.T("没有发现失效的启动项或卸载记录")
                       : string.Join(L.T("，"), new[] { brokenStartup > 0 ? L.F("{0} 个启动项指向的程序已不存在", brokenStartup) : "",
                                                  brokenEntries > 0 ? L.F("{0} 条卸载记录已失效", brokenEntries) : "" }.Where(s => s != "")),
                ActionText = broken > 0 ? L.T("去清理") : "", ActionPage = brokenStartup > 0 ? "startup" : "uninstall",
            });

            progress?.Report(L.T("正在检查磁盘空间…"));
            string sysDrive = (Environment.GetEnvironmentVariable("SystemDrive") ?? "C:") + "\\";
            var drive = SysInfo.Drives().FirstOrDefault(d => d.Name.Equals(sysDrive, StringComparison.OrdinalIgnoreCase));
            if (drive != null)
            {
                double freePct = drive.Total > 0 ? 100.0 * drive.Free / drive.Total : 100;
                var level = freePct < 10 ? CheckLevel.Bad : freePct < 20 ? CheckLevel.Warn : CheckLevel.Good;
                r.Items.Add(new CheckResult
                {
                    Icon = "", Title = L.T("系统盘空间"), Level = level,
                    Penalty = level == CheckLevel.Bad ? 25 : level == CheckLevel.Warn ? 10 : 0,
                    Detail = L.F("{0} 剩余 {1}（{2:0}%）", sysDrive, FsUtil.HumanSize(drive.Free), freePct) + (level == CheckLevel.Good ? L.T("，空间充足") : L.T("，空间偏紧")),
                    ActionText = level == CheckLevel.Good ? "" : L.T("去分析"), ActionPage = "disk",
                });
            }

            var up = SysInfo.Uptime();
            r.Items.Add(Grade("", L.T("连续运行时间"), (long)up.TotalDays, 7, 30,
                L.F("已连续运行 {0} 天，建议找时间重启一次", up.Days), L.F("已运行 {0} 天 {1} 小时", up.Days, up.Hours), 10, "", ""));

            progress?.Report(L.T("正在检查病毒防护…"));
            r.Items.Add(AntivirusCheck(Defender.Status()));

            progress?.Report(L.T("正在检查隐私设置…"));
            try
            {
                int left = Privacy.List().Count(i => i.Recommended && i.State != PrivacyState.Off);
                r.Items.Add(new CheckResult
                {
                    Icon = "\uE72E", Title = L.T("隐私与广告"),
                    Level = left > 0 ? CheckLevel.Warn : CheckLevel.Good, Penalty = left > 0 ? 5 : 0,
                    Detail = left > 0 ? L.F("还有 {0} 项 Windows 广告或数据收集没有关闭", left) : L.T("推荐关闭的广告和数据收集都已关闭"),
                    ActionText = left > 0 ? L.T("去关闭") : "", ActionPage = "privacy",
                });
            }
            catch (Exception) { }

            foreach (var i in r.Items)
                if (i.Detail.Length > 0 && ".。!！?？".IndexOf(i.Detail[i.Detail.Length - 1]) < 0) i.Detail += L.T("。");
            r.Score = Math.Max(0, 100 - r.Items.Sum(i => i.Penalty));
            return r;
        }

        static CheckResult AntivirusCheck(DefenderStatus s)
        {
            var c = new CheckResult { Icon = "\uEA18", Title = L.T("病毒防护"), ActionPage = "more:defender" };
            bool defenderActive = s.Available && s.AntivirusOn && !s.Passive;
            if (!defenderActive && s.OtherAntivirus.Count > 0)
            { c.Level = CheckLevel.Good; c.Detail = L.F("由 {0} 负责", string.Join(L.T("、"), s.OtherAntivirus)); }
            else if (!defenderActive)
            { c.Level = CheckLevel.Bad; c.Penalty = 20; c.Detail = L.T("没有检测到正在工作的杀毒软件"); c.ActionText = L.T("去查看"); }
            else if (!s.RealTimeOn)
            { c.Level = CheckLevel.Bad; c.Penalty = 20; c.Detail = L.T("Defender 的实时保护已关闭"); c.ActionText = L.T("去查看"); }
            else if (s.SignatureAgeDays >= 7)
            { c.Level = CheckLevel.Warn; c.Penalty = 10; c.Detail = L.F("病毒库已经 {0} 天没更新", s.SignatureAgeDays); c.ActionText = L.T("去更新"); }
            else
            { c.Level = CheckLevel.Good; c.Detail = L.T("Defender 实时保护已开启，病毒库是新的"); }
            return c;
        }

        static CheckResult Grade(string icon, string title, long value, long warnAt, long badAt,
            string problemText, string okText, int maxPenalty, string action, string page)
        {
            var level = value >= badAt ? CheckLevel.Bad : value >= warnAt ? CheckLevel.Warn : CheckLevel.Good;
            return new CheckResult
            {
                Icon = icon, Title = title, Level = level,
                Detail = level == CheckLevel.Good ? okText : problemText,
                Penalty = level == CheckLevel.Bad ? maxPenalty : level == CheckLevel.Warn ? maxPenalty / 2 : 0,
                ActionText = level == CheckLevel.Good ? "" : action,
                ActionPage = page,
            };
        }
    }
}
