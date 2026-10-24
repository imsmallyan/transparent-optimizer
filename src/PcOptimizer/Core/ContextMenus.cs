using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Win32;

namespace PcOptimizer.Core
{
    public enum MenuKind { Verb, Handler }

    public class MenuItemEntry : INotifyPropertyChanged
    {
        public MenuKind Kind { get; set; }
        public string Title { get; set; } = "";
        public string Program { get; set; } = "";
        public string Company { get; set; } = "";
        public bool IsSystem { get; set; }
        public bool ShiftOnly { get; set; }
        public List<string> Scopes { get; } = new List<string>();
        public List<string> KeyPaths { get; } = new List<string>();
        public string Clsid { get; set; } = "";

        bool _enabled = true;
        public bool Enabled { get => _enabled; set { _enabled = value; Raise(nameof(Enabled)); Raise(nameof(StateText)); } }
        public string StateText => Enabled ? L.T("显示") : L.T("已隐藏");
        public string KindText => Kind == MenuKind.Verb ? L.T("菜单命令") : L.T("扩展组件");
        public string ScopeText => string.Join(L.T("、"), Scopes.Distinct());
        public string Initial => string.IsNullOrEmpty(Title) ? "?" : Title.Substring(0, 1).ToUpperInvariant();
        public string Meta => (Company.Length > 0 ? Company + " · " : "") + (Program.Length > 0 ? Program : (Clsid.Length > 0 ? Clsid : ""));

        public event PropertyChangedEventHandler? PropertyChanged;
        void Raise(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    public static class ContextMenus
    {
        const string BlockedKey = @"Software\Microsoft\Windows\CurrentVersion\Shell Extensions\Blocked";

        static readonly (string Key, string Label)[] Scopes =
        {
            ("*", L.T("所有文件")),
            ("AllFilesystemObjects", L.T("文件和文件夹")),
            ("Directory", L.T("文件夹")),
            ("Folder", L.T("文件夹")),
            (@"Directory\Background", L.T("文件夹空白处")),
            ("DesktopBackground", L.T("桌面空白处")),
            ("Drive", L.T("磁盘")),
        };

        public static List<MenuItemEntry> List()
        {
            var verbs = new Dictionary<string, MenuItemEntry>(StringComparer.OrdinalIgnoreCase);
            var handlers = new Dictionary<string, MenuItemEntry>(StringComparer.OrdinalIgnoreCase);
            var blocked = BlockedClsids();

            foreach (var (scope, label) in Scopes)
            {
                using (var shell = Registry.ClassesRoot.OpenSubKey(scope + @"\shell"))
                {
                    if (shell != null)
                        foreach (var verb in shell.GetSubKeyNames())
                        {
                            using (var k = shell.OpenSubKey(verb))
                            {
                                if (k == null || k.GetValue("ProgrammaticAccessOnly") != null) continue;
                                string command = (k.OpenSubKey("command")?.GetValue("") as string) ?? "";
                                string title = Text(k.GetValue("MUIVerb") as string) ?? Text(k.GetValue("") as string) ?? verb;
                                title = title.Replace("&", "");
                                string program = command.Length > 0 ? StartupManager.ParseExePath(command) : "";
                                if (program.Length == 0)
                                {
                                    string handler = (k.GetValue("ExplorerCommandHandler") as string)
                                                     ?? (k.OpenSubKey("command")?.GetValue("DelegateExecute") as string) ?? "";
                                    if (IsClsid(handler.Trim())) program = ComDll(handler.Trim().ToUpperInvariant());
                                }
                                string id = title + "|" + program;
                                if (!verbs.TryGetValue(id, out var e))
                                {
                                    e = new MenuItemEntry { Kind = MenuKind.Verb, Title = title, Program = program };
                                    FillCompany(e, program);
                                    verbs[id] = e;
                                }
                                e.Scopes.Add(label);
                                e.KeyPaths.Add($@"{scope}\shell\{verb}");
                                if (k.GetValue("Extended") != null) e.ShiftOnly = true;
                                if (k.GetValue("LegacyDisable") != null) e.Enabled = false;
                            }
                        }
                }

                using (var hk = Registry.ClassesRoot.OpenSubKey(scope + @"\shellex\ContextMenuHandlers"))
                {
                    if (hk != null)
                        foreach (var name in hk.GetSubKeyNames())
                        {
                            using (var k = hk.OpenSubKey(name))
                            {
                                string clsid = (k?.GetValue("") as string ?? "").Trim();
                                if (!IsClsid(clsid)) clsid = name.Trim();
                                if (!IsClsid(clsid)) continue;
                                clsid = clsid.ToUpperInvariant();
                                if (!handlers.TryGetValue(clsid, out var e))
                                {
                                    string dll = ComDll(clsid);
                                    string title = name.StartsWith("{") ? (ComName(clsid) ?? clsid) : name;
                                    e = new MenuItemEntry { Kind = MenuKind.Handler, Title = title, Program = dll, Clsid = clsid };
                                    FillCompany(e, dll);
                                    e.Enabled = !blocked.Contains(clsid);
                                    handlers[clsid] = e;
                                }
                                e.Scopes.Add(label);
                            }
                        }
                }
            }
            return verbs.Values.Concat(handlers.Values)
                .OrderBy(e => e.IsSystem)
                .ThenBy(e => e.Title, StringComparer.CurrentCulture)
                .ToList();
        }

        public static void SetEnabled(MenuItemEntry e, bool enabled)
        {
            if (e.Kind == MenuKind.Verb)
            {
                foreach (var path in e.KeyPaths)
                {
                    using (var k = Registry.ClassesRoot.OpenSubKey(path, true))
                    {
                        if (k == null) continue;
                        if (enabled) k.DeleteValue("LegacyDisable", false);
                        else k.SetValue("LegacyDisable", "", RegistryValueKind.String);
                    }
                }
            }
            else
            {
                using (var k = Registry.LocalMachine.CreateSubKey(BlockedKey, true))
                {
                    if (k == null) throw new UnauthorizedAccessException(L.T("无法打开注册表项"));
                    if (enabled) k.DeleteValue(e.Clsid, false);
                    else k.SetValue(e.Clsid, e.Title, RegistryValueKind.String);
                }
                if (enabled)
                    using (var k = Registry.CurrentUser.OpenSubKey(BlockedKey, true)) k?.DeleteValue(e.Clsid, false);
            }
            e.Enabled = enabled;
            OpLog.Write(enabled ? "MENU_ENABLE" : "MENU_DISABLE",
                $"{e.KindText}\t{e.Title}\t{(e.Kind == MenuKind.Verb ? string.Join(";", e.KeyPaths) : e.Clsid)}");
        }

        static HashSet<string> BlockedClsids()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var root in new[] { Registry.LocalMachine, Registry.CurrentUser })
                using (var k = root.OpenSubKey(BlockedKey))
                    if (k != null) foreach (var n in k.GetValueNames()) if (IsClsid(n)) set.Add(n.ToUpperInvariant());
            return set;
        }

        static bool IsClsid(string s) => s.Length == 38 && s[0] == '{' && s[37] == '}' && Guid.TryParse(s, out _);

        static string ComDll(string clsid)
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using (var b = RegistryKey.OpenBaseKey(RegistryHive.ClassesRoot, view))
                    using (var k = b.OpenSubKey($@"CLSID\{clsid}\InprocServer32"))
                    {
                        string? v = k?.GetValue("") as string;
                        if (!string.IsNullOrEmpty(v)) return Environment.ExpandEnvironmentVariables(v!.Trim('"'));
                    }
                }
                catch (Exception) { }
            }
            return "";
        }

        static string? ComName(string clsid)
        {
            using (var k = Registry.ClassesRoot.OpenSubKey($@"CLSID\{clsid}"))
            {
                string? v = k?.GetValue("") as string;
                return string.IsNullOrWhiteSpace(v) ? null : v;
            }
        }

        static void FillCompany(MenuItemEntry e, string program)
        {
            string windir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string path = program;
            if (path.Length > 0 && !Path.IsPathRooted(path))
                path = Path.Combine(Environment.SystemDirectory, path);
            try
            {
                if (path.Length > 0 && File.Exists(path))
                    e.Company = (FileVersionInfo.GetVersionInfo(path).CompanyName ?? "").Trim();
            }
            catch (Exception) { }
            e.IsSystem = e.Company.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase)
                         || (path.Length > 0 && FsUtil.IsWithin(path, windir) && e.Company.Length == 0)
                         || program.Length == 0 && e.Kind == MenuKind.Verb && e.Company.Length == 0 && IsBuiltinVerb(e.Title);
        }

        static bool IsBuiltinVerb(string title) =>
            new[] { "open", "opennewwindow", "opennewprocess", "opennewtab", "find", "runas", "print", "explore", "pintohome", "properties" }
                .Contains(title.ToLowerInvariant());

        static string? Text(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            if (!s!.StartsWith("@")) return s;
            var sb = new StringBuilder(512);
            return NativeMethods.SHLoadIndirectString(s, sb, sb.Capacity, IntPtr.Zero) == 0 ? sb.ToString() : null;
        }
    }
}
