using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;

namespace PcOptimizer.Core
{
    public class DupFile : INotifyPropertyChanged
    {
        public string Path { get; set; } = "";
        public long Size { get; set; }
        public DateTime Modified { get; set; }
        public DupGroup Group { get; set; } = null!;

        public string ModifiedText => Modified.ToString("yyyy-MM-dd HH:mm");

        bool _checked;
        public bool Checked
        {
            get => _checked;
            set { _checked = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Checked))); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    public class DupGroup
    {
        public string Md5 { get; set; } = "";
        public long Size { get; set; }
        public List<DupFile> Files { get; } = new List<DupFile>();

        public string Title => L.F("{0} 个相同文件 · 每个 {1}", Files.Count, FsUtil.HumanSize(Size));
        public string HashText => "MD5 " + Md5;
    }

    public class DedupProgress
    {
        public string Stage = "";
        public long Done, Total;
    }

    public static class Dedup
    {
        public static List<DupGroup> Find(IEnumerable<string> roots, long minSize, IProgress<DedupProgress>? progress, CancellationToken ct)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var files = new List<FileInfo>();
            var report = new DedupProgress { Stage = L.T("正在列出文件") };
            foreach (var root in roots)
            {
                foreach (var f in FsUtil.WalkFiles(root, ct))
                {
                    long len;
                    try { len = f.Length; } catch (Exception) { continue; }
                    if (len < minSize || len == 0) continue;
                    if (!seen.Add(f.FullName)) continue;
                    files.Add(f);
                    if (files.Count % 500 == 0) { report.Done = files.Count; progress?.Report(report); }
                }
            }

            var candidates = files.GroupBy(f => f.Length).Where(g => g.Count() > 1).SelectMany(g => g).ToList();

            var hp = new DedupProgress { Stage = L.T("正在计算 MD5"), Total = candidates.Sum(f => f.Length) };
            var hashed = new List<(FileInfo File, string Md5)>();
            foreach (var f in candidates)
            {
                ct.ThrowIfCancellationRequested();
                string? md5 = Md5Of(f.FullName, ct, n => { hp.Done += n; progress?.Report(hp); });
                if (md5 != null) hashed.Add((f, md5));
            }

            var groups = new List<DupGroup>();
            foreach (var g in hashed.GroupBy(h => (h.File.Length, h.Md5)).Where(g => g.Count() > 1))
            {
                var grp = new DupGroup { Md5 = g.Key.Md5, Size = g.Key.Length };
                foreach (var (file, _) in g.OrderBy(x => x.File.LastWriteTime).ThenBy(x => x.File.FullName.Length))
                    grp.Files.Add(new DupFile { Path = file.FullName, Size = file.Length, Modified = file.LastWriteTime, Group = grp });
                foreach (var f in grp.Files.Skip(1)) f.Checked = true;
                groups.Add(grp);
            }
            return groups.OrderByDescending(g => g.Size * (g.Files.Count - 1)).ToList();
        }

        public static string? Md5Of(string path, CancellationToken ct = default, Action<int>? onRead = null)
        {
            try
            {
                using (var md5 = MD5.Create())
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan))
                {
                    var buf = new byte[1 << 20];
                    int n;
                    while ((n = fs.Read(buf, 0, buf.Length)) > 0)
                    {
                        ct.ThrowIfCancellationRequested();
                        md5.TransformBlock(buf, 0, n, null, 0);
                        onRead?.Invoke(n);
                    }
                    md5.TransformFinalBlock(buf, 0, 0);
                    return BitConverter.ToString(md5.Hash).Replace("-", "").ToLowerInvariant();
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { return null; }
        }

        public static (int Removed, long Bytes, List<string> Failed) Delete(IEnumerable<DupGroup> groups)
        {
            int removed = 0;
            long bytes = 0;
            var failed = new List<string>();
            foreach (var g in groups)
            {
                var del = g.Files.Where(f => f.Checked).ToList();
                if (del.Count == 0) continue;
                var keep = g.Files.Where(f => !f.Checked).ToList();
                var keeper = keep.FirstOrDefault(k => Md5Of(k.Path) == g.Md5);
                if (keeper == null)
                {
                    failed.Add(L.F("{0}：这一组没有可保留的文件（全部勾选了，或保留的文件已变化），整组跳过。", System.IO.Path.GetFileName(g.Files[0].Path)));
                    continue;
                }
                foreach (var f in del)
                {
                    try
                    {
                        if (string.Equals(f.Path, keeper.Path, StringComparison.OrdinalIgnoreCase)) continue;
                        var info = new FileInfo(f.Path);
                        if (!info.Exists) { failed.Add(L.F("{0}：文件已不存在", f.Path)); continue; }
                        if (info.Length != f.Size || Md5Of(f.Path) != g.Md5) { failed.Add(L.F("{0}：扫描后被修改过，已跳过。", f.Path)); continue; }
                        RecycleBin.Send(f.Path);
                        removed++;
                        bytes += f.Size;
                        OpLog.Write("DEDUP_DELETE", $"{f.Path}\tmd5={g.Md5}\tkeep={keeper.Path}");
                    }
                    catch (Exception ex) { failed.Add(L.F("{0}：{1}", f.Path, ex.Message)); }
                }
            }
            return (removed, bytes, failed);
        }
    }
}
