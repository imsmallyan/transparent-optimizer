using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace PcOptimizer.Core
{
    public class InstalledProgram : INotifyPropertyChanged
    {
        public string KeyName { get; set; } = "";
        public RegistryHive Hive { get; set; }
        public string KeyPath { get; set; } = "";
        public string Name { get; set; } = "";
        public string Publisher { get; set; } = "";
        public string Version { get; set; } = "";
        public string InstallLocation { get; set; } = "";
        public string UninstallString { get; set; } = "";
        public string DisplayIcon { get; set; } = "";
        public long SizeKB { get; set; }
        public string InstallDate { get; set; } = "";

        public string Initial => Name.Length > 0 ? Name.Substring(0, 1).ToUpperInvariant() : "?";
        public string SizeText => SizeKB > 0 ? FsUtil.HumanSize(SizeKB * 1024) : "";
        public string Meta => string.Join("  ·  ", new[] { Publisher, Version.Length > 0 ? L.T("版本 ") + Version : "", SizeText, InstallDate }
                                                     .Where(s => s.Length > 0));
        public bool NeedsAdmin => Hive == RegistryHive.LocalMachine;

        public bool IsBroken { get; set; }

        string _status = "";
        public string Status { get => _status; set { _status = value; Raise(nameof(Status)); } }

        public event PropertyChangedEventHandler? PropertyChanged;
        void Raise(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    public static class Programs
    {
        const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";
        const string UninstallKeyWow = @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall";

        public static List<InstalledProgram> List()
        {
            var list = new List<InstalledProgram>();
            var sources = new[]
            {
                (RegistryHive.LocalMachine, UninstallKey),
                (RegistryHive.LocalMachine, UninstallKeyWow),
                (RegistryHive.CurrentUser, UninstallKey),
            };
            foreach (var (hive, path) in sources)
            {
                try
                {
                    using (var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64).OpenSubKey(path))
                    {
                        if (root == null) continue;
                        foreach (var sub in root.GetSubKeyNames())
                        {
                            try
                            {
                                using (var k = root.OpenSubKey(sub))
                                {
                                    var p = Read(k, hive, path + "\\" + sub, sub);
                                    if (p != null) list.Add(p);
                                }
                            }
                            catch (Exception) { }
                        }
                    }
                }
                catch (Exception) { }
            }
            return list.GroupBy(p => (p.Name.ToLowerInvariant(), p.Version))
                       .Select(g => g.First())
                       .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
                       .ToList();
        }

        static InstalledProgram? Read(RegistryKey? k, RegistryHive hive, string keyPath, string keyName)
        {
            if (k == null) return null;
            string S(string n) => k.GetValue(n)?.ToString()?.Trim() ?? "";
            string name = S("DisplayName");
            string uninstall = S("UninstallString");
            if (name == "" || uninstall == "") return null;
            if (k.GetValue("SystemComponent") is int sc && sc == 1) return null;
            if (S("ParentKeyName") != "" || S("ReleaseType") is string rt && (rt == "Update" || rt == "Hotfix" || rt == "Security Update")) return null;

            var p = new InstalledProgram
            {
                KeyName = keyName, Hive = hive, KeyPath = keyPath, Name = name,
                Publisher = S("Publisher"), Version = S("DisplayVersion"),
                InstallLocation = S("InstallLocation").Trim('"'), UninstallString = uninstall,
                DisplayIcon = S("DisplayIcon"),
                SizeKB = k.GetValue("EstimatedSize") is int kb ? kb : 0,
                InstallDate = FormatDate(S("InstallDate")),
            };
            p.IsBroken = IsBrokenEntry(uninstall, File.Exists);
            return p;
        }

        static string FormatDate(string yyyymmdd) =>
            DateTime.TryParseExact(yyyymmdd, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
                ? d.ToString("yyyy-MM-dd") : "";

        public static bool IsBrokenEntry(string uninstallString, Func<string, bool> fileExists)
        {
            var (exe, _) = SplitCommand(uninstallString);
            if (exe.Length == 0) return false;
            string file = Path.GetFileName(exe).ToLowerInvariant();
            if (file == "msiexec.exe" || file == "msiexec" || file == "rundll32.exe" || file == "rundll32") return false;
            if (!Path.IsPathRooted(exe)) return false;
            return !fileExists(exe);
        }

        public static (string Exe, string Args) SplitCommand(string command)
        {
            command = Environment.ExpandEnvironmentVariables(command.Trim());
            if (command.StartsWith("\""))
            {
                int end = command.IndexOf('"', 1);
                if (end < 0) return (command.Trim('"'), "");
                return (command.Substring(1, end - 1), command.Substring(end + 1).Trim());
            }
            int idx = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (idx >= 0) return (command.Substring(0, idx + 4), command.Substring(idx + 4).Trim());
            int sp = command.IndexOf(' ');
            return sp > 0 ? (command.Substring(0, sp), command.Substring(sp + 1).Trim()) : (command, "");
        }

        public static bool StillInstalled(InstalledProgram p)
        {
            try
            {
                using (var k = RegistryKey.OpenBaseKey(p.Hive, RegistryView.Registry64).OpenSubKey(p.KeyPath))
                    return k != null;
            }
            catch (Exception) { return true; }
        }

        public static string RemoveEntry(InstalledProgram p)
        {
            string backup = RegBackup.ExportKey(p.Hive, p.KeyPath, L.T("卸载记录-") + p.Name);
            using (var parent = RegistryKey.OpenBaseKey(p.Hive, RegistryView.Registry64)
                       .OpenSubKey(p.KeyPath.Substring(0, p.KeyPath.LastIndexOf('\\')), true))
            {
                if (parent == null) throw new UnauthorizedAccessException(L.T("无法打开注册表项"));
                parent.DeleteSubKeyTree(p.KeyName, false);
            }
            OpLog.Write("REMOVE_UNINSTALL_ENTRY", $"{RegBackup.HiveName(p.Hive)}\\{p.KeyPath}\t{p.Name}\t备份={backup}");
            return backup;
        }
    }
}
