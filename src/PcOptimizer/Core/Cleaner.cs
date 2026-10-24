using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace PcOptimizer.Core
{
    public class CleanTarget
    {
        public string Key { get; set; } = "";
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public string Icon { get; set; } = "\uE74D";
        public List<string> Roots { get; set; } = new List<string>();
        public string[] Patterns { get; set; } = { "*" };
        public double MinAgeHours { get; set; } = 24;
        public bool NeedsAdmin { get; set; }
        public bool DefaultOn { get; set; } = true;
    }

    public class FoundFile
    {
        public string TargetKey { get; set; } = "";
        public string Root { get; set; } = "";
        public string Path { get; set; } = "";
        public long Size { get; set; }
        public DateTime MtimeUtc { get; set; }
    }

    public class DeleteResult
    {
        public int Deleted;
        public long Freed;
        public List<KeyValuePair<string, string>> Skipped = new List<KeyValuePair<string, string>>();
    }

    public static class Cleaner
    {
        static string Env(string name, string fallback = "") =>
            Environment.GetEnvironmentVariable(name) is string v && v.Length > 0 ? v : fallback;

        static List<string> ChromiumCacheDirs(string userData)
        {
            var dirs = new List<string>();
            if (!Directory.Exists(userData)) return dirs;
            string[] profiles;
            try { profiles = Directory.GetDirectories(userData); }
            catch (Exception) { return dirs; }
            foreach (var p in profiles)
            {
                string name = System.IO.Path.GetFileName(p);
                if (name != "Default" && !name.StartsWith("Profile ")) continue;
                foreach (var sub in new[] { "Cache", "Code Cache", "GPUCache" })
                {
                    string d = System.IO.Path.Combine(p, sub);
                    if (Directory.Exists(d)) dirs.Add(d);
                }
            }
            return dirs;
        }

        public static List<CleanTarget> DefaultTargets()
        {
            string windir = Env("WINDIR", @"C:\Windows");
            string lad = Env("LOCALAPPDATA", Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
            string progData = Env("PROGRAMDATA", @"C:\ProgramData");
            var targets = new List<CleanTarget>
            {
                new CleanTarget { Key = "user_temp", Name = L.T("用户临时文件"), Icon = "\uE8B7",
                    Description = L.T("%TEMP% 目录，程序运行时留下的临时文件（只删 24 小时前的）。"),
                    Roots = { System.IO.Path.GetTempPath() } },
                new CleanTarget { Key = "win_temp", Name = L.T("系统临时文件"), Icon = "\uE770", NeedsAdmin = true,
                    Description = L.T(@"C:\Windows\Temp，系统和安装程序留下的临时文件。"),
                    Roots = { System.IO.Path.Combine(windir, "Temp") } },
                new CleanTarget { Key = "thumbcache", Name = L.T("缩略图缓存"), Icon = "\uE91B", MinAgeHours = 0,
                    Description = L.T("资源管理器的缩略图数据库，删除后会自动重建。"),
                    Roots = { System.IO.Path.Combine(lad, @"Microsoft\Windows\Explorer") },
                    Patterns = new[] { "thumbcache_*.db", "iconcache_*.db" } },
                new CleanTarget { Key = "wer", Name = L.T("系统错误报告"), Icon = "\uE7BA", MinAgeHours = 0,
                    Description = L.T("Windows 错误报告（WER）留下的崩溃报告。"),
                    Roots = { System.IO.Path.Combine(lad, @"Microsoft\Windows\WER"),
                              System.IO.Path.Combine(progData, @"Microsoft\Windows\WER") } },
                new CleanTarget { Key = "crashdumps", Name = L.T("程序崩溃转储"), Icon = "\uEBE8", MinAgeHours = 0,
                    Description = L.T("程序崩溃时生成的 .dmp 文件。"),
                    Roots = { System.IO.Path.Combine(lad, "CrashDumps") } },
                new CleanTarget { Key = "chrome_cache", Name = L.T("Chrome 浏览器缓存"), Icon = "\uE774", MinAgeHours = 0, DefaultOn = false,
                    Description = L.T("只清缓存，不动密码、书签、Cookie 和历史记录。建议先关闭浏览器。"),
                    Roots = ChromiumCacheDirs(System.IO.Path.Combine(lad, @"Google\Chrome\User Data")) },
                new CleanTarget { Key = "edge_cache", Name = L.T("Edge 浏览器缓存"), Icon = "\uE774", MinAgeHours = 0, DefaultOn = false,
                    Description = L.T("只清缓存，不动密码、书签、Cookie 和历史记录。建议先关闭浏览器。"),
                    Roots = ChromiumCacheDirs(System.IO.Path.Combine(lad, @"Microsoft\Edge\User Data")) },
                new CleanTarget { Key = "wu_download", Name = L.T("Windows 更新缓存"), Icon = "\uE895", MinAgeHours = 72,
                    NeedsAdmin = true, DefaultOn = false,
                    Description = L.T(@"已下载的更新安装包（SoftwareDistribution\Download）。正在更新时请勿清理。"),
                    Roots = { System.IO.Path.Combine(windir, @"SoftwareDistribution\Download") } },
            };
            foreach (var t in targets)
                t.Roots = t.Roots.Where(r => !string.IsNullOrEmpty(r) && Directory.Exists(r)).ToList();
            return targets;
        }

        public static List<FoundFile> Scan(IEnumerable<CleanTarget> targets,
            IProgress<string>? progress = null, CancellationToken ct = default)
        {
            var found = new List<FoundFile>();
            DateTime now = DateTime.UtcNow;
            foreach (var t in targets)
            {
                DateTime cutoff = now.AddHours(-t.MinAgeHours);
                foreach (var root in t.Roots)
                {
                    progress?.Report(L.F("正在扫描：{0}", t.Name));
                    foreach (var f in FsUtil.WalkFiles(root, ct))
                    {
                        long size; DateTime mtime;
                        try { size = f.Length; mtime = f.LastWriteTimeUtc; }
                        catch (Exception) { continue; }
                        if (mtime > cutoff) continue;
                        if (!t.Patterns.Any(p => FsUtil.WildcardMatch(f.Name, p))) continue;
                        found.Add(new FoundFile { TargetKey = t.Key, Root = root, Path = f.FullName, Size = size, MtimeUtc = mtime });
                    }
                }
            }
            return found;
        }

        public static DeleteResult Delete(IList<FoundFile> files,
            IProgress<string>? progress = null, CancellationToken ct = default)
        {
            var result = new DeleteResult();
            for (int i = 0; i < files.Count; i++)
            {
                if (ct.IsCancellationRequested) break;
                var f = files[i];
                if (i % 50 == 0) progress?.Report(L.F("正在清理 {0}/{1}", i, files.Count));
                string? reason = CheckBeforeDelete(f);
                if (reason != null) { result.Skipped.Add(new KeyValuePair<string, string>(f.Path, reason)); continue; }
                try
                {
                    var info = new FileInfo(f.Path);
                    if (info.IsReadOnly) info.IsReadOnly = false;
                    info.Delete();
                    result.Deleted++;
                    result.Freed += f.Size;
                    OpLog.Write("DELETE", $"{f.Size}\t{f.Path}");
                }
                catch (Exception e)
                {
                    result.Skipped.Add(new KeyValuePair<string, string>(f.Path, e is IOException ? L.T("文件正在使用") : e.Message));
                }
            }
            OpLog.Write("CLEAN_SUMMARY", $"删除 {result.Deleted} 个，释放 {result.Freed} 字节，跳过 {result.Skipped.Count} 个");
            foreach (var kv in result.Skipped) OpLog.Write("SKIP", $"{kv.Value}\t{kv.Key}");
            return result;
        }

        public static string? CheckBeforeDelete(FoundFile f)
        {
            if (!FsUtil.IsWithin(f.Path, f.Root)) return L.T("不在清理范围内");
            var info = new FileInfo(f.Path);
            try
            {
                if (!info.Exists) return L.T("文件已不存在");
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0) return L.T("是链接");
                if (info.Length != f.Size || Math.Abs((info.LastWriteTimeUtc - f.MtimeUtc).TotalSeconds) > 1)
                    return L.T("扫描后文件有变化");
            }
            catch (Exception e) { return e.Message; }
            return null;
        }
    }
}
