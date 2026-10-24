using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace PcOptimizer.Core
{
    public class StartupItem : INotifyPropertyChanged
    {
        public string Name { get; set; } = "";
        public string Command { get; set; } = "";
        public string Source { get; set; } = "";
        public RegistryHive Hive { get; set; }
        public string ApprovedSub { get; set; } = "";
        public bool NeedsAdmin { get; set; }
        public string RunKeyPath { get; set; } = "";
        public string TargetPath { get; set; } = "";
        public bool TargetMissing { get; set; }

        bool _enabled;
        public bool Enabled
        {
            get => _enabled;
            set { _enabled = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Enabled))); PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StateText))); }
        }
        public string StateText => Enabled ? L.T("已启用") : L.T("已禁用");
        public string Initial => string.IsNullOrEmpty(Name) ? "?" : Name.Substring(0, 1).ToUpperInvariant();

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    public static class StartupManager
    {
        const string Run = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunWow = @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";
        const string Approved = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";

        static RegistryKey Base(RegistryHive hive) => RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);

        static List<KeyValuePair<string, object?>> ReadValues(RegistryHive hive, string path)
        {
            var list = new List<KeyValuePair<string, object?>>();
            try
            {
                using (var k = Base(hive).OpenSubKey(path))
                {
                    if (k == null) return list;
                    foreach (var n in k.GetValueNames())
                        list.Add(new KeyValuePair<string, object?>(n, k.GetValue(n, null, RegistryValueOptions.DoNotExpandEnvironmentNames)));
                }
            }
            catch (Exception) { }
            return list;
        }

        static bool IsApproved(RegistryHive hive, string sub, string name)
        {
            foreach (var kv in ReadValues(hive, Approved + "\\" + sub))
                if (kv.Key == name && kv.Value is byte[] b && b.Length > 0)
                    return (b[0] & 1) == 0;
            return true;
        }

        public static List<StartupItem> ListItems()
        {
            var items = new List<StartupItem>();
            var sources = new[]
            {
                (RegistryHive.CurrentUser, Run, "Run", L.T("当前用户（注册表）"), false),
                (RegistryHive.LocalMachine, Run, "Run", L.T("所有用户（注册表）"), true),
                (RegistryHive.LocalMachine, RunWow, "Run32", L.T("所有用户·32位（注册表）"), true),
            };
            foreach (var (hive, path, sub, label, admin) in sources)
                foreach (var kv in ReadValues(hive, path))
                {
                    if (string.IsNullOrEmpty(kv.Key)) continue;
                    string cmd = kv.Value?.ToString() ?? "";
                    string target = ParseExePath(cmd);
                    items.Add(new StartupItem
                    {
                        Name = kv.Key, Command = cmd, Source = label,
                        Hive = hive, ApprovedSub = sub, NeedsAdmin = admin, RunKeyPath = path,
                        Enabled = IsApproved(hive, sub, kv.Key),
                        TargetPath = target, TargetMissing = IsMissing(target),
                    });
                }

            string rel = @"Microsoft\Windows\Start Menu\Programs\Startup";
            var folders = new[]
            {
                (RegistryHive.CurrentUser, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), rel), L.T("当前用户启动文件夹"), false),
                (RegistryHive.LocalMachine, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), rel), L.T("所有用户启动文件夹"), true),
            };
            foreach (var (hive, folder, label, admin) in folders)
            {
                if (!Directory.Exists(folder)) continue;
                string[] files;
                try { files = Directory.GetFiles(folder); } catch (Exception) { continue; }
                foreach (var file in files.OrderBy(f => f))
                {
                    string fn = Path.GetFileName(file);
                    if (fn.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                    string target = fn.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)
                        ? ShellLink.GetTarget(file) ?? "" : file;
                    items.Add(new StartupItem
                    {
                        TargetPath = target, TargetMissing = IsMissing(target),
                        Name = fn, Command = file, Source = label, Hive = hive,
                        ApprovedSub = "StartupFolder", NeedsAdmin = admin,
                        Enabled = IsApproved(hive, "StartupFolder", fn),
                    });
                }
            }
            return items;
        }

        public static void SetEnabled(StartupItem item, bool enabled)
        {
            byte[] data = new byte[12];
            if (enabled)
            {
                data[0] = 0x02;
            }
            else
            {
                data[0] = 0x03;
                BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()).CopyTo(data, 4);
            }
            using (var k = Base(item.Hive).CreateSubKey(Approved + "\\" + item.ApprovedSub, true))
            {
                if (k == null) throw new UnauthorizedAccessException(L.T("无法打开注册表项"));
                k.SetValue(item.Name, data, RegistryValueKind.Binary);
            }
            item.Enabled = enabled;
            OpLog.Write(enabled ? "STARTUP_ENABLE" : "STARTUP_DISABLE",
                $"{item.Hive}\\{item.ApprovedSub}\t{item.Name}\t{item.Command}");
        }

        static bool IsMissing(string path)
        {
            try { return Path.IsPathRooted(path) && !File.Exists(path) && !Directory.Exists(path); }
            catch (Exception) { return false; }
        }

        public static string Remove(StartupItem item)
        {
            if (!item.TargetMissing) throw new InvalidOperationException(L.T("只能删除失效的启动项"));
            string backup;
            if (item.RunKeyPath.Length > 0)
            {
                backup = RegBackup.ExportValue(item.Hive, item.RunKeyPath, item.Name, L.T("启动项-") + item.Name);
                using (var k = Base(item.Hive).OpenSubKey(item.RunKeyPath, true))
                {
                    if (k == null) throw new UnauthorizedAccessException(L.T("无法打开注册表项"));
                    k.DeleteValue(item.Name, false);
                }
            }
            else
            {
                RecycleBin.Send(item.Command);
                backup = L.T("回收站");
            }
            try
            {
                using (var k = Base(item.Hive).OpenSubKey(Approved + "\\" + item.ApprovedSub, true))
                    k?.DeleteValue(item.Name, false);
            }
            catch (Exception) { }
            OpLog.Write("STARTUP_REMOVE", $"{item.Hive}\t{item.Name}\t{item.Command}\t备份={backup}");
            return backup;
        }

        public static string ParseExePath(string command)
        {
            command = Environment.ExpandEnvironmentVariables(command.Trim());
            if (command.StartsWith("\""))
            {
                int end = command.IndexOf('"', 1);
                return end > 0 ? command.Substring(1, end - 1) : command.Substring(1);
            }
            string lower = command.ToLowerInvariant();
            foreach (var ext in new[] { ".exe", ".lnk", ".bat", ".cmd", ".vbs" })
            {
                int idx = lower.IndexOf(ext, StringComparison.Ordinal);
                if (idx >= 0) return command.Substring(0, idx + ext.Length);
            }
            int sp = command.IndexOf(' ');
            return sp > 0 ? command.Substring(0, sp) : command;
        }
    }
}
