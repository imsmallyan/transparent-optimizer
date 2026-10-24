using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using PcOptimizer.Core;

namespace PcOptimizer.Pages
{
    public partial class PrivacyPage : UserControl, IPage
    {
        List<PrivacyItem> _items = new List<PrivacyItem>();
        bool _needRestart;

        public PrivacyPage()
        {
            InitializeComponent();
        }

        MainWindow Main => (MainWindow)Application.Current.MainWindow;

        public void OnShown() => Refresh();

        void Refresh()
        {
            bool admin = SysInfo.IsAdmin();
            _items = Privacy.List();
            foreach (var i in _items) i.AdminTag = i.NeedsAdmin && !admin;
            var view = new ListCollectionView(_items);
            view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(PrivacyItem.Group)));
            ItemList.ItemsSource = view;
            UpdateSummary();
        }

        void UpdateSummary()
        {
            int off = _items.Count(i => i.State == PrivacyState.Off);
            int left = _items.Count(i => i.Recommended && i.State != PrivacyState.Off);
            Summary.Text = L.F("已关闭 {0} 项 · 共 {1} 项", off, _items.Count) +
                           (left > 0 ? L.F(" · 还有 {0} 项推荐关闭", left) : L.T(" · 推荐的都已关闭"));
            RestartButton.Visibility = _needRestart ? Visibility.Visible : Visibility.Collapsed;
        }

        void Toggle_Click(object sender, RoutedEventArgs e)
        {
            if (!(sender is CheckBox cb) || !(cb.Tag is PrivacyItem item)) return;
            bool turnOff = item.State != PrivacyState.Off;
            cb.SetCurrentValue(ToggleButton.IsCheckedProperty, item.IsOn);
            if (Apply(new[] { item }, turnOff) == 0) return;
            Main.SetStatus((turnOff ? L.F("已关闭“{0}”", item.Title) : L.F("“{0}”已恢复默认", item.Title)) +
                           (item.RestartExplorer ? L.T("，重启资源管理器后生效。") : L.T("。")));
        }

        void TurnOffRecommended_Click(object sender, RoutedEventArgs e)
        {
            bool admin = SysInfo.IsAdmin();
            var todo = _items.Where(i => i.Recommended && i.State != PrivacyState.Off).ToList();
            var skipped = admin ? new List<PrivacyItem>() : todo.Where(i => i.NeedsAdmin).ToList();
            todo = todo.Except(skipped).ToList();
            if (todo.Count == 0)
            {
                Dialog.Info(Main, L.T("没有要关闭的"), skipped.Count > 0
                    ? L.F("剩下的 {0} 项需要管理员权限。请点底部的“以管理员身份重新打开”。", skipped.Count)
                    : L.T("推荐关闭的项目都已经关闭了。"));
                return;
            }
            string msg = L.F("将关闭下面 {0} 项，随时可以在这里恢复默认：\n\n", todo.Count) + string.Join("\n", todo.Select(i => "· " + i.Title));
            if (skipped.Count > 0) msg += L.F("\n\n另有 {0} 项需要管理员权限，这次先跳过。", skipped.Count);
            if (!Dialog.Confirm(Main, L.T("一键关闭推荐项"), msg, L.T("确定关闭"))) return;
            int done = Apply(todo, true);
            Main.SetStatus(L.F("已关闭 {0} 项", done) + (_needRestart ? L.T("，有几项要重启资源管理器后生效。") : L.T("。")));
        }

        void RestoreAll_Click(object sender, RoutedEventArgs e)
        {
            var todo = _items.Where(i => i.State != PrivacyState.Default).ToList();
            if (todo.Count == 0) { Main.SetStatus(L.T("所有项目都是 Windows 默认设置。")); return; }
            if (!Dialog.Confirm(Main, L.T("全部恢复默认"), L.F("把这 {0} 项改回 Windows 的默认设置？", todo.Count), L.T("恢复默认"))) return;
            int done = Apply(todo, false);
            Main.SetStatus(L.F("已恢复 {0} 项", done) + L.T("。"));
        }

        int Apply(IEnumerable<PrivacyItem> items, bool turnOff)
        {
            int done = 0;
            var denied = new List<string>();
            var failed = new List<string>();
            foreach (var item in items)
            {
                try
                {
                    Privacy.Set(item, turnOff);
                    done++;
                    if (item.RestartExplorer) _needRestart = true;
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException || ex is SecurityException) { denied.Add(item.Title); }
                catch (Exception ex) { failed.Add(item.Title + L.T("：") + ex.Message); }
            }
            UpdateSummary();
            if (denied.Count > 0)
                Dialog.Warn(Main, L.T("权限不足"), L.F("下面几项是对所有用户生效的，修改需要管理员权限：\n\n{0}\n\n请点底部的“以管理员身份重新打开”。",
                    string.Join("\n", denied.Select(t => "· " + t))));
            if (failed.Count > 0) Dialog.Warn(Main, L.T("没有改成功"), string.Join("\n", failed));
            return done;
        }

        async void RestartExplorer_Click(object sender, RoutedEventArgs e)
        {
            if (!Dialog.Confirm(Main, L.T("重启资源管理器"),
                L.T("任务栏和桌面会消失几秒再出现，打开的文件夹窗口会关闭。正在复制的文件请等复制完再重启。"), L.T("重启"))) return;
            try
            {
                foreach (var p in Process.GetProcessesByName("explorer")) { try { p.Kill(); } catch (Exception) { } }
                await Task.Delay(2500);
                if (Process.GetProcessesByName("explorer").Length == 0) Process.Start("explorer.exe");
                _needRestart = false;
                UpdateSummary();
                OpLog.Write("EXPLORER_RESTART", "");
                Main.SetStatus(L.T("资源管理器已重启。"));
            }
            catch (Exception ex)
            {
                Dialog.Warn(Main, L.T("没有重启成功"), ex.Message);
            }
        }
    }
}
