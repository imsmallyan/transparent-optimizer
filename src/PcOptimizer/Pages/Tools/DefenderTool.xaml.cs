using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using PcOptimizer.Core;

namespace PcOptimizer.Pages.Tools
{
    public partial class DefenderTool : UserControl, IPage, IFileDropTarget
    {
        DefenderStatus? _status;
        CancellationTokenSource? _cts;
        bool _loading;

        public DefenderTool()
        {
            InitializeComponent();
        }

        MainWindow Main => (MainWindow)Application.Current.MainWindow;

        public void OnShown()
        {
            if (_cts == null) _ = Refresh();
        }

        async Task Refresh()
        {
            if (_loading) return;
            _loading = true;
            try
            {
                var (status, threats) = await Task.Run(() => (Defender.Status(), Defender.Threats()));
                _status = status;
                ShowStatus();
                ThreatList.ItemsSource = threats;
                NoThreats.Text = status.Available ? L.T("没有查杀记录，Defender 没有发现过威胁。") : L.T("读取不到 Defender 的查杀记录。");
                NoThreats.Visibility = threats.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                int open = threats.Count(t => !t.Handled);
                HistorySummary.Text = threats.Count == 0 ? "" :
                    L.F("共 {0} 条", threats.Count) + (open > 0 ? L.F("，其中 {0} 条没有处理完，请到 Windows 安全中心查看。", open) : L.T("，都已处理。"));
            }
            finally { _loading = false; }
        }

        void ShowStatus()
        {
            var s = _status!;
            string sig = s.SignatureUpdated.HasValue
                ? L.F("病毒库 {0:yyyy-MM-dd} 更新", s.SignatureUpdated.Value) + (s.SignatureAgeDays >= 7 ? L.F("（已经 {0} 天没更新了）", s.SignatureAgeDays) : "")
                : L.T("病毒库更新时间未知");
            string lastScan = new[] { s.QuickScanEnd, s.FullScanEnd }.Max() is DateTime t && t.Year > 2000 ? L.F("上次扫描 {0:yyyy-MM-dd HH:mm}", t) : L.T("还没有扫描过");
            string other = s.OtherAntivirus.Count > 0 ? L.F("已安装的杀毒软件：{0}。", string.Join(L.T("、"), s.OtherAntivirus)) : "";

            if (!s.Available)
            {
                SetState(CheckLevel.Bad, s.OtherAntivirus.Count > 0 ? L.F("防护由 {0} 负责", s.OtherAntivirus[0]) : L.T("没有读取到 Microsoft Defender"),
                    s.OtherAntivirus.Count > 0 ? other + L.T("Defender 已被它接管，这里不能用 Defender 扫描，请用它自己的扫描功能。")
                                               : L.T("Defender 可能被关闭或被删除，电脑目前可能没有任何杀毒保护。请打开 Windows 安全中心检查。"));
            }
            else if (s.Passive || !s.AntivirusOn)
            {
                SetState(s.OtherAntivirus.Count > 0 ? CheckLevel.Good : CheckLevel.Bad,
                    s.OtherAntivirus.Count > 0 ? L.F("防护由 {0} 负责", s.OtherAntivirus[0]) : L.T("Defender 的病毒防护没有开启"),
                    other + (s.OtherAntivirus.Count > 0 ? L.T("Defender 处于备用状态，仍可用它手动扫描做第二意见。") : L.T("请到 Windows 安全中心打开“实时保护”。")) + L.F(" {0}，{1}。", sig, lastScan));
            }
            else if (!s.RealTimeOn)
                SetState(CheckLevel.Bad, L.T("实时保护已关闭"), L.F("运行和下载的文件不会被自动检查。请到 Windows 安全中心打开“实时保护”。{0}，{1}。", sig, lastScan));
            else if (s.SignatureAgeDays >= 7)
                SetState(CheckLevel.Warn, L.T("实时保护已开启 但病毒库有点旧"), L.F("{0}，建议点“更新病毒库”。{1}。", sig, lastScan));
            else
                SetState(CheckLevel.Good, L.T("实时保护已开启"), L.F("{0}，{1}。", sig, lastScan) + (s.TamperProtected ? L.T("已开启防篡改保护，病毒无法偷偷关掉 Defender。") : ""));

            bool idle = _cts == null;
            QuickButton.IsEnabled = FullButton.IsEnabled = FileButton.IsEnabled = FolderButton.IsEnabled = idle && s.CanScan;
            UpdateButton.IsEnabled = idle && Defender.MpCmdRunPath() != null && s.Available;
        }

        void SetState(CheckLevel level, string title, string detail)
        {
            var brush = (Brush)FindResource(level == CheckLevel.Good ? "Good" : level == CheckLevel.Warn ? "Warn" : "Bad");
            StateIcon.Foreground = brush;
            StateIcon.Text = level == CheckLevel.Good ? "" : "";
            StateBadge.SetResourceReference(Border.BackgroundProperty, level == CheckLevel.Good ? "GoodLight" : level == CheckLevel.Warn ? "WarnLight" : "BadLight");
            StateTitle.Text = title;
            StateDetail.Text = detail;
        }

        void Quick_Click(object sender, RoutedEventArgs e) => _ = RunScan(Defender.ScanKind.Quick, null, L.T("正在快速扫描"));

        void Full_Click(object sender, RoutedEventArgs e)
        {
            if (!Dialog.Confirm(Main, L.T("全盘扫描"), L.T("全盘扫描会检查所有磁盘上的每个文件，通常要一到几个小时，期间电脑会变慢。\n\n平时用快速扫描就够了；怀疑中毒时再做全盘扫描。"), L.T("开始全盘扫描")))
                return;
            _ = RunScan(Defender.ScanKind.Full, null, L.T("正在全盘扫描"));
        }

        void ScanFile_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Title = L.T("选择要扫描的文件") };
            if (dlg.ShowDialog(Main) == true) _ = RunScan(Defender.ScanKind.Custom, dlg.FileName, L.T("正在扫描"));
        }

        void ScanFolder_Click(object sender, RoutedEventArgs e)
        {
            using (var dlg = new System.Windows.Forms.FolderBrowserDialog { Description = L.T("选择要扫描的文件夹（包括里面的所有文件）") })
                if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK) _ = RunScan(Defender.ScanKind.Custom, dlg.SelectedPath, L.T("正在扫描"));
        }

        void OnDragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        void OnDrop(object sender, DragEventArgs e)
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) AcceptFiles(paths);
            e.Handled = true;
        }

        public async void AcceptFiles(string[] paths)
        {
            if (_cts != null) { Main.SetStatus(L.T("正在扫描，请等这次扫描结束后再拖。")); return; }
            if (_status == null || !_status.CanScan) { Main.SetStatus(L.T("现在不能用 Defender 扫描，原因见上方的防护状态。")); return; }
            foreach (var p in paths)
                if (!await RunScan(Defender.ScanKind.Custom, p, paths.Length > 1 ? L.F("正在扫描（{0}/{1}）", Array.IndexOf(paths, p) + 1, paths.Length) : L.T("正在扫描")))
                    break;
        }

        async Task<bool> RunScan(Defender.ScanKind kind, string? path, string title)
        {
            _cts = new CancellationTokenSource();
            ShowStatus();
            ResultPanel.Visibility = Visibility.Collapsed;
            ProgressPanel.Visibility = Visibility.Visible;
            StopButton.Visibility = Visibility.Visible;
            ProgressTitle.Text = title + (path != null ? L.T("：") + System.IO.Path.GetFileName(path.TrimEnd('\\')) : "");
            ProgressText.Text = path ?? "";
            var sw = Stopwatch.StartNew();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            timer.Tick += (s, _) => ProgressText.Text = L.F("已用时 {0:hh\\:mm\\:ss}", sw.Elapsed) + (path != null ? L.T("　") + path : "");
            timer.Start();
            bool ok = false;
            try
            {
                var token = _cts.Token;
                var r = await Task.Run(() => Defender.Scan(kind, path, null, token));
                string where = path != null ? L.F("“{0}”", path) : kind == Defender.ScanKind.Quick ? L.T("快速扫描") : L.T("全盘扫描");
                if (r.ThreatsFound)
                    ShowResult(CheckLevel.Bad, L.F("{0}发现了威胁，Defender 已按默认设置处理（通常是隔离）。详情见下方查杀记录；如果是误报，可以在 Windows 安全中心里恢复。", where));
                else if (r.Ok)
                    ShowResult(CheckLevel.Good, L.F("{0}完成，没有发现威胁。用时 {1:hh\\:mm\\:ss}。", where, sw.Elapsed));
                else
                    ShowResult(CheckLevel.Warn, L.F("扫描没有正常完成（返回码 {0}）。", r.ExitCode) + LastLines(r.Output));
                ok = r.Ok;
            }
            catch (OperationCanceledException) { ShowResult(CheckLevel.Warn, L.T("扫描已停止。")); }
            catch (Exception ex) { ShowResult(CheckLevel.Warn, L.T("扫描出错：") + ex.Message); }
            finally
            {
                timer.Stop();
                _cts = null;
                ProgressPanel.Visibility = Visibility.Collapsed;
            }
            await Refresh();
            return ok;
        }

        async void Update_Click(object sender, RoutedEventArgs e)
        {
            _cts = new CancellationTokenSource();
            ShowStatus();
            ResultPanel.Visibility = Visibility.Collapsed;
            ProgressPanel.Visibility = Visibility.Visible;
            StopButton.Visibility = Visibility.Collapsed;
            ProgressTitle.Text = L.T("正在更新病毒库（由 Defender 联网下载）");
            ProgressText.Text = "";
            try
            {
                var token = _cts.Token;
                var r = await Task.Run(() => Defender.UpdateSignatures(null, token));
                if (r.ExitCode == 0) ShowResult(CheckLevel.Good, L.T("病毒库已是最新。"));
                else ShowResult(CheckLevel.Warn, L.F("更新没有成功（返回码 {0}），可能是没联网。", r.ExitCode) + LastLines(r.Output));
            }
            catch (Exception ex) { ShowResult(CheckLevel.Warn, L.T("更新出错：") + ex.Message); }
            finally
            {
                _cts = null;
                ProgressPanel.Visibility = Visibility.Collapsed;
            }
            await Refresh();
        }

        static string LastLines(string output)
        {
            var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            return lines.Length == 0 ? "" : "\n" + string.Join("\n", lines.Skip(Math.Max(0, lines.Length - 3)));
        }

        void ShowResult(CheckLevel level, string text)
        {
            ResultPanel.Visibility = Visibility.Visible;
            ResultIcon.Text = level == CheckLevel.Good ? "" : "";
            ResultIcon.Foreground = (Brush)FindResource(level == CheckLevel.Good ? "Good" : level == CheckLevel.Warn ? "Warn" : "Bad");
            ResultText.Text = text;
            Main.SetStatus(text.Split('\n')[0]);
        }

        void Stop_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

        void Refresh_Click(object sender, RoutedEventArgs e) => _ = Refresh();

        void OpenCenter_Click(object sender, RoutedEventArgs e) => Defender.OpenSecurityCenter();
    }
}
