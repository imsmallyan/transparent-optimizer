using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using PcOptimizer.Core;

namespace PcOptimizer.Pages
{
    public class TargetRow : INotifyPropertyChanged
    {
        public CleanTarget Target { get; }
        public TargetRow(CleanTarget t, bool admin)
        {
            Target = t;
            Available = t.Roots.Count > 0 && (admin || !t.NeedsAdmin);
            _isChecked = Available && t.DefaultOn;
        }

        public bool Available { get; }
        public bool NotFound => Target.Roots.Count == 0;
        public double RowOpacity => Available ? 1 : 0.6;
        public string RootsText => NotFound ? L.T("本机没有这个目录") : string.Join("\n", Target.Roots);

        bool _isChecked;
        public bool IsChecked { get => _isChecked; set { _isChecked = value; Raise(nameof(IsChecked)); } }

        List<FoundFile> _files = new List<FoundFile>();
        public List<FoundFile> Files
        {
            get => _files;
            set { _files = value; Raise(nameof(Files)); Raise(nameof(HasFiles)); Raise(nameof(ResultText)); Raise(nameof(ResultBrush)); }
        }
        public bool Scanned { get; set; }
        public bool HasFiles => Files.Count > 0;
        public long Size => Files.Sum(f => f.Size);
        public string ResultText => !Scanned ? "" : HasFiles ? FsUtil.HumanSize(Size) : L.T("很干净");
        public Brush ResultBrush => (Brush)Application.Current.FindResource(HasFiles ? "Warn" : "Good");

        public event PropertyChangedEventHandler? PropertyChanged;
        void Raise(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    public partial class CleanPage : UserControl, IPage
    {
        readonly List<TargetRow> _rows;
        CancellationTokenSource? _cts;

        public CleanPage()
        {
            InitializeComponent();
            bool admin = SysInfo.IsAdmin();
            _rows = Cleaner.DefaultTargets().Select(t => new TargetRow(t, admin)).ToList();
            TargetList.ItemsSource = _rows;
            foreach (var r in _rows)
                r.PropertyChanged += (s, e) =>
                {
                    if (e.PropertyName == nameof(TargetRow.IsChecked) && _rows.Any(x => x.Scanned)) UpdateSummary();
                };
        }

        MainWindow Main => (MainWindow)Application.Current.MainWindow;

        public void OnShown() { RefreshBin(); RefreshLogs(); }

        int _binVersion;

        async void RefreshBin()
        {
            int v = ++_binVersion;
            BinText.Text = L.T("正在统计回收站…");
            var info = await Task.Run(() => RecycleBin.Query());
            if (v != _binVersion) return;
            BinText.Text = info.HasValue
                ? L.F("{0} 个项目，占用 {1}。清空后无法恢复。", info.Value.Count, FsUtil.HumanSize(info.Value.Bytes))
                : L.T("无法读取回收站信息");
        }

        void SetBusy(bool busy)
        {
            ProgressPanel.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            StopButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            ScanButton.IsEnabled = CleanButton.IsEnabled = !busy;
            TargetList.IsEnabled = !busy;
        }

        async void Scan_Click(object sender, RoutedEventArgs e)
        {
            var rows = _rows.Where(r => r.IsChecked && r.Available).ToList();
            if (rows.Count == 0) { Dialog.Info(Main, L.T("提示"), L.T("请至少勾选一个项目。")); return; }

            foreach (var r in _rows) { r.Scanned = false; r.Files = new List<FoundFile>(); }
            SetBusy(true);
            CleanButton.Visibility = Visibility.Collapsed;
            Headline.Text = L.T("正在扫描…");
            _cts = new CancellationTokenSource();
            var progress = new Progress<string>(s => ProgressText.Text = s);
            var targets = rows.Select(r => r.Target).ToList();
            try
            {
                var token = _cts.Token;
                var found = await Task.Run(() => Cleaner.Scan(targets, progress, token));
                foreach (var r in rows)
                {
                    r.Scanned = true;
                    r.Files = found.Where(f => f.TargetKey == r.Target.Key).ToList();
                }
                UpdateSummary();
            }
            catch (OperationCanceledException)
            {
                Headline.Text = L.T("扫描已停止");
            }
            finally
            {
                SetBusy(false);
            }
        }

        void UpdateSummary()
        {
            var selected = _rows.Where(r => r.IsChecked && r.HasFiles).ToList();
            long total = _rows.Where(r => r.Scanned).Sum(r => r.Size);
            Headline.Text = total > 0 ? L.F("发现 {0} 可清理文件", FsUtil.HumanSize(total)) : L.T("扫描完成 电脑很干净");
            Subline.Text = total > 0
                ? L.F("已勾选 {0} 个文件。到目前为止没有删除任何东西，点“立即清理”后还会再确认一次。", selected.Sum(r => r.Files.Count))
                : L.T("没有找到可清理的文件");
            CleanButton.Visibility = total > 0 ? Visibility.Visible : Visibility.Collapsed;
            ExportButton.IsEnabled = total > 0;
            Main.SetStatus(Headline.Text);
        }

        void Stop_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

        void Detail_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button b && b.Tag is TargetRow row)
                new FileListWindow(row.Target.Name, row.Files) { Owner = Main }.ShowDialog();
        }

        void Export_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new SaveFileDialog { FileName = L.T("清理清单.txt"), Filter = L.T("文本文件|*.txt") };
            if (dlg.ShowDialog(Main) != true) return;
            using (var w = new StreamWriter(dlg.FileName, false, new System.Text.UTF8Encoding(true)))
            {
                w.WriteLine(L.T("状态\t字节数\t路径"));
                foreach (var r in _rows.Where(r => r.Scanned))
                    foreach (var f in r.Files)
                        w.WriteLine($"{(r.IsChecked ? L.T("将删除") : L.T("保留"))}\t{f.Size}\t{f.Path}");
            }
            Main.SetStatus(L.F("清单已导出到 {0}", dlg.FileName));
        }

        async void Clean_Click(object sender, RoutedEventArgs e)
        {
            var rows = _rows.Where(r => r.IsChecked && r.HasFiles).ToList();
            if (rows.Count == 0) { Dialog.Info(Main, L.T("提示"), L.T("没有勾选任何有文件的项目。")); return; }
            var files = rows.SelectMany(r => r.Files).ToList();
            string detail = string.Join("\n", rows.Select(r => L.F("· {0}：{1} 个文件（{2}）", r.Target.Name, r.Files.Count, FsUtil.HumanSize(r.Size))));
            if (!Dialog.Confirm(Main, L.T("确认清理"),
                    L.F("将永久删除 {0} 个文件，共 {1}：\n\n{2}\n\n正在使用的文件会自动跳过。", files.Count, FsUtil.HumanSize(files.Sum(f => f.Size)), detail),
                    L.T("确认清理")))
                return;

            SetBusy(true);
            var progress = new Progress<string>(s => ProgressText.Text = s);
            try
            {
                var result = await Task.Run(() => Cleaner.Delete(files, progress));
                string msg = L.F("已删除 {0} 个文件，释放 {1}。", result.Deleted, FsUtil.HumanSize(result.Freed));
                if (result.Skipped.Count > 0) msg += L.F("\n跳过 {0} 个（多为正在使用或无权限），详情见操作日志。", result.Skipped.Count);
                foreach (var r in rows) r.Files = new List<FoundFile>();
                UpdateSummary();
                Headline.Text = L.F("清理完成 释放了 {0}", FsUtil.HumanSize(result.Freed));
                Dialog.Info(Main, L.T("清理完成"), msg);
            }
            finally
            {
                SetBusy(false);
            }
        }

        void OpenBin_Click(object sender, RoutedEventArgs e)
        {
            try { System.Diagnostics.Process.Start("explorer.exe", "shell:RecycleBinFolder"); }
            catch (Exception) { }
        }

        async void EmptyBin_Click(object sender, RoutedEventArgs e)
        {
            var info = await Task.Run(() => RecycleBin.Query());
            if (info.HasValue && info.Value.Count == 0) { Dialog.Info(Main, L.T("回收站"), L.T("回收站已经是空的。")); return; }
            if (!Dialog.Confirm(Main, L.T("清空回收站"), L.T("回收站里的文件清空后无法恢复。确定清空吗？"), L.T("清空"))) return;
            BinText.Text = L.T("正在清空回收站…");
            if (!await Sta.Run(() => RecycleBin.Empty())) Dialog.Warn(Main, L.T("回收站"), L.T("没有完全清空，可能有文件正在使用。"));
            RefreshBin();
        }

        bool _sxsBusy;
        ComponentStoreInfo? _sxs;

        bool NeedAdmin()
        {
            if (SysInfo.IsAdmin()) return false;
            Dialog.Warn(Main, L.T("权限不足"), L.T("清理系统组件旧版本需要管理员权限。\n\n请点底部的“以管理员身份重新打开”。"));
            return true;
        }

        void SetSxsBusy(bool busy, string text)
        {
            _sxsBusy = busy;
            SxsAnalyze.IsEnabled = SxsClean.IsEnabled = !busy;
            SxsBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            SxsText.Text = text;
        }

        string SxsSummary(ComponentStoreInfo i)
        {
            string s = L.F("系统组件共占用 {0}，其中可以清理约 {1}（被替换的旧组件 {2} · 缓存 {3}", FsUtil.HumanSize(i.ActualSize),
                           FsUtil.HumanSize(i.Estimated), FsUtil.HumanSize(i.Backups), FsUtil.HumanSize(i.Cache)) +
                       (i.ReclaimablePackages > 0 ? L.F(" · {0} 个可回收的旧组件包", i.ReclaimablePackages) : "") + L.T("）。");
            if (i.LastCleanup.Length > 0) s += L.F("上次清理：{0}。", i.LastCleanup);
            if (!i.Recommended) s += L.T("Windows 认为现在不需要清理。");
            return s;
        }

        async void SxsAnalyze_Click(object sender, RoutedEventArgs e)
        {
            if (_sxsBusy || NeedAdmin()) return;
            SxsBar.IsIndeterminate = true;
            SetSxsBusy(true, L.T("正在分析系统组件，要一两分钟…"));
            string text;
            try
            {
                _sxs = await Task.Run(() => WindowsResidue.AnalyzeComponentStore());
                text = SxsSummary(_sxs);
                SxsSize.Text = FsUtil.HumanSize(_sxs.Estimated);
                SxsClean.Visibility = Visibility.Visible;
            }
            catch (Exception ex) { text = L.T("没有分析成功：") + ex.Message; }
            SetSxsBusy(false, text);
        }

        async void SxsClean_Click(object sender, RoutedEventArgs e)
        {
            if (_sxsBusy || NeedAdmin()) return;
            if (!Dialog.Confirm(Main, L.T("清理系统组件旧版本"),
                    L.T("将用 Windows 自带的 DISM 删除被新版本替换下来的系统组件。\n\n") +
                    L.T("这些旧版本已经不再使用，和“磁盘清理”里的“Windows 更新清理”是同一个操作。\n\n可能要十几分钟，期间电脑会有点慢，请不要关机。"),
                    L.T("开始清理")))
                return;
            long before = _sxs?.ActualSize ?? 0;
            SxsBar.IsIndeterminate = false;
            SxsBar.Value = 0;
            SetSxsBusy(true, L.T("正在清理系统组件，可能要十几分钟，请不要关机…"));
            var progress = new Progress<double>(v =>
            {
                if (!_sxsBusy || SxsBar.IsIndeterminate) return;
                SxsBar.Value = v;
                SxsText.Text = L.F("正在清理系统组件 {0:0}%，请不要关机…", v);
            });
            string text;
            try
            {
                await Task.Run(() => WindowsResidue.CleanupComponentStore(progress));
                SxsBar.IsIndeterminate = true;
                SxsText.Text = L.T("清理完成，正在重新统计…");
                _sxs = await Task.Run(() => WindowsResidue.AnalyzeComponentStore());
                long freed = Math.Max(0, before - _sxs.ActualSize);
                text = (before > 0 ? L.F("清理完成，空出约 {0}。", FsUtil.HumanSize(freed)) : L.T("清理完成。")) + SxsSummary(_sxs);
                SxsSize.Text = FsUtil.HumanSize(_sxs.Estimated);
                Main.SetStatus(L.T("系统组件旧版本已清理"));
            }
            catch (Exception ex) { text = L.T("没有清理成功：") + ex.Message; }
            SetSxsBusy(false, text);
        }

        void RefreshLogs()
        {
            var files = LogFiles.List();
            LogSize.Text = files.Count == 0 ? "" : FsUtil.HumanSize(files.Sum(f => f.Size));
        }

        void Logs_Click(object sender, RoutedEventArgs e)
        {
            new LogWindow { Owner = Main }.ShowDialog();
            RefreshLogs();
        }
    }
}
