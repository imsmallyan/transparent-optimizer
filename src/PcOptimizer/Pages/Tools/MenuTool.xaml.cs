using System;
using System.Collections.Generic;
using System.Linq;
using System.Security;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using PcOptimizer.Core;

namespace PcOptimizer.Pages.Tools
{
    public partial class MenuTool : UserControl, IPage
    {
        List<MenuItemEntry> _all = new List<MenuItemEntry>();

        public MenuTool()
        {
            InitializeComponent();
        }

        MainWindow Main => (MainWindow)Application.Current.MainWindow;

        public void OnShown() => _ = Refresh();

        async Task Refresh()
        {
            Headline.Text = L.T("正在读取右键菜单…");
            _all = await Task.Run(() => ContextMenus.List());
            ApplyFilter();
        }

        void ApplyFilter()
        {
            var list = _all.Where(i => ShowSystem.IsChecked == true || !i.IsSystem).ToList();
            ItemList.ItemsSource = list;
            EmptyCard.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            var third = _all.Where(i => !i.IsSystem).ToList();
            Headline.Text = L.F("软件添加了 {0} 个右键菜单项", third.Count);
            int hidden = _all.Count(i => !i.Enabled);
            Summary.Text = hidden > 0 ? L.F("已隐藏 {0} 个", hidden) : "";
            SystemLabel.Text = L.F("显示 Windows 自带的（{0}）", _all.Count(i => i.IsSystem));
        }

        void Filter_Changed(object sender, RoutedEventArgs e) => ApplyFilter();
        async void Refresh_Click(object sender, RoutedEventArgs e) => await Refresh();

        void Toggle_Click(object sender, RoutedEventArgs e)
        {
            if (!(sender is CheckBox cb) || !(cb.Tag is MenuItemEntry item)) return;
            bool want = !item.Enabled;
            cb.IsChecked = item.Enabled;
            if (!want && item.IsSystem &&
                !Dialog.Confirm(Main, L.T("隐藏 Windows 自带的菜单项"),
                    L.F("“{0}”是 Windows 自带的。隐藏后右键菜单里就看不到它了，随时可以在这里重新打开。", item.Title), L.T("隐藏")))
                return;
            try
            {
                ContextMenus.SetEnabled(item, want);
                Main.SetStatus((want ? L.T("已恢复") : L.T("已隐藏")) + L.F("右键菜单项“{0}”", item.Title) +
                               (item.Kind == MenuKind.Handler ? L.T("，重新打开资源管理器窗口后生效。") : ""));
                ApplyFilter();
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException || ex is SecurityException)
            {
                Dialog.Warn(Main, L.T("权限不足"), L.F("“{0}”是为所有用户注册的，修改需要管理员权限。\n\n请点底部的“以管理员身份重新打开”。", item.Title));
            }
            catch (Exception ex)
            {
                Dialog.Warn(Main, L.T("没有改成功"), ex.Message);
            }
        }
    }
}
