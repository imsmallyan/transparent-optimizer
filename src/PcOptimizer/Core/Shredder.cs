using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;

namespace PcOptimizer.Core
{
    public class ShredResult
    {
        public int Files;
        public long Bytes;
        public List<string> Failed = new List<string>();
    }

    public static class Shredder
    {
        public static string? CheckAllowed(string path)
        {
            string full;
            try { full = Path.GetFullPath(path).TrimEnd('\\', '/'); }
            catch (Exception) { return L.T("路径无效"); }
            if (full.Length <= 3 || Path.GetPathRoot(full)?.TrimEnd('\\', '/') == full) return L.T("不能粉碎整个磁盘");

            var sf = new[]
            {
                Environment.SpecialFolder.Windows, Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86,
                Environment.SpecialFolder.CommonApplicationData,
            };
            foreach (var f in sf)
            {
                string d = Environment.GetFolderPath(f);
                if (d.Length == 0) continue;
                if (FsUtil.IsWithin(full, d)) return L.T("系统或软件目录里的文件不能粉碎");
                if (FsUtil.IsWithin(d, full)) return L.T("这个文件夹里包含系统目录");
            }
            var personal = new[]
            {
                Environment.SpecialFolder.UserProfile, Environment.SpecialFolder.Desktop, Environment.SpecialFolder.MyDocuments,
                Environment.SpecialFolder.MyPictures, Environment.SpecialFolder.MyMusic, Environment.SpecialFolder.MyVideos,
                Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.LocalApplicationData,
            };
            foreach (var f in personal)
            {
                string d = Environment.GetFolderPath(f);
                if (d.Length > 0 && FsUtil.IsWithin(d, full)) return L.T("不能整个粉碎用户文件夹（可以粉碎里面的文件）");
            }
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (appData.Length > 0 && FsUtil.IsWithin(full, appData) ||
                FsUtil.IsWithin(full, Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)))
                return L.T("AppData 里是软件的数据，不能粉碎。");
            if (FsUtil.IsWithin(AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\', '/'), full)) return L.T("不能粉碎本工具自己所在的文件夹");
            return null;
        }

        public static List<FileInfo> Expand(IEnumerable<string> items)
        {
            var list = new List<FileInfo>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in items)
            {
                if (File.Exists(p)) { if (seen.Add(p)) list.Add(new FileInfo(p)); }
                else if (Directory.Exists(p) && !FsUtil.IsLinkLike(new DirectoryInfo(p)))
                    foreach (var f in FsUtil.WalkFiles(p)) if (seen.Add(f.FullName)) list.Add(f);
            }
            return list;
        }

        public static ShredResult Shred(IList<string> items, int passes, IProgress<(long Done, long Total, string Current)>? progress, CancellationToken ct)
        {
            var r = new ShredResult();
            var files = Expand(items);
            long total = files.Sum(f => SafeLength(f)), done = 0;
            foreach (var f in files)
            {
                ct.ThrowIfCancellationRequested();
                if (CheckAllowed(f.FullName) is string why) { r.Failed.Add(L.F("{0}：{1}", f.FullName, why)); continue; }
                long len = SafeLength(f);
                try
                {
                    progress?.Report((done, total, f.FullName));
                    Overwrite(f.FullName, passes, n => { done += n; progress?.Report((done, total, f.FullName)); }, ct);
                    RemoveTraces(f.FullName);
                    r.Files++;
                    r.Bytes += len;
                    OpLog.Write("SHRED", $"{len}\t{f.FullName}\tpasses={passes}");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { r.Failed.Add(L.F("{0}：{1}", f.FullName, Friendly(ex))); }
            }
            foreach (var p in items.Where(Directory.Exists))
            {
                if (CheckAllowed(p) != null) continue;
                foreach (var d in AllDirs(p).OrderByDescending(d => d.Length))
                {
                    try { Directory.Delete(RenameRandom(d, isDir: true), false); }
                    catch (Exception) { }
                }
            }
            return r;
        }

        public static void Overwrite(string path, int passes, Action<long>? onBytes, CancellationToken ct)
        {
            var attr = File.GetAttributes(path);
            if ((attr & (FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System)) != 0)
                File.SetAttributes(path, FileAttributes.Normal);

            var buf = new byte[1 << 20];
            using (var rng = RandomNumberGenerator.Create())
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.WriteThrough))
            {
                long len = fs.Length;
                for (int pass = 0; pass < passes; pass++)
                {
                    fs.Position = 0;
                    long left = len;
                    while (left > 0)
                    {
                        ct.ThrowIfCancellationRequested();
                        rng.GetBytes(buf);
                        int n = (int)Math.Min(buf.Length, left);
                        fs.Write(buf, 0, n);
                        left -= n;
                        if (pass == passes - 1) onBytes?.Invoke(n);
                    }
                    fs.Flush(true);
                }
            }
        }

        public static void RemoveTraces(string path)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None))
            {
                fs.SetLength(0);
                fs.Flush(true);
            }
            string p = RenameRandom(path, isDir: false);
            var t = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            try { File.SetCreationTimeUtc(p, t); File.SetLastWriteTimeUtc(p, t); File.SetLastAccessTimeUtc(p, t); }
            catch (Exception) { }
            File.Delete(p);
        }

        static string RenameRandom(string path, bool isDir)
        {
            string current = path;
            string dir = Path.GetDirectoryName(path)!;
            for (int i = 0; i < 3; i++)
            {
                string next = Path.Combine(dir, Path.GetRandomFileName().Replace(".", ""));
                try
                {
                    if (isDir) Directory.Move(current, next); else File.Move(current, next);
                    current = next;
                }
                catch (Exception) { break; }
            }
            return current;
        }

        static IEnumerable<string> AllDirs(string root)
        {
            var stack = new Stack<DirectoryInfo>();
            stack.Push(new DirectoryInfo(root));
            while (stack.Count > 0)
            {
                var d = stack.Pop();
                yield return d.FullName;
                DirectoryInfo[] subs;
                try { subs = d.GetDirectories(); } catch (Exception) { continue; }
                foreach (var s in subs) if (!FsUtil.IsLinkLike(s)) stack.Push(s);
            }
        }

        static long SafeLength(FileInfo f) { try { return f.Length; } catch (Exception) { return 0; } }

        static string Friendly(Exception ex) =>
            ex is IOException && ex.HResult == unchecked((int)0x80070020) ? L.T("文件正在被其他程序使用") :
            ex is UnauthorizedAccessException ? L.T("没有权限（可能需要管理员身份）") : ex.Message;
    }
}
