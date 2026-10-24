using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PcOptimizer.Core;

namespace PcOptimizer.Pages
{
    public partial class DiskPage : UserControl, IPage
    {
        CancellationTokenSource? _cts;

        public DiskPage()
        {
            InitializeComponent();
            PathBox.Text = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        MainWindow Main => (MainWindow)Application.Current.MainWindow;

        public void OnShown() => DriveList.ItemsSource = SysInfo.Drives();

        async Task Analyze(string root)
        {
            if (_cts != null) return;
            if (!Directory.Exists(root)) { Dialog.Warn(Main, L.T("目录不存在"), root); return; }
            PathBox.Text = root;
            _cts = new CancellationTokenSource();
            Progress.Visibility = StopButton.Visibility = Visibility.Visible;
            GoButton.IsEnabled = false;
            var progress = new Progress<string>(s => SummaryText.Text = s);
            try
            {
                var token = _cts.Token;
                var report = await Task.Run(() => DiskUsage.Analyze(root, 50, progress, token));
                DirList.ItemsSource = report.Children;
                FileList.ItemsSource = report.Biggest;
                SummaryText.Text = L.F("{0} 共 {1}", root, FsUtil.HumanSize(report.Total)) +
                                   (token.IsCancellationRequested ? L.T("（已停止，结果不完整）") : L.T("（没有权限读取的目录不计入）"));
            }
            finally
            {
                _cts = null;
                Progress.Visibility = StopButton.Visibility = Visibility.Collapsed;
                GoButton.IsEnabled = true;
            }
        }

        async void Go_Click(object sender, RoutedEventArgs e) => await Analyze(PathBox.Text.Trim());

        async void PathBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) await Analyze(PathBox.Text.Trim());
        }

        async void Drive_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button b && b.Tag is string drive) await Analyze(drive);
        }

        async void Up_Click(object sender, RoutedEventArgs e)
        {
            var parent = Directory.GetParent(PathBox.Text.Trim().TrimEnd('\\'));
            if (parent != null) await Analyze(parent.FullName);
        }

        async void Browse_Click(object sender, RoutedEventArgs e)
        {
            using (var dlg = new System.Windows.Forms.FolderBrowserDialog { SelectedPath = PathBox.Text })
            {
                if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK) await Analyze(dlg.SelectedPath);
            }
        }

        void Stop_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

        async void DirList_DoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (DirList.SelectedItem is DirSize d && !d.IsLooseFiles) await Analyze(d.Path);
        }

        void FileList_DoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (FileList.SelectedItem is BigFile f) MainWindow.Reveal(f.Path);
        }

        void FileList_RightDown(object sender, MouseButtonEventArgs e)
        {
            var item = ItemsControl.ContainerFromElement(FileList, (DependencyObject)e.OriginalSource) as ListBoxItem;
            if (item != null) item.IsSelected = true;
        }

        void FileList_MenuOpening(object sender, ContextMenuEventArgs e)
        {
            if (!(FileList.SelectedItem is BigFile f)) { e.Handled = true; return; }
            foreach (var o in FileList.ContextMenu.Items) if (o is DependencyObject d) UiText.Translate(d);
            string? why = SpaceHints.RecycleBlocked(f.Path);
            MenuRecycle.IsEnabled = MenuDelete.IsEnabled = why == null;
            MenuRecycle.ToolTip = MenuDelete.ToolTip = why;
            ToolTipService.SetShowOnDisabled(MenuRecycle, true);
            ToolTipService.SetShowOnDisabled(MenuDelete, true);
        }

        async void MenuRecycle_Click(object sender, RoutedEventArgs e)
        {
            if (FileList.SelectedItem is BigFile f) await RecycleFile(f, null);
        }

        void MenuReveal_Click(object sender, RoutedEventArgs e)
        {
            if (FileList.SelectedItem is BigFile f) MainWindow.Reveal(f.Path);
        }

        void MenuProps_Click(object sender, RoutedEventArgs e)
        {
            if (FileList.SelectedItem is BigFile f) MainWindow.ShowProperties(Main, f.Path);
        }

        void MenuDelete_Click(object sender, RoutedEventArgs e)
        {
            if (!(FileList.SelectedItem is BigFile f)) return;
            string? why = SpaceHints.RecycleBlocked(f.Path);
            if (why != null) { Dialog.Warn(Main, L.T("不能删除"), why); return; }
            if (!Dialog.Confirm(Main, L.T("直接删除"),
                    L.F("将直接删除这个文件，不进回收站：\n{0}\n大小 {1}\n\n删除后无法从回收站还原。不确定的话，请改用“移到回收站”。", f.Path, f.SizeText),
                    L.T("直接删除")))
                return;
            try
            {
                var fi = new FileInfo(f.Path);
                if (!fi.Exists) throw new FileNotFoundException(L.T("文件已经不存在。"));
                if (fi.Length != f.Size) throw new IOException(L.T("文件在分析后被改动过，为安全起见没有删除。请重新分析。"));
                if ((fi.Attributes & FileAttributes.ReadOnly) != 0) fi.Attributes &= ~FileAttributes.ReadOnly;
                fi.Delete();
                OpLog.Write("DISK_DELETE", $"{f.Path}\t{f.Size}");
                RemoveFile(f);
                Main.SetStatus(L.F("已删除“{0}”", Path.GetFileName(f.Path)));
            }
            catch (Exception ex)
            {
                Dialog.Warn(Main, L.T("没有删除"), ex.Message);
            }
        }

        void RemoveFile(BigFile f)
        {
            if (FileList.ItemsSource is IEnumerable<BigFile> list) FileList.ItemsSource = list.Where(x => x != f).ToList();
        }

        async void Recycle_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button b && b.Tag is BigFile f) await RecycleFile(f, b);
        }

        async Task RecycleFile(BigFile f, Button? b)
        {
            string? why = SpaceHints.RecycleBlocked(f.Path);
            if (why != null) { Dialog.Warn(Main, L.T("不能删除"), why); return; }
            if (!Dialog.Confirm(Main, L.T("移到回收站"),
                    L.F("将把这个文件移到回收站：\n{0}\n大小 {1}\n\n删错了可以从回收站还原。清空回收站后才会真正释放空间。", f.Path, f.SizeText),
                    L.T("移到回收站")))
                return;
            if (b != null) b.IsEnabled = false;
            try
            {
                await Sta.Run(() => { RecycleBin.Send(f.Path); return true; });
                OpLog.Write("DISK_RECYCLE", $"{f.Path}\t{f.Size}");
                RemoveFile(f);
                Main.SetStatus(L.F("已把“{0}”移到回收站", Path.GetFileName(f.Path)));
            }
            catch (Exception ex)
            {
                if (b != null) b.IsEnabled = true;
                Dialog.Warn(Main, L.T("没有移到回收站"), ex.Message);
            }
        }

        bool _healthLoaded, _healthBusy;

        void Tab_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded) return;
            bool health = TabHealth.IsChecked == true;
            HealthPanel.Visibility = health ? Visibility.Visible : Visibility.Collapsed;
            SpacePanel.Visibility = health ? Visibility.Collapsed : Visibility.Visible;
            if (health && !_healthLoaded) _ = LoadHealth();
        }

        public bool HealthShown => TabHealth.IsChecked == true;

        public void ShowHealth()
        {
            TabHealth.IsChecked = true;
            if (!IsLoaded)
            {
                RoutedEventHandler? once = null;
                once = (s, e) => { Loaded -= once; Tab_Changed(this, e); };
                Loaded += once;
            }
        }

        async void HealthRefresh_Click(object sender, RoutedEventArgs e) => await LoadHealth();

        async Task LoadHealth()
        {
            if (_healthBusy) return;
            _healthBusy = true;
            HealthRefresh.IsEnabled = false;
            HealthProgress.Visibility = Visibility.Visible;
            HealthHeadline.Text = L.T("正在读取硬盘信息…");
            try
            {
                var disks = await Task.Run(() => DiskHealth.List());
                _healthLoaded = true;
                DiskCards.ItemsSource = disks;
                if (disks.Count == 0)
                {
                    HealthHeadline.Text = L.T("硬盘健康");
                    HealthEmptyText.Text = L.T("没有读到硬盘信息。可能是 Windows 的存储管理组件不可用，或 PowerShell 被禁用了。");
                    HealthEmpty.Visibility = Visibility.Visible;
                    return;
                }
                HealthEmpty.Visibility = Visibility.Collapsed;
                int bad = disks.Count(d => d.IsBad), warn = disks.Count(d => d.Level == HealthLevel.Warn);
                HealthHeadline.Text = bad > 0 ? L.F("{0} 块硬盘有问题，请尽快备份", bad)
                                    : warn > 0 ? L.F("{0} 块硬盘需要留意", warn)
                                    : L.F("{0} 块硬盘，状态良好", disks.Count);
                if (!SysInfo.IsAdmin() && disks.All(d => !d.CountersRead))
                {
                    HealthEmptyText.Text = L.T("现在是普通权限，只能读到健康状态。通电时间、温度、寿命等记录需要点底部的“以管理员身份重新打开”。");
                    HealthEmpty.Visibility = Visibility.Visible;
                }
            }
            finally
            {
                _healthBusy = false;
                HealthRefresh.IsEnabled = true;
                HealthProgress.Visibility = Visibility.Collapsed;
            }
        }
    }
}
