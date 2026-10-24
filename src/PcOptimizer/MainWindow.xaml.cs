using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using PcOptimizer.Core;
using PcOptimizer.Pages;

namespace PcOptimizer
{
    public partial class MainWindow : Window
    {
        readonly Dictionary<string, UserControl> _pages = new Dictionary<string, UserControl>();
        readonly Dictionary<string, RadioButton> _navs;

        public static string Version =>
            Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "";

        public MainWindow()
        {
            InitializeComponent();
            UiText.Attach(this);
            Theme.Track(this);
            _navs = new Dictionary<string, RadioButton>
            {
                ["home"] = NavHome, ["clean"] = NavClean, ["startup"] = NavStartup, ["memory"] = NavMemory, ["uninstall"] = NavUninstall, ["privacy"] = NavPrivacy,
                ["disk"] = NavDisk, ["more"] = NavMore,
            };
            bool admin = SysInfo.IsAdmin();
            ElevateButton.Visibility = admin ? Visibility.Collapsed : Visibility.Visible;
            UpdateTexts();
            StateChanged += (s, e) =>
            {
                ((FrameworkElement)Content).Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
                MaxButton.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
            };
            NavHome.IsChecked = true;

            var args = Environment.GetCommandLineArgs();
            int i = Array.IndexOf(args, "--page");
            if (i > 0 && i + 1 < args.Length && _navs.ContainsKey(args[i + 1].Split(':')[0]))
                Navigate(args[i + 1]);
        }

        string CurrentPlace()
        {
            string current = "home";
            foreach (var kv in _pages)
                if (kv.Value == PageHost.Content) current = kv.Key;
            if (PageHost.Content is MorePage more && more.CurrentTool != null) return "more:" + more.CurrentTool;
            if (PageHost.Content is DiskPage disk && disk.HealthShown) return "disk:health";
            return current;
        }

        public void Navigate(string key)
        {
            if (key.StartsWith("more:"))
            {
                Navigate("more");
                ((MorePage)_pages["more"]).Open(key.Substring(5));
                return;
            }
            if (key == "disk:health")
            {
                Navigate("disk");
                ((DiskPage)_pages["disk"]).ShowHealth();
                return;
            }
            if (_navs.TryGetValue(key, out var nav) && nav.IsChecked != true)
            {
                nav.IsChecked = true;
                return;
            }
            if (!_pages.TryGetValue(key, out var page))
            {
                switch (key)
                {
                    case "clean": page = new CleanPage(); break;
                    case "startup": page = new StartupPage(); break;
                    case "memory": page = new MemoryPage(); break;
                    case "uninstall": page = new UninstallPage(); break;
                    case "privacy": page = new PrivacyPage(); break;
                    case "disk": page = new DiskPage(); break;
                    case "more": page = new MorePage(); break;
                    default: page = new HomePage(); break;
                }
                _pages[key] = page;
            }
            PageHost.Content = page;
            if (page is IPage p) p.OnShown();
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            if (SysInfo.IsAdmin())
            {
                AdminDrop.Enable(this, DropFiles);
                Loaded += (s, _) => AdminDrop.RevokeOle(this);
            }
        }

        void DropFiles(string[] paths)
        {
            if (PageHost.Content is IFileDropTarget t) t.AcceptFiles(paths);
            else SetStatus(L.T("这个页面不接收拖入的文件。要粉碎或扫描文件，请先打开“更多功能”里的“文件粉碎”或“病毒防护”。"));
        }

        public T GetPage<T>(string key) where T : UserControl
        {
            if (!_pages.ContainsKey(key)) Navigate(key);
            return (T)_pages[key];
        }

        public void SetStatus(string text) => StatusText.Text = text;

        void Nav_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is RadioButton rb && rb.CommandParameter is string key) Navigate(key);
        }

        void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
        void Maximize_Click(object sender, RoutedEventArgs e) =>
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        void Close_Click(object sender, RoutedEventArgs e) => Close();

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            if (Pages.Tools.NetTool.Recording &&
                !Dialog.Confirm(this, L.T("正在记录联网"), L.T("“联网控制”正在记录联网的软件，退出程序会停止记录，记录的结果也不会保留。确定要退出吗？"), L.T("退出")))
                e.Cancel = true;
            base.OnClosing(e);
        }

        void Logs_Click(object sender, RoutedEventArgs e)
        {
            new LogWindow { Owner = this }.ShowDialog();
        }

        void Settings_Click(object sender, RoutedEventArgs e)
        {
            var menu = new ContextMenu { PlacementTarget = SettingsButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Top };
            var header = (Style)FindResource("MenuHeader");

            menu.Items.Add(new MenuItem { Header = L.T("主题"), Style = header });
            foreach (var (code, name) in Theme.Modes)
            {
                var item = new MenuItem { Header = L.T(name), IsCheckable = true, IsChecked = code == Theme.Mode };
                item.Click += (s, _) =>
                {
                    if (!Theme.Set(code)) SetStatus(L.T("本工具所在的文件夹不能写入，主题设置只在这次打开时有效。"));
                };
                menu.Items.Add(item);
            }

            menu.Items.Add(new Separator());
            menu.Items.Add(new MenuItem { Header = "\U0001F310 " + (L.Lang == "en" ? "Language" : L.T("语言") + " / Language"), Style = header });
            foreach (var (code, name) in L.Languages)
            {
                var item = new MenuItem { Header = name, IsCheckable = true, IsChecked = code == L.Lang };
                item.Click += (s, _) => SwitchLanguage(code);
                menu.Items.Add(item);
            }

            menu.Items.Add(new Separator());
            var logs = new MenuItem { Header = L.T("操作日志") };
            logs.Click += Logs_Click;
            menu.Items.Add(logs);
            var about = new MenuItem { Header = L.T("关于") };
            about.Click += About_Click;
            menu.Items.Add(about);
            menu.IsOpen = true;
        }

        void UpdateTexts()
        {
            AdminText.Text = SysInfo.IsAdmin() ? L.T("管理员模式") : L.T("普通权限");
            Title = L.F("透明优化助手 v{0}", Version);
        }

        void SwitchLanguage(string code)
        {
            if (code == L.Lang) return;
            bool saved = L.Save(code);
            L.Use(code);
            App.ApplyFont();
            foreach (Window w in Application.Current.Windows)
            {
                w.Language = App.XmlLang;
                UiText.Retranslate(w);
            }
            UpdateTexts();

            string current = "home";
            foreach (var kv in _pages)
                if (kv.Value == PageHost.Content) current = kv.Key;
            _pages.Clear();
            PageHost.Content = null;
            Navigate(current);
            SetStatus(saved ? L.T("不联网 · 不常驻后台 · 删除前必先预览确认")
                            : L.T("本工具所在的文件夹不能写入，语言设置只在这次打开时有效。"));
        }

        void Elevate_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var exe = Process.GetCurrentProcess().MainModule?.FileName;
                Process.Start(new ProcessStartInfo(exe!) { UseShellExecute = true, Verb = "runas", Arguments = "--page " + CurrentPlace() });
                Application.Current.Shutdown();
            }
            catch (System.ComponentModel.Win32Exception)
            {
            }
        }

        void About_Click(object sender, RoutedEventArgs e)
        {
            Dialog.Info(this, L.T("关于透明优化助手"),
                L.F("版本 {0}\n\n", Version) +
                L.T("· 没有任何联网代码，源码可以逐行审查。\n") +
                L.T("· 不装服务、不加开机启动、不常驻后台，关闭窗口即完全退出。\n") +
                L.T("· 所有删除都先列出明细，确认后才执行。\n") +
                L.T("· 启动项只做“禁用/启用”，和任务管理器用的是同一套开关；只有指向程序已不存在的失效项才允许删除。\n") +
                L.T("· 卸载调用软件自带的卸载程序；残留文件夹移到回收站，注册表先备份成 .reg。\n") +
                L.T("· 内存优化只列出你自己运行的软件，不碰系统进程；不做“一键清理内存”这种只是把内存挪到硬盘的操作。\n") +
                L.T("· 文件去重以 MD5 为准，删除前重新校验；联网控制只用 Windows 防火墙和 QoS 策略，不装驱动；右键菜单只隐藏不删除；文件粉碎要二次确认，系统和软件目录不允许粉碎。\n") +
                L.T("· 病毒防护不自带病毒库，用的是 Windows 自带的 Microsoft Defender。\n") +
                L.F("· 每次实际操作都记录在：{0}", OpLog.LogDir));
        }

        public static void ShowProperties(Window owner, string path)
        {
            try { NativeMethods.SHObjectProperties(new System.Windows.Interop.WindowInteropHelper(owner).Handle, NativeMethods.SHOP_FILEPATH, path, null); }
            catch (Exception) { }
        }

        public static void Reveal(string path)
        {
            try
            {
                if (File.Exists(path)) Process.Start("explorer.exe", $"/select,\"{path}\"");
                else if (Directory.Exists(path)) Process.Start("explorer.exe", $"\"{path}\"");
            }
            catch (Exception) { }
        }
    }

    public interface IPage
    {
        void OnShown();
    }
}
