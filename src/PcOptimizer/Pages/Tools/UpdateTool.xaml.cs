using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using PcOptimizer.Core;

namespace PcOptimizer.Pages.Tools
{
    public partial class UpdateTool : UserControl, IPage
    {
        List<SoftwareUpdate> _all = new List<SoftwareUpdate>();
        bool _checked, _busy;

        public UpdateTool()
        {
            InitializeComponent();
            UpdateSelection();
        }

        MainWindow Main => (MainWindow)Application.Current.MainWindow;

        public void OnShown()
        {
            if (!_checked) _ = Check();
        }

        async Task Check()
        {
            if (_busy) return;
            SetBusy(true);
            Headline.Text = L.T("正在检查更新…");
            Summary.Text = L.T("winget 正在查找新版本，第一次可能要等一会儿。");
            MessageCard.Visibility = Visibility.Collapsed;
            InstallWinget.Visibility = Visibility.Collapsed;
            ItemList.ItemsSource = null;
            try
            {
                _all = await Task.Run(() => Winget.ListUpdates());
                _checked = true;
                Headline.Text = _all.Count > 0 ? L.F("有 {0} 个软件可以更新", _all.Count) : L.T("软件都是最新的");
                if (_all.Count == 0) ShowMessage(L.T("winget 没有找到可以更新的软件。不是通过 winget 或安装程序登记的软件（比如绿色版）查不到。"));
            }
            catch (FileNotFoundException)
            {
                _all = new List<SoftwareUpdate>();
                Headline.Text = L.T("软件更新");
                ShowMessage(L.T("这台电脑上没有 winget。它是 Windows 10 和 11 自带的“应用安装程序”的一部分，可以从应用商店免费安装或更新。"));
                InstallWinget.Visibility = Visibility.Visible;
            }
            catch (Exception ex)
            {
                _all = new List<SoftwareUpdate>();
                Headline.Text = L.T("软件更新");
                ShowMessage(L.T("检查更新失败：") + ex.Message);
            }
            finally { SetBusy(false); }
            ItemList.ItemsSource = _all;
            UpdateSelection();
        }

        void ShowMessage(string text)
        {
            MessageText.Text = text;
            MessageCard.Visibility = Visibility.Visible;
        }

        void SetBusy(bool busy)
        {
            _busy = busy;
            CheckButton.IsEnabled = !busy;
            UpdateSelection();
        }

        void UpdateSelection()
        {
            int n = _all.Count(u => u.Selected && u.CanSelect);
            if (!_busy) Summary.Text = _all.Count == 0 ? "" : n > 0 ? L.F("已选 {0} 个", n) : L.T("勾选要更新的软件");
            UpdateButton.IsEnabled = n > 0 && !_busy;
            SelectAll.Visibility = !_busy && _all.Any(u => u.CanSelect && !u.Selected) ? Visibility.Visible : Visibility.Collapsed;
        }

        void Select_Click(object sender, RoutedEventArgs e) => UpdateSelection();
        async void Check_Click(object sender, RoutedEventArgs e) => await Check();

        void SelectAll_Click(object sender, RoutedEventArgs e)
        {
            foreach (var u in _all.Where(u => u.CanSelect)) u.Selected = true;
            UpdateSelection();
        }

        async void Update_Click(object sender, RoutedEventArgs e)
        {
            var todo = _all.Where(u => u.Selected && u.CanSelect).ToList();
            if (todo.Count == 0 || _busy) return;
            string msg = L.F("将用 winget 更新下面 {0} 个软件：\n\n", todo.Count) + string.Join("\n", todo.Select(u => $"· {u.Name}  {u.VersionText}")) +
                         L.T("\n\n安装程序会在后台静默运行。正在用的软件请先保存好内容，有的软件更新时会被关掉。");
            if (!Dialog.Confirm(Main, L.T("更新软件"), msg, L.T("开始更新"))) return;

            SetBusy(true);
            int done = 0, failed = 0;
            foreach (var u in todo)
            {
                u.State = UpdateState.Updating;
                Summary.Text = L.F("正在更新“{0}”… ({1}/{2})", u.Name, done + failed + 1, todo.Count);
                Main.SetStatus(Summary.Text);
                string? error = await Task.Run(() => Winget.Upgrade(u));
                if (error == null) { u.State = UpdateState.Done; u.Selected = false; done++; }
                else { u.Message = error; u.State = UpdateState.Failed; failed++; }
            }
            SetBusy(false);
            Main.SetStatus(failed == 0 ? L.F("已更新 {0} 个软件。", done) : L.F("已更新 {0} 个软件，{1} 个没有成功，原因写在各自右边。", done, failed));
        }

        void InstallWinget_Click(object sender, RoutedEventArgs e)
        {
            try { Process.Start(new ProcessStartInfo(Winget.StoreLink) { UseShellExecute = true }); }
            catch (Exception ex) { Dialog.Warn(Main, L.T("打不开应用商店"), ex.Message); }
        }
    }
}
