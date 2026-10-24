using System;
using System.Collections.Generic;
using System.Linq;
using System.Security;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using PcOptimizer.Core;

namespace PcOptimizer.Pages
{
    public partial class MemoryPage : UserControl, IPage
    {
        List<AppGroup> _all = new List<AppGroup>();
        List<AppGroup> _shown = new List<AppGroup>();
        bool _busy;

        public MemoryPage()
        {
            InitializeComponent();
        }

        MainWindow Main => (MainWindow)Application.Current.MainWindow;

        public void OnShown() => _ = Refresh();

        async Task Refresh()
        {
            if (_busy) return;
            _busy = true;
            try
            {
                Headline.Text = L.T("正在读取正在运行的软件…");
                var selected = new HashSet<string>(_all.Where(a => a.Checked).Select(a => a.Path), StringComparer.OrdinalIgnoreCase);
                _all = await Task.Run(() => MemoryManager.List());
                foreach (var a in _all) a.Checked = selected.Contains(a.Path);
                UpdateMemory();
                ApplyFilter();
            }
            finally { _busy = false; }
        }

        void UpdateMemory()
        {
            var m = SysInfo.Memory();
            if (m == null) return;
            ulong used = m.Value.Total - m.Value.Avail;
            double pct = m.Value.Total > 0 ? 100.0 * used / m.Value.Total : 0;
            MemBar.Value = pct;
            MemPercent.Text = $"{pct:0}%";
            MemPercent.Foreground = (System.Windows.Media.Brush)FindResource(pct >= 85 ? "Bad" : pct >= 70 ? "Warn" : "PrimaryDark");
            MemText.Text = L.F("已用 {0} · 可用 {1} · 共 {2}", FsUtil.HumanSize((long)used), FsUtil.HumanSize((long)m.Value.Avail), FsUtil.HumanSize((long)m.Value.Total));
        }

        void ApplyFilter()
        {
            _shown = _all.Where(a => BackgroundOnly.IsChecked != true || a.IsBackground).ToList();
            AppList.ItemsSource = _shown;
            EmptyCard.Visibility = _shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            int bg = _all.Count(a => a.IsBackground);
            long total = _all.Sum(a => a.Memory);
            Headline.Text = _all.Count == 0 ? L.T("没有其他软件在运行") : L.F("{0} 个软件正在运行 共占用约 {1}", _all.Count, FsUtil.HumanSize(total));
            BackgroundLabel.Text = L.F("只看后台运行的（{0}）", bg);
            UpdateSummary();
        }

        void UpdateSummary()
        {
            var sel = _all.Where(a => a.Checked).ToList();
            Summary.Text = sel.Count == 0 ? L.T("勾选后可以一起结束") : L.F("已勾选 {0} 个 · 约 {1}", sel.Count, FsUtil.HumanSize(sel.Sum(a => a.Memory)));
            EndSelectedButton.IsEnabled = sel.Count > 0;
            EndSelectedButton.Content = sel.Count > 0 ? L.F("结束所选（{0}）", sel.Count) : L.T("结束所选");
            SelectAll.IsChecked = _shown.Count > 0 && _shown.All(a => a.Checked);
        }

        void Filter_Changed(object sender, RoutedEventArgs e) => ApplyFilter();
        void Item_Checked(object sender, RoutedEventArgs e) => UpdateSummary();
        async void Refresh_Click(object sender, RoutedEventArgs e) => await Refresh();

        void SelectAll_Click(object sender, RoutedEventArgs e)
        {
            bool on = SelectAll.IsChecked == true;
            foreach (var a in _shown) a.Checked = on;
            UpdateSummary();
        }

        async void EndOne_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button b && b.Tag is AppGroup g) await EndApps(new List<AppGroup> { g });
        }

        async void EndSelected_Click(object sender, RoutedEventArgs e) =>
            await EndApps(_all.Where(a => a.Checked).ToList());

        async Task EndApps(List<AppGroup> apps)
        {
            if (apps.Count == 0 || _busy) return;
            string list = string.Join("\n", apps.Take(8).Select(a => L.F("· {0}（约 {1}）", a.Name, a.MemoryText))) +
                          (apps.Count > 8 ? L.F("\n……共 {0} 个", apps.Count) : "");
            bool anyStartup = apps.Any(a => a.StartupEnabled);
            if (!Dialog.Confirm(Main, L.T("结束软件"),
                    L.F("将结束：\n\n{0}\n\n", list) +
                    L.T("有窗口的软件会先被要求正常关闭（和点右上角 × 一样）；3 秒内没退出的会被强制结束，没保存的内容会丢失。") +
                    (anyStartup ? L.T("\n\n其中有开机自启的软件，下次开机还会运行；不想让它运行可以点“关闭自启”。") : ""),
                    L.T("结束")))
                return;

            _busy = true;
            ulong availBefore = SysInfo.Memory()?.Avail ?? 0;
            var failed = new List<string>();
            int killed = 0, ok = 0;
            foreach (var a in apps)
            {
                a.Status = L.T("正在结束…");
                var r = await Task.Run(() => MemoryManager.End(a));
                killed += r.Killed;
                failed.AddRange(r.Failed);
                if (r.Failed.Count == 0) ok++;
                a.Status = r.Failed.Count > 0 ? L.T("部分进程没能结束") : L.T("已结束");
            }
            await Task.Delay(800);
            ulong availAfter = SysInfo.Memory()?.Avail ?? 0;
            _busy = false;
            foreach (var a in apps) a.Checked = false;
            await Refresh();

            long freed = (long)availAfter - (long)availBefore;
            string msg = L.F("已结束 {0} 个软件", ok) +
                         (freed > 0 ? L.F("，可用内存多了约 {0}", FsUtil.HumanSize(freed)) : "") +
                         (killed > 0 ? L.F("（其中 {0} 个进程是强制结束的）", killed) : "");
            if (failed.Count > 0)
            {
                bool denied = failed.Any(f => f.Contains("拒绝") || f.Contains("拒絕") || f.Contains("拒否") || f.IndexOf("denied", StringComparison.OrdinalIgnoreCase) >= 0);
                Dialog.Warn(Main, L.T("部分没有结束"), string.Join("\n", failed.Take(6)) +
                    (denied ? L.T("\n\n以管理员身份运行的软件需要本工具也以管理员身份运行才能结束，请点底部的“以管理员身份重新打开”。") : ""));
            }
            Main.SetStatus(msg);
        }

        void DisableStartup_Click(object sender, RoutedEventArgs e)
        {
            if (!(sender is Button b) || !(b.Tag is AppGroup g) || g.Startup == null) return;
            try
            {
                StartupManager.SetEnabled(g.Startup, false);
                g.StartupChanged();
                Main.SetStatus(L.F("已关闭“{0}”的开机自启，下次开机生效；随时可以在“启动加速”里打开。", g.Name));
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException || ex is SecurityException)
            {
                Dialog.Warn(Main, L.T("权限不足"), L.F("“{0}”是所有用户共用的启动项，需要管理员权限才能修改。\n\n请点底部的“以管理员身份重新打开”。", g.Name));
            }
            catch (Exception ex)
            {
                Dialog.Warn(Main, L.T("没有改成功"), ex.Message);
            }
        }
    }
}
