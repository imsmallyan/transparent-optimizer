using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using PcOptimizer.Core;

namespace PcOptimizer.Pages.Tools
{
    public class ShredItem
    {
        public string Path { get; set; } = "";
        public bool IsFolder { get; set; }
        public int FileCount { get; set; }
        public long Size { get; set; }
        public string Icon => IsFolder ? "" : "";
        public string SizeText => IsFolder ? L.F("{0} 个文件 · {1}", FileCount, FsUtil.HumanSize(Size)) : FsUtil.HumanSize(Size);
    }

    public partial class ShredTool : UserControl, IFileDropTarget
    {
        readonly ObservableCollection<ShredItem> _items = new ObservableCollection<ShredItem>();
        CancellationTokenSource? _cts;

        public ShredTool()
        {
            InitializeComponent();
            ItemList.ItemsSource = _items;
            _items.CollectionChanged += (s, e) => UpdateSummary();
        }

        MainWindow Main => (MainWindow)Application.Current.MainWindow;

        void AddFiles_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Title = L.T("选择要粉碎的文件") };
            if (dlg.ShowDialog(Main) == true) Add(dlg.FileNames);
        }

        void AddFolder_Click(object sender, RoutedEventArgs e)
        {
            using (var dlg = new System.Windows.Forms.FolderBrowserDialog { Description = L.T("选择要粉碎的文件夹（里面的所有文件都会被粉碎）") })
                if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK) Add(new[] { dlg.SelectedPath });
        }

        void OnDragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        void OnDrop(object sender, DragEventArgs e)
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) Add(paths);
            e.Handled = true;
        }

        public void AcceptFiles(string[] paths) => Add(paths);

        void Add(string[] paths)
        {
            var refused = new System.Collections.Generic.List<string>();
            foreach (var raw in paths)
            {
                string p = System.IO.Path.GetFullPath(raw);
                if (_items.Any(i => string.Equals(i.Path, p, StringComparison.OrdinalIgnoreCase) || (i.IsFolder && FsUtil.IsWithin(p, i.Path)))) continue;
                if (Shredder.CheckAllowed(p) is string why) { refused.Add(L.F("{0}：{1}", p, why)); continue; }
                var item = new ShredItem { Path = p, IsFolder = Directory.Exists(p) };
                var files = Shredder.Expand(new[] { p });
                item.FileCount = files.Count;
                item.Size = files.Sum(f => { try { return f.Length; } catch (Exception) { return 0L; } });
                if (item.IsFolder) foreach (var inner in _items.Where(i => FsUtil.IsWithin(i.Path, p)).ToList()) _items.Remove(inner);
                _items.Add(item);
            }
            if (refused.Count > 0) Dialog.Warn(Main, L.T("有些不能粉碎"), string.Join("\n", refused.Take(6)));
        }

        void Remove_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button b && b.Tag is ShredItem i) _items.Remove(i);
        }

        void UpdateSummary()
        {
            int files = _items.Sum(i => i.FileCount);
            Summary.Text = _items.Count == 0 ? L.T("把文件或文件夹拖到这里，或点右边的按钮添加。")
                                             : L.F("共 {0} 个文件 · {1}", files, FsUtil.HumanSize(_items.Sum(i => i.Size)));
            DropCard.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            ShredButton.IsEnabled = _items.Count > 0 && _cts == null;
        }

        void Stop_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

        async void Shred_Click(object sender, RoutedEventArgs e)
        {
            if (_items.Count == 0) return;
            int files = _items.Sum(i => i.FileCount);
            string list = string.Join("\n", _items.Take(6).Select(i => "· " + i.Path)) + (_items.Count > 6 ? L.F("\n……共 {0} 项", _items.Count) : "");
            if (!Dialog.Confirm(Main, L.T("粉碎文件（无法恢复）"),
                    L.F("将彻底删除以下 {0} 个文件（{1}），不进回收站，任何工具都找不回来：\n\n{2}", files, FsUtil.HumanSize(_items.Sum(i => i.Size)), list),
                    L.T("确定粉碎")))
                return;

            int passes = int.Parse((string)((ComboBoxItem)Passes.SelectedItem).Tag);
            var paths = _items.Select(i => i.Path).ToList();
            _cts = new CancellationTokenSource();
            ShredButton.IsEnabled = false;
            ProgressPanel.Visibility = Visibility.Visible;
            var progress = new Progress<(long Done, long Total, string Current)>(p =>
            {
                Progress.Value = p.Total > 0 ? 100.0 * p.Done / p.Total : 0;
                ProgressText.Text = L.F("正在粉碎：{0}（{1} / {2}）", System.IO.Path.GetFileName(p.Current), FsUtil.HumanSize(p.Done), FsUtil.HumanSize(p.Total));
            });
            ShredResult? r = null;
            bool stopped = false;
            try
            {
                var token = _cts.Token;
                r = await Task.Run(() => Shredder.Shred(paths, passes, progress, token));
            }
            catch (OperationCanceledException) { stopped = true; }
            finally
            {
                _cts = null;
                ProgressPanel.Visibility = Visibility.Collapsed;
            }

            foreach (var i in _items.ToList())
            {
                if (!File.Exists(i.Path) && !Directory.Exists(i.Path)) _items.Remove(i);
                else
                {
                    var left = Shredder.Expand(new[] { i.Path });
                    i.FileCount = left.Count;
                    i.Size = left.Sum(f => f.Length);
                }
            }
            ItemList.ItemsSource = null;
            ItemList.ItemsSource = _items;
            UpdateSummary();

            if (stopped) { Dialog.Warn(Main, L.T("已停止"), L.T("已经粉碎的文件无法恢复；正在处理的那个文件内容可能已被部分覆盖。列表里剩下的是还没粉碎的。")); return; }
            string msg = L.F("已粉碎 {0} 个文件（{1}）。", r!.Files, FsUtil.HumanSize(r.Bytes));
            if (r.Failed.Count > 0) Dialog.Warn(Main, L.T("部分文件没有粉碎"), msg + L.F("\n\n有 {0} 个没有粉碎：\n", r.Failed.Count) + string.Join("\n", r.Failed.Take(6)));
            Main.SetStatus(msg);
        }
    }
}
