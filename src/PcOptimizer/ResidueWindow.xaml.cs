using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using PcOptimizer.Core;

namespace PcOptimizer
{
    public partial class ResidueWindow : Window
    {
        readonly InstalledProgram _program;
        readonly List<InstalledProgram> _remaining;
        List<ResidueItem> _items = new List<ResidueItem>();

        public ResidueWindow(InstalledProgram removed, IEnumerable<InstalledProgram> remaining)
        {
            InitializeComponent();
            UiText.Attach(this);
            Theme.Track(this);
            Language = App.XmlLang;
            _program = removed;
            _remaining = remaining.Where(p => p.KeyPath != removed.KeyPath || p.Hive != removed.Hive).ToList();
            Title = L.F("残留清理 - {0}", removed.Name);
            Loaded += async (s, e) => await Scan();
        }

        async Task Scan()
        {
            Headline.Text = L.F("正在查找“{0}”的残留…", _program.Name);
            _items = await Task.Run(() => Residue.Find(_program, _remaining));
            Progress.Visibility = Visibility.Collapsed;
            foreach (var it in _items) it.PropertyChanged += Item_Changed;
            ItemList.ItemsSource = _items;
            EmptyCard.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            Headline.Text = _items.Count == 0 ? L.F("“{0}”没有残留", _program.Name) : L.F("发现“{0}”的 {1} 处残留", _program.Name, _items.Count);
            UpdateSummary();
        }

        void Item_Changed(object? sender, PropertyChangedEventArgs e) => UpdateSummary();

        void UpdateSummary()
        {
            var sel = _items.Where(i => i.Checked).ToList();
            long size = sel.Where(i => i.Kind == ResidueKind.Folder).Sum(i => i.Size);
            Summary.Text = _items.Count == 0 ? "" : L.F("已勾选 {0} 项", sel.Count) + (size > 0 ? L.F(" · 文件共 {0}", FsUtil.HumanSize(size)) : "");
            CleanButton.IsEnabled = sel.Count > 0;
        }

        async void Clean_Click(object sender, RoutedEventArgs e)
        {
            var sel = _items.Where(i => i.Checked).ToList();
            if (sel.Count == 0) return;
            string list = string.Join("\n", sel.Take(8).Select(i => $"· [{i.KindText}] {i.Display}")) +
                          (sel.Count > 8 ? L.F("\n……共 {0} 项", sel.Count) : "");
            if (!Dialog.Confirm(this, L.T("确认清理残留"),
                    L.F("将清理以下内容：\n\n{0}\n\n文件夹移到回收站，注册表先备份到：\n{1}", list, RegBackup.BackupDir), L.T("确认清理")))
                return;

            CleanButton.IsEnabled = false;
            Progress.Visibility = Visibility.Visible;
            var result = await Sta.Run(() => Residue.Remove(sel, _program.Name));
            Progress.Visibility = Visibility.Collapsed;

            string msg = L.F("已清理 {0} 项。", result.Removed);
            if (result.Backups.Count > 0) msg += L.F("\n注册表备份在：{0}", RegBackup.BackupDir);
            if (result.Failed.Count > 0) msg += L.F("\n\n有 {0} 项没有清理：\n", result.Failed.Count) + string.Join("\n", result.Failed.Take(6));
            if (result.Failed.Count > 0) Dialog.Warn(this, L.T("清理完成（部分未完成）"), msg);
            else Dialog.Info(this, L.T("清理完成"), msg);
            await Scan();
        }

        void Close_Click(object sender, RoutedEventArgs e) => Close();
    }
}
