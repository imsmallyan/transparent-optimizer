using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using PcOptimizer.Core;

namespace PcOptimizer.Pages.Tools
{
    public partial class DedupTool : UserControl
    {
        readonly ObservableCollection<string> _folders = new ObservableCollection<string>();
        List<DupGroup> _groups = new List<DupGroup>();
        CancellationTokenSource? _cts;

        public DedupTool()
        {
            InitializeComponent();
            FolderList.ItemsSource = _folders;
            _folders.CollectionChanged += (s, e) => NoFolderText.Visibility = _folders.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        MainWindow Main => (MainWindow)Application.Current.MainWindow;

        void AddFolder_Click(object sender, RoutedEventArgs e)
        {
            using (var dlg = new System.Windows.Forms.FolderBrowserDialog { Description = L.T("选择要查找重复文件的文件夹") })
            {
                if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
                string path = dlg.SelectedPath;
                if (_folders.Any(f => FsUtil.IsWithin(path, f))) return;
                foreach (var inner in _folders.Where(f => FsUtil.IsWithin(f, path)).ToList()) _folders.Remove(inner);
                _folders.Add(path);
            }
        }

        void RemoveFolder_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button b && b.Tag is string f) _folders.Remove(f);
        }

        async void Scan_Click(object sender, RoutedEventArgs e)
        {
            if (_folders.Count == 0) { Dialog.Info(Main, L.T("还没有选文件夹"), L.T("请先点“添加文件夹”，选择要查找的位置。")); return; }
            long min = long.Parse((string)((ComboBoxItem)MinSize.SelectedItem).Tag);
            var roots = _folders.ToList();

            _cts = new CancellationTokenSource();
            ScanButton.IsEnabled = DeleteButton.IsEnabled = false;
            ProgressPanel.Visibility = Visibility.Visible;
            Progress.IsIndeterminate = true;
            GroupList.ItemsSource = null;
            Summary.Text = "";
            var progress = new Progress<DedupProgress>(p =>
            {
                if (p.Total > 0)
                {
                    Progress.IsIndeterminate = false;
                    Progress.Value = 100.0 * p.Done / p.Total;
                    ProgressText.Text = L.F("{0}：{1} / {2}", p.Stage, FsUtil.HumanSize(p.Done), FsUtil.HumanSize(p.Total));
                }
                else ProgressText.Text = L.F("{0}：已找到 {1} 个文件", p.Stage, p.Done);
            });
            try
            {
                var token = _cts.Token;
                _groups = await Task.Run(() => Dedup.Find(roots, min, progress, token));
                ShowResults();
            }
            catch (OperationCanceledException) { Summary.Text = L.T("已停止。"); }
            finally
            {
                ProgressPanel.Visibility = Visibility.Collapsed;
                ScanButton.IsEnabled = true;
                _cts = null;
            }
        }

        void Stop_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

        void ShowResults()
        {
            GroupList.ItemsSource = _groups;
            long wasted = _groups.Sum(g => g.Size * (g.Files.Count - 1));
            Headline.Text = _groups.Count == 0 ? L.T("没有找到重复文件") : L.F("找到 {0} 组重复文件 可省出 {1}", _groups.Count, FsUtil.HumanSize(wasted));
            UpdateSummary();
        }

        void File_Checked(object sender, RoutedEventArgs e) => UpdateSummary();

        void UpdateSummary()
        {
            var sel = _groups.SelectMany(g => g.Files).Where(f => f.Checked).ToList();
            int allChecked = _groups.Count(g => g.Files.All(f => f.Checked));
            Summary.Text = _groups.Count == 0 ? "" :
                L.F("已勾选 {0} 个文件 · 共 {1}", sel.Count, FsUtil.HumanSize(sel.Sum(f => f.Size))) +
                (allChecked > 0 ? L.F("。注意：有 {0} 组全部勾选了，这些组会整组跳过，至少要留一个。", allChecked) : "");
            DeleteButton.IsEnabled = sel.Count > 0;
        }

        void Reveal_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button b && b.Tag is string path) MainWindow.Reveal(path);
        }

        async void Delete_Click(object sender, RoutedEventArgs e)
        {
            var sel = _groups.SelectMany(g => g.Files).Where(f => f.Checked).ToList();
            if (sel.Count == 0) return;
            var full = _groups.Where(g => g.Files.All(f => f.Checked)).ToList();
            if (full.Count > 0)
            {
                Dialog.Warn(Main, L.T("每组至少留一个"), L.F("有 {0} 组的文件全部勾选了，比如：\n{1}\n\n每组请至少留一个不勾选，否则这份内容就一个都不剩了。", full.Count, full[0].Files[0].Path));
                return;
            }
            string list = string.Join("\n", sel.Take(6).Select(f => "· " + f.Path)) + (sel.Count > 6 ? L.F("\n……共 {0} 个", sel.Count) : "");
            if (!Dialog.Confirm(Main, L.T("删除重复文件"),
                    L.F("将把以下文件移到回收站（共 {0}）：\n\n{1}\n\n", FsUtil.HumanSize(sel.Sum(f => f.Size)), list) +
                    L.T("每组至少保留一个未勾选的文件。删除前会重新计算 MD5，扫描后被改动过的文件会跳过。"),
                    L.T("移到回收站")))
                return;

            DeleteButton.IsEnabled = ScanButton.IsEnabled = false;
            var groups = _groups.ToList();
            var r = await Sta.Run(() => Dedup.Delete(groups));
            ScanButton.IsEnabled = true;

            foreach (var g in _groups)
                g.Files.RemoveAll(f => f.Checked && !System.IO.File.Exists(f.Path));
            _groups = _groups.Where(g => g.Files.Count > 1).ToList();
            ShowResults();

            string msg = L.F("已把 {0} 个文件移到回收站，省出 {1}。", r.Removed, FsUtil.HumanSize(r.Bytes));
            if (r.Failed.Count > 0) Dialog.Warn(Main, L.T("部分文件没有删除"), msg + L.F("\n\n有 {0} 个没有删除：\n", r.Failed.Count) + string.Join("\n", r.Failed.Take(6)));
            Main.SetStatus(msg);
        }
    }
}
