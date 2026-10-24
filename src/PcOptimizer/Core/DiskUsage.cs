using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace PcOptimizer.Core
{
    public class DirSize
    {
        public string Path { get; set; } = "";
        public string Name { get; set; } = "";
        public long Size { get; set; }
        public int Files { get; set; }
        public bool IsLooseFiles { get; set; }
        public double Percent { get; set; }
        public string SizeText => FsUtil.HumanSize(Size);
        public string PercentText => $"{Percent:0.0}%";

        SpaceHint? _hint; bool _hinted;
        public SpaceHint? Hint { get { if (!_hinted && !IsLooseFiles) { _hint = SpaceHints.Explain(Path, true); _hinted = true; } return _hint; } }
        public string HintText => Hint?.Text ?? "";
    }

    public class BigFile
    {
        public string Path { get; set; } = "";
        public long Size { get; set; }
        public string SizeText => FsUtil.HumanSize(Size);

        SpaceHint? _hint; bool _hinted;
        public SpaceHint? Hint { get { if (!_hinted) { _hint = SpaceHints.Explain(Path, false); _hinted = true; } return _hint; } }
        public string HintText => Hint?.Text ?? "";
        public bool HintKeep => Hint != null && Hint.Kind != HintKind.CanDelete;
        public bool HintDelete => Hint?.Kind == HintKind.CanDelete;
        public bool CanRecycle => SpaceHints.RecycleBlocked(Path) == null;
    }

    public class DiskReport
    {
        public List<DirSize> Children = new List<DirSize>();
        public List<BigFile> Biggest = new List<BigFile>();
        public long Total;
    }

    public static class DiskUsage
    {
        public static DiskReport Analyze(string root, int topN = 50,
            IProgress<string>? progress = null, CancellationToken ct = default)
        {
            var report = new DiskReport();
            var loose = new DirSize { Path = root, Name = L.T("（此目录下的文件）"), IsLooseFiles = true };
            var biggest = new SortedSet<(long, string)>();

            void Track(long size, string path)
            {
                biggest.Add((size, path));
                if (biggest.Count > topN) biggest.Remove(biggest.Min);
            }

            FileSystemInfo[] entries;
            try { entries = new DirectoryInfo(root).GetFileSystemInfos(); }
            catch (Exception) { return report; }

            foreach (var e in entries.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (ct.IsCancellationRequested) break;
                if (FsUtil.IsLinkLike(e)) continue;
                try
                {
                    if (e is DirectoryInfo d)
                    {
                        progress?.Report(L.F("正在统计：{0}", d.FullName));
                        var ds = new DirSize { Path = d.FullName, Name = d.Name };
                        foreach (var f in FsUtil.WalkFiles(d.FullName, ct))
                        {
                            long len;
                            try { len = f.Length; } catch (Exception) { continue; }
                            ds.Size += len; ds.Files++;
                            Track(len, f.FullName);
                        }
                        report.Children.Add(ds);
                    }
                    else if (e is FileInfo fi)
                    {
                        loose.Size += fi.Length; loose.Files++;
                        Track(fi.Length, fi.FullName);
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception) { }
            }
            if (loose.Files > 0) report.Children.Add(loose);
            report.Total = report.Children.Sum(c => c.Size);
            foreach (var c in report.Children)
                c.Percent = report.Total > 0 ? 100.0 * c.Size / report.Total : 0;
            report.Children = report.Children.OrderByDescending(c => c.Size).ToList();
            report.Biggest = biggest.Reverse().Select(b => new BigFile { Size = b.Item1, Path = b.Item2 }).ToList();
            return report;
        }
    }
}
