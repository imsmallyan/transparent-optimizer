using System;
using System.IO;
using System.Linq;
using System.Security;
using System.Windows;
using System.Windows.Controls;
using PcOptimizer.Core;

namespace PcOptimizer.Pages
{
    public partial class StartupPage : UserControl, IPage
    {
        public StartupPage()
        {
            InitializeComponent();
        }

        MainWindow Main => (MainWindow)Application.Current.MainWindow;

        public void OnShown() => Refresh();

        void Refresh()
        {
            var items = StartupManager.ListItems().OrderByDescending(i => i.TargetMissing).ToList();
            ItemList.ItemsSource = items;
            EmptyCard.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            int broken = items.Count(i => i.TargetMissing);
            Headline.Text = L.F("共 {0} 个启动项 {1} 个已启用", items.Count, items.Count(i => i.Enabled)) + (broken > 0 ? L.F(" {0} 个已失效", broken) : "");
            CleanBrokenButton.Content = L.F("清理失效项（{0}）", broken);
            CleanBrokenButton.Visibility = broken > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        void CleanBroken_Click(object sender, RoutedEventArgs e) =>
            RemoveItems(((System.Collections.Generic.IEnumerable<StartupItem>)ItemList.ItemsSource).Where(i => i.TargetMissing).ToList());

        void RemoveOne_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button b && b.Tag is StartupItem item) RemoveItems(new System.Collections.Generic.List<StartupItem> { item });
        }

        void RemoveItems(System.Collections.Generic.List<StartupItem> items)
        {
            if (items.Count == 0) return;
            string list = string.Join("\n", items.Take(8).Select(i => $"· {i.Name}  →  {i.TargetPath}"));
            if (!Dialog.Confirm(Main, L.T("删除失效启动项"),
                    L.F("以下启动项指向的程序已经不存在，开机时只会白白报错或什么也不做：\n\n{0}\n\n", list) +
                    L.T("注册表里的项会先备份成 .reg 文件（双击即可恢复），启动文件夹里的快捷方式会移到回收站。"),
                    L.T("删除")))
                return;
            var failed = new System.Collections.Generic.List<string>();
            foreach (var it in items)
            {
                try { StartupManager.Remove(it); }
                catch (Exception ex) when (ex is UnauthorizedAccessException || ex is SecurityException)
                { failed.Add(L.F("{0}：需要管理员权限", it.Name)); }
                catch (Exception ex) { failed.Add(L.F("{0}：{1}", it.Name, ex.Message)); }
            }
            Refresh();
            if (failed.Count > 0)
                Dialog.Warn(Main, L.T("部分未删除"), string.Join("\n", failed) + L.T("\n\n需要管理员权限的，请点底部的“以管理员身份重新打开”。"));
            else
                Main.SetStatus(L.F("已删除 {0} 个失效启动项，备份在：{1}", items.Count, RegBackup.BackupDir));
        }

        void Refresh_Click(object sender, RoutedEventArgs e) => Refresh();

        void Toggle_Click(object sender, RoutedEventArgs e)
        {
            if (!(sender is CheckBox cb) || !(cb.Tag is StartupItem item)) return;
            bool want = !item.Enabled;
            try
            {
                StartupManager.SetEnabled(item, want);
                Main.SetStatus(L.F("已{0}“{1}”，重启后生效。", (want ? L.T("启用") : L.T("禁用")), item.Name));
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException || ex is SecurityException)
            {
                Dialog.Warn(Main, L.T("权限不足"), L.F("“{0}”是所有用户共用的启动项，需要管理员权限才能修改。\n\n请点底部的“以管理员身份重新打开”。", item.Name));
            }
            catch (Exception ex)
            {
                Dialog.Warn(Main, L.T("没有改成功"), ex.Message);
            }
            cb.IsChecked = item.Enabled;
            Headline.Text = L.F("共 {0} 个启动项 ", ((System.Collections.IList)ItemList.ItemsSource).Count) +
                            L.F("{0} 个已启用", ItemList.ItemsSource.Cast<StartupItem>().Count(i => i.Enabled));
        }

        void Reveal_Click(object sender, RoutedEventArgs e)
        {
            if (!(sender is Button b) || !(b.Tag is StartupItem item)) return;
            string path = item.ApprovedSub == "StartupFolder" ? item.Command : StartupManager.ParseExePath(item.Command);
            if (File.Exists(path)) MainWindow.Reveal(path);
            else Dialog.Info(Main, L.T("找不到文件"), L.F("启动命令指向的文件不存在，可能程序已被卸载：\n\n{0}", item.Command));
        }
    }
}
