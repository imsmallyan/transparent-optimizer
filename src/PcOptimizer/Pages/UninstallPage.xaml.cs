using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using PcOptimizer.Core;

namespace PcOptimizer.Pages
{
    public partial class UninstallPage : UserControl, IPage
    {
        List<InstalledProgram> _all = new List<InstalledProgram>();
        readonly ObservableCollection<InstalledProgram> _recent = new ObservableCollection<InstalledProgram>();
        readonly HashSet<string> _pending = new HashSet<string>();
        bool _loaded;

        public UninstallPage()
        {
            InitializeComponent();
            RecentList.ItemsSource = _recent;
            _recent.CollectionChanged += (s, e) => UpdateRecent();
        }

        void UpdateRecent()
        {
            RecentPanel.Visibility = _recent.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            RecentTitle.Text = L.F("刚卸载的软件（{0}） 可以再扫描一次残留", _recent.Count);
        }

        void DismissRecent_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button b && b.Tag is InstalledProgram p) _recent.Remove(p);
        }

        void ClearRecent_Click(object sender, RoutedEventArgs e) => _recent.Clear();

        MainWindow Main => (MainWindow)Application.Current.MainWindow;

        public void OnShown()
        {
            if (!_loaded) _ = Refresh();
        }

        static string Id(InstalledProgram p) => $"{p.Hive}\\{p.KeyPath}";

        async Task Refresh()
        {
            _loaded = true;
            Headline.Text = L.T("正在读取已安装的软件…");
            var old = _all;
            _all = await Task.Run(() => Programs.List());

            var nowIds = new HashSet<string>(_all.Select(Id));
            foreach (var p in old.Where(p => _pending.Contains(Id(p)) && !nowIds.Contains(Id(p))).ToList())
            {
                _pending.Remove(Id(p));
                if (!_recent.Any(r => Id(r) == Id(p))) _recent.Insert(0, p);
            }
            ApplyFilter();
        }

        void ApplyFilter()
        {
            string q = SearchBox.Text.Trim();
            var list = _all.Where(p => q.Length == 0
                                       || p.Name.IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0
                                       || p.Publisher.IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0)
                           .Where(p => BrokenOnly.IsChecked != true || p.IsBroken)
                           .ToList();
            ProgramList.ItemsSource = list;
            EmptyCard.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            int broken = _all.Count(p => p.IsBroken);
            Headline.Text = L.F("共 {0} 个软件", _all.Count) + (broken > 0 ? L.F(" 其中 {0} 条卸载记录已失效", broken) : "");
            BrokenLabel.Text = L.F("只看失效记录（{0}）", broken);
        }

        void Search_Changed(object sender, TextChangedEventArgs e) => ApplyFilter();
        void Filter_Changed(object sender, RoutedEventArgs e) => ApplyFilter();
        void OpenApps_Click(object sender, RoutedEventArgs e) => Main.Navigate("more:apps");
        async void Refresh_Click(object sender, RoutedEventArgs e) => await Refresh();

        async void Uninstall_Click(object sender, RoutedEventArgs e)
        {
            if (!(sender is Button b) || !(b.Tag is InstalledProgram p)) return;
            if (!Dialog.Confirm(Main, L.T("卸载软件"),
                    L.F("将运行“{0}”自带的卸载程序，请在它弹出的窗口里完成卸载。\n\n", p.Name) +
                    L.T("卸载完成后，本工具会查找残留的文件夹、注册表和启动项，并让你逐项确认后再清理。"),
                    L.T("开始卸载")))
                return;

            var (exe, args) = Programs.SplitCommand(p.UninstallString);
            if (exe.EndsWith("msiexec.exe", StringComparison.OrdinalIgnoreCase) || exe.Equals("msiexec", StringComparison.OrdinalIgnoreCase))
                args = System.Text.RegularExpressions.Regex.Replace(args, @"(?i)/I\s*\{", "/X{");

            p.Status = L.T("正在运行卸载程序…");
            _pending.Add(Id(p));
            OpLog.Write("UNINSTALL_START", $"{p.Name}\t{exe} {args}");
            try
            {
                using (var proc = Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = true }))
                {
                    if (proc != null) await Task.Run(() => proc.WaitForExit());
                }
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                _pending.Remove(Id(p));
                p.Status = ex.NativeErrorCode == 1223 ? L.T("已取消") : L.T("无法启动卸载程序：") + ex.Message;
                return;
            }

            p.Status = L.T("等待卸载完成…");
            for (int i = 0; i < 15 && Programs.StillInstalled(p); i++) await Task.Delay(2000);

            if (Programs.StillInstalled(p))
            {
                p.Status = L.T("卸载程序已关闭，但软件还在。如果还在卸载中，完成后点“刷新”");
                return;
            }
            OpLog.Write("UNINSTALL_DONE", p.Name);
            await Refresh();
            ShowResidue(p);
        }

        void ScanResidue_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button b && b.Tag is InstalledProgram p) ShowResidue(p);
        }

        void ShowResidue(InstalledProgram removed)
        {
            new ResidueWindow(removed, _all) { Owner = Main }.ShowDialog();
        }

        async void RemoveEntry_Click(object sender, RoutedEventArgs e)
        {
            if (!(sender is Button b) || !(b.Tag is InstalledProgram p)) return;
            if (!Dialog.Confirm(Main, L.T("删除失效的卸载记录"),
                    L.F("“{0}”的卸载程序已经不存在，这条记录无法再用来卸载。\n\n", p.Name) +
                    L.T("删除前会把这条注册表记录备份成 .reg 文件，双击即可恢复。删除后还会查找这个软件的其他残留。"),
                    L.T("删除记录")))
                return;
            try
            {
                string backup = Programs.RemoveEntry(p);
                Main.SetStatus(L.F("已删除“{0}”的卸载记录，备份：{1}", p.Name, backup));
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException || ex is System.Security.SecurityException)
            {
                Dialog.Warn(Main, L.T("权限不足"), L.T("这条记录属于“所有用户”，需要管理员权限才能删除。\n\n请点底部的“以管理员身份重新打开”。"));
                return;
            }
            catch (Exception ex)
            {
                Dialog.Warn(Main, L.T("没有删除成功"), ex.Message);
                return;
            }
            await Refresh();
            if (!_recent.Any(r => Id(r) == Id(p))) _recent.Insert(0, p);
            ShowResidue(p);
        }
    }
}
