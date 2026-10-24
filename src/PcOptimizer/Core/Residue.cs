using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace PcOptimizer.Core
{
    public enum ResidueKind { Folder, RegistryKey, StartupItem }

    public class ResidueItem : INotifyPropertyChanged
    {
        public ResidueKind Kind { get; set; }
        public string Display { get; set; } = "";
        public string Reason { get; set; } = "";
        public long Size { get; set; }
        public string FolderPath { get; set; } = "";
        public RegistryHive Hive { get; set; }
        public string KeyPath { get; set; } = "";
        public StartupItem? Startup { get; set; }

        public string KindText => Kind == ResidueKind.Folder ? L.T("文件夹") : Kind == ResidueKind.RegistryKey ? L.T("注册表") : L.T("启动项");
        public string SizeText => Kind == ResidueKind.Folder ? FsUtil.HumanSize(Size) : "";

        bool _checked = true;
        public bool Checked { get => _checked; set { _checked = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Checked))); } }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    public class ResidueResult
    {
        public int Removed;
        public List<string> Failed = new List<string>();
        public List<string> Backups = new List<string>();
    }

    public static class Residue
    {
        static readonly HashSet<string> Generic = new HashSet<string>
        {
            "microsoft", "windows", "commonfiles", "common", "intel", "nvidia", "amd", "google", "adobe", "apple",
            "packages", "temp", "programs", "system", "systemprofile", "users", "public", "default", "app", "apps",
            "software", "classes", "policies", "wow6432node", "tools", "tool", "data", "update", "updater",
        };

        public static string Normalize(string name)
        {
            string s = name.ToLowerInvariant();
            s = Regex.Replace(s, @"\(.*?\)|\[.*?\]", " ");
            s = Regex.Replace(s, @"\b(x64|x86|64-bit|32-bit|64bit|32bit|amd64|win64|win32)\b", " ");
            s = Regex.Replace(s, @"\bv?\d+(\.\d+)+\b", " ");      // 1.2.3 / v10.0
            s = Regex.Replace(s, @"\s\d+$", " ");
            s = new string(s.Where(char.IsLetterOrDigit).ToArray());
            return s;
        }

        static bool Usable(string normalized) => normalized.Length >= 2 && !Generic.Contains(normalized);

        static bool SameOrInside(string a, string b)
        {
            string x = a.TrimEnd('\\', '/') + "\\", y = b.TrimEnd('\\', '/') + "\\";
            x = x.Replace('/', '\\'); y = y.Replace('/', '\\');
            return x.StartsWith(y, StringComparison.OrdinalIgnoreCase);
        }

        public static List<string> FindFolders(string productName, string publisher, string installLocation,
            IEnumerable<string> roots, IEnumerable<string> protectedPaths, ISet<string> protectedNames)
        {
            var result = new List<string>();
            var protect = protectedPaths.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
            string prod = Normalize(productName), pub = Normalize(publisher);
            var rootList = roots.Where(r => !string.IsNullOrEmpty(r) && Directory.Exists(r)).ToList();

            string windir = Environment.GetEnvironmentVariable("WINDIR") ?? "";
            var critical = new[] { windir, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) }
                .Where(c => c.Length > 0).ToList();

            bool Allowed(string dir)
            {
                try { if (Path.GetPathRoot(Path.GetFullPath(dir))?.TrimEnd('\\', '/') == Path.GetFullPath(dir).TrimEnd('\\', '/')) return false; }
                catch (Exception) { return false; }
                if (critical.Any(c => SameOrInside(c, dir))) return false;
                if (windir.Length > 0 && SameOrInside(dir, windir)) return false;
                if (rootList.Any(r => SameOrInside(r, dir))) return false;
                if (protect.Any(p => SameOrInside(dir, p) || SameOrInside(p, dir))) return false;
                try { if ((new DirectoryInfo(dir).Attributes & FileAttributes.ReparsePoint) != 0) return false; }
                catch (Exception) { return false; }
                return true;
            }
            void Add(string dir)
            {
                if (Directory.Exists(dir) && Allowed(dir) && !result.Any(r => SameOrInside(dir, r)))
                {
                    result.RemoveAll(r => SameOrInside(r, dir));
                    result.Add(dir);
                }
            }

            if (!string.IsNullOrWhiteSpace(installLocation) && Path.IsPathRooted(installLocation))
                Add(installLocation);

            if (!Usable(prod) || protectedNames.Contains(prod)) return result;
            foreach (var root in rootList)
            {
                string[] children;
                try { children = Directory.GetDirectories(root); } catch (Exception) { continue; }
                foreach (var child in children)
                {
                    string n = Normalize(Path.GetFileName(child));
                    if (n == prod) Add(child);
                    else if (Usable(pub) && n == pub)
                    {
                        string[] grand;
                        try { grand = Directory.GetDirectories(child); } catch (Exception) { continue; }
                        foreach (var g in grand)
                            if (Normalize(Path.GetFileName(g)) == prod) Add(g);
                    }
                }
            }
            return result;
        }

        public static List<string> DataRoots()
        {
            var sf = new[]
            {
                Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86,
                Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.LocalApplicationData,
                Environment.SpecialFolder.CommonApplicationData,
            };
            var roots = sf.Select(Environment.GetFolderPath).ToList();
            roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"));
            string? low = Path.GetDirectoryName(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
            if (low != null) roots.Add(Path.Combine(low, "LocalLow"));
            return roots.Where(r => r.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        public static List<ResidueItem> Find(InstalledProgram removed, IList<InstalledProgram> remaining)
        {
            var items = new List<ResidueItem>();
            var protectedPaths = remaining.Select(p => p.InstallLocation).ToList();
            var protectedNames = new HashSet<string>(remaining.Select(p => Normalize(p.Name)));
            var protectedPublishers = new HashSet<string>(remaining.Select(p => Normalize(p.Publisher)));

            foreach (var dir in FindFolders(removed.Name, removed.Publisher, removed.InstallLocation,
                         DataRoots(), protectedPaths, protectedNames))
            {
                long size = 0;
                try { size = FsUtil.WalkFiles(dir).Sum(f => { try { return f.Length; } catch (Exception) { return 0L; } }); }
                catch (Exception) { }
                bool isInstall = removed.InstallLocation.Length > 0 &&
                                 string.Equals(dir.TrimEnd('\\'), removed.InstallLocation.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
                items.Add(new ResidueItem
                {
                    Kind = ResidueKind.Folder, Display = dir, FolderPath = dir, Size = size,
                    Reason = isInstall ? L.T("原安装目录") : L.T("文件夹名与软件名一致"),
                });
            }

            string prod = Normalize(removed.Name), pub = Normalize(removed.Publisher);
            if (Usable(prod) && !protectedNames.Contains(prod))
            {
                var bases = new[]
                {
                    (RegistryHive.CurrentUser, "Software"),
                    (RegistryHive.LocalMachine, "SOFTWARE"),
                    (RegistryHive.LocalMachine, @"SOFTWARE\WOW6432Node"),
                };
                foreach (var (hive, basePath) in bases)
                    foreach (var keyPath in FindKeys(hive, basePath, prod, pub, protectedPublishers))
                        items.Add(new ResidueItem
                        {
                            Kind = ResidueKind.RegistryKey, Hive = hive, KeyPath = keyPath,
                            Display = $"{RegBackup.HiveName(hive)}\\{keyPath}", Reason = L.T("键名与软件名一致"),
                        });
            }

            if (Usable(prod))
            {
                try
                {
                    foreach (var s in StartupManager.ListItems().Where(s => s.TargetMissing))
                    {
                        bool related = Normalize(s.Name) == prod || Normalize(s.TargetPath).Contains(prod) ||
                                       items.Any(i => i.Kind == ResidueKind.Folder && SameOrInside(s.TargetPath, i.FolderPath));
                        if (related)
                            items.Add(new ResidueItem
                            {
                                Kind = ResidueKind.StartupItem, Startup = s, Display = $"{s.Name}  →  {s.Command}",
                                Reason = L.T("开机启动项 指向的程序已不存在"),
                            });
                    }
                }
                catch (Exception) { }
            }
            return items;
        }

        static IEnumerable<string> FindKeys(RegistryHive hive, string basePath, string prod, string pub, ISet<string> protectedPublishers)
        {
            var found = new List<string>();
            try
            {
                using (var b = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64).OpenSubKey(basePath))
                {
                    if (b == null) return found;
                    foreach (var sub in b.GetSubKeyNames())
                    {
                        string n = Normalize(sub);
                        if (n == prod && !protectedPublishers.Contains(n)) found.Add(basePath + "\\" + sub);
                        else if (Usable(pub) && n == pub)
                        {
                            using (var pk = b.OpenSubKey(sub))
                            {
                                if (pk == null) continue;
                                foreach (var g in pk.GetSubKeyNames())
                                    if (Normalize(g) == prod) found.Add(basePath + "\\" + sub + "\\" + g);
                            }
                        }
                    }
                }
            }
            catch (Exception) { }
            return found;
        }

        public static ResidueResult Remove(IEnumerable<ResidueItem> items, string programName)
        {
            var r = new ResidueResult();
            foreach (var it in items)
            {
                try
                {
                    switch (it.Kind)
                    {
                        case ResidueKind.Folder:
                            RecycleBin.Send(it.FolderPath);
                            OpLog.Write("RESIDUE_FOLDER_TO_RECYCLEBIN", $"{programName}\t{it.FolderPath}");
                            break;
                        case ResidueKind.RegistryKey:
                            string backup = RegBackup.ExportKey(it.Hive, it.KeyPath, L.T("残留-") + programName);
                            r.Backups.Add(backup);
                            int cut = it.KeyPath.LastIndexOf('\\');
                            using (var parent = RegistryKey.OpenBaseKey(it.Hive, RegistryView.Registry64)
                                       .OpenSubKey(it.KeyPath.Substring(0, cut), true))
                            {
                                if (parent == null) throw new UnauthorizedAccessException(L.T("无法打开注册表项"));
                                parent.DeleteSubKeyTree(it.KeyPath.Substring(cut + 1), false);
                            }
                            OpLog.Write("RESIDUE_REGKEY", $"{programName}\t{it.Display}\t备份={backup}");
                            break;
                        case ResidueKind.StartupItem:
                            if (it.Startup != null)
                            {
                                string b = StartupManager.Remove(it.Startup);
                                if (b.EndsWith(".reg")) r.Backups.Add(b);
                            }
                            break;
                    }
                    r.Removed++;
                }
                catch (Exception e)
                {
                    string why = e is UnauthorizedAccessException || e is System.Security.SecurityException ? L.T("需要管理员权限") : e.Message;
                    r.Failed.Add(L.F("{0}（{1}）", it.Display, why));
                }
            }
            return r;
        }
    }
}
