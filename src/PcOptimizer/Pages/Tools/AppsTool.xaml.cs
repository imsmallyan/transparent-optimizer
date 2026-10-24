using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using PcOptimizer.Core;

namespace PcOptimizer.Pages.Tools
{
    public partial class AppsTool : UserControl, IPage
    {
        List<StoreApp> _all = new List<StoreApp>();
        bool _loaded, _busy;

        public AppsTool()
        {
            InitializeComponent();
        }

        MainWindow Main => (MainWindow)Application.Current.MainWindow;

        public void OnShown()
        {
            if (!_loaded) _ = Refresh();
        }

        async Task Refresh()
        {
            if (_busy) return;
            _busy = true;
            Headline.Text = L.T("正在读取已安装的应用…");
            MessageCard.Visibility = Visibility.Collapsed;
            try
            {
                _all = await Task.Run(() => StoreApps.List());
                StoreApps.ForgetReinstalled(_all);
                _loaded = true;
            }
            catch (Exception ex)
            {
                _all = new List<StoreApp>();
                MessageText.Text = L.T("没有读到应用列表。这个功能需要 Windows 10 或 11 自带的 PowerShell。") + "\n\n" + ex.Message;
                MessageCard.Visibility = Visibility.Visible;
            }
            finally { _busy = false; }
            ApplyFilter();
        }

        void ApplyFilter()
        {
            bool other = ShowOther.IsChecked == true;
            var list = _all.Where(a => other || a.Advice != AppAdvice.Other).ToList();
            if (!other) foreach (var a in _all.Where(a => a.Advice == AppAdvice.Other)) a.Selected = false;
            var view = new ListCollectionView(list);
            view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(StoreApp.GroupText)));
            ItemList.ItemsSource = view;

            int known = _all.Count(a => a.Advice != AppAdvice.Other);
            Headline.Text = _loaded ? L.F("找到 {0} 个预装应用", known) : L.T("预装应用清理");
            OtherLabel.Text = L.F("显示其他应用（{0}）", _all.Count(a => a.Advice == AppAdvice.Other));
            if (_loaded && list.Count == 0 && MessageCard.Visibility != Visibility.Visible)
            {
                MessageText.Text = L.T("没有找到常见的预装应用，已经很干净了。");
                MessageCard.Visibility = Visibility.Visible;
            }
            else if (list.Count > 0) MessageCard.Visibility = Visibility.Collapsed;

            var removed = StoreApps.Removed();
            RemovedList.ItemsSource = removed;
            RemovedPanel.Visibility = removed.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            UpdateSelection();
        }

        void UpdateSelection()
        {
            int n = _all.Count(a => a.Selected);
            Summary.Text = n > 0 ? L.F("已选 {0} 个", n) : L.T("勾选要删除的应用");
            RemoveButton.IsEnabled = n > 0 && !_busy;
            SelectRecommended.Visibility = _all.Any(a => a.Advice == AppAdvice.Recommend && !a.Selected) ? Visibility.Visible : Visibility.Collapsed;
        }

        void Select_Click(object sender, RoutedEventArgs e) => UpdateSelection();
        void Filter_Changed(object sender, RoutedEventArgs e) => ApplyFilter();
        async void Refresh_Click(object sender, RoutedEventArgs e) => await Refresh();

        void SelectRecommended_Click(object sender, RoutedEventArgs e)
        {
            foreach (var a in _all.Where(a => a.Advice == AppAdvice.Recommend)) a.Selected = true;
            UpdateSelection();
        }

        async void Remove_Click(object sender, RoutedEventArgs e)
        {
            var todo = _all.Where(a => a.Selected).ToList();
            if (todo.Count == 0 || _busy) return;
            string msg = L.F("将为当前用户删除下面 {0} 个应用：\n\n", todo.Count) + string.Join("\n", todo.Select(a => "· " + a.Title)) +
                         L.T("\n\n大部分可以在应用商店里重新安装，删除记录会留在本页下方。");
            if (!Dialog.Confirm(Main, L.T("删除预装应用"), msg, L.T("删除"))) return;

            _busy = true;
            RemoveButton.IsEnabled = false;
            var failed = new List<string>();
            int done = 0;
            foreach (var app in todo)
            {
                Main.SetStatus(L.F("正在删除“{0}”… ({1}/{2})", app.Title, done + failed.Count + 1, todo.Count));
                try
                {
                    await Task.Run(() => StoreApps.Remove(app));
                    _all.Remove(app);
                    done++;
                }
                catch (Exception ex) { failed.Add(app.Title + L.T("：") + ex.Message); }
            }
            _busy = false;
            ApplyFilter();
            Main.SetStatus(L.F("已删除 {0} 个应用", done) + L.T("。"));
            if (failed.Count > 0)
                Dialog.Warn(Main, L.T("有些应用没有删掉"), string.Join("\n\n", failed) +
                    L.T("\n\n有的应用被 Windows 设为不能删除，或者正在运行。可以关掉它再试一次。"));
        }

        void Reinstall_Click(object sender, RoutedEventArgs e)
        {
            if (!(sender is Button b) || !(b.Tag is string family)) return;
            try { Process.Start(new ProcessStartInfo(StoreApps.StoreLink(family)) { UseShellExecute = true }); }
            catch (Exception ex) { Dialog.Warn(Main, L.T("打不开应用商店"), ex.Message); }
        }
    }
}
