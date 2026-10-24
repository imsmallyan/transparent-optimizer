using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace PcOptimizer.Core
{
    public static class FsUtil
    {
        public static string HumanSize(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double n = bytes;
            int i = 0;
            while (n >= 1024 && i < units.Length - 1) { n /= 1024; i++; }
            return i == 0 ? $"{bytes} B" : $"{n:0.0} {units[i]}";
        }

        public static bool IsLinkLike(FileSystemInfo info)
        {
            try { return (info.Attributes & FileAttributes.ReparsePoint) != 0; }
            catch (Exception) { return true; }
        }

        public static IEnumerable<FileInfo> WalkFiles(string root, CancellationToken ct = default)
        {
            var stack = new Stack<DirectoryInfo>();
            stack.Push(new DirectoryInfo(root));
            while (stack.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                var dir = stack.Pop();
                FileSystemInfo[] entries;
                try { entries = dir.GetFileSystemInfos(); }
                catch (Exception) { continue; }
                foreach (var e in entries)
                {
                    if (IsLinkLike(e)) continue;
                    if (e is DirectoryInfo d) stack.Push(d);
                    else if (e is FileInfo f) yield return f;
                }
            }
        }

        public static bool IsWithin(string path, string root)
        {
            try
            {
                string p = Path.GetFullPath(path).TrimEnd('\\', '/');
                string r = Path.GetFullPath(root).TrimEnd('\\', '/');
                var cmp = Path.DirectorySeparatorChar == '\\'
                    ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                return p.Equals(r, cmp) || p.StartsWith(r + Path.DirectorySeparatorChar, cmp);
            }
            catch (Exception) { return false; }
        }

        public static bool WildcardMatch(string name, string pattern)
        {
            return Match(name.ToLowerInvariant(), 0, pattern.ToLowerInvariant(), 0);
        }

        static bool Match(string s, int i, string p, int j)
        {
            while (j < p.Length)
            {
                if (p[j] == '*')
                {
                    for (int k = i; k <= s.Length; k++)
                        if (Match(s, k, p, j + 1)) return true;
                    return false;
                }
                if (i >= s.Length || (p[j] != '?' && p[j] != s[i])) return false;
                i++; j++;
            }
            return i == s.Length;
        }
    }
}
