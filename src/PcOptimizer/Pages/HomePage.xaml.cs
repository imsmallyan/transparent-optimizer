using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PcOptimizer.Core;

namespace PcOptimizer.Pages
{
    public partial class HomePage : UserControl, IPage
    {
        HealthReport? _report;
        bool _busy;

        public HomePage()
        {
            InitializeComponent();
            DrawArc(0, (Brush)FindResource("Primary"));
        }

        MainWindow Main => (MainWindow)Application.Current.MainWindow;

        bool _infoLoaded;

        public void OnShown()
        {
            if (!_infoLoaded) LoadInfo();
        }

        async void LoadInfo()
        {
            _infoLoaded = true;
            InfoList.ItemsSource = await Task.Run(() => SysInfo.Collect());
        }

        void InfoRefresh_Click(object sender, RoutedEventArgs e) => LoadInfo();

        async void Check_Click(object sender, RoutedEventArgs e) => await RunCheck();

        async Task RunCheck()
        {
            if (_busy) return;
            _busy = true;
            CheckButton.IsEnabled = false;
            FixButton.Visibility = Visibility.Collapsed;
            ProgressPanel.Visibility = Visibility.Visible;
            Headline.Text = L.T("正在体检…");
            Subline.Text = L.T("只读取信息，不会修改任何东西。");
            var progress = new Progress<string>(s => ProgressText.Text = s);
            try
            {
                _report = await Task.Run(() => HealthCheck.Run(progress, CancellationToken.None));
                ShowReport(_report);
            }
            finally
            {
                _busy = false;
                CheckButton.IsEnabled = true;
                CheckButton.Content = L.T("重新体检");
                ProgressPanel.Visibility = Visibility.Collapsed;
            }
        }

        void ShowReport(HealthReport r)
        {
            Placeholder.Visibility = Visibility.Collapsed;
            ResultList.ItemsSource = r.Items.OrderByDescending(i => i.Level).ToList();
            string colorKey = r.Score >= 85 ? "Good" : r.Score >= 60 ? "Warn" : "Bad";
            var brush = (Brush)FindResource(colorKey);
            ScoreText.Text = r.Score.ToString();
            ScoreText.Foreground = brush;
            ScoreUnit.Text = L.T("分");
            DrawArc(r.Score, brush);

            int problems = r.Items.Count(i => i.Level != CheckLevel.Good);
            Headline.Text = problems == 0 ? L.T("电脑状态很好，继续保持！")
                          : r.Score >= 85 ? L.F("电脑状态良好 有 {0} 项可以优化", problems)
                          : L.F("发现 {0} 项问题 建议处理", problems);
            long junk = r.JunkFiles.Sum(f => f.Size);
            Subline.Text = junk > 0 ? L.F("可清理垃圾 {0}，清理前会再次确认。", FsUtil.HumanSize(junk)) : L.T("体检完成");
            FixButton.Visibility = junk > 0 ? Visibility.Visible : Visibility.Collapsed;
            CheckButton.Style = (Style)FindResource(junk > 0 ? "BigGhostButton" : "BigButton");
            Main.SetStatus(L.F("体检完成：{0} 分", r.Score));
        }

        void DrawArc(int score, Brush brush)
        {
            Arc.Stroke = brush;
            if (score <= 0) { Arc.Data = null; return; }
            double angle = Math.Min(359.9, 360.0 * score / 100);
            const double c = 75, radius = 69;
            Point P(double deg) => new Point(c + radius * Math.Sin(deg * Math.PI / 180), c - radius * Math.Cos(deg * Math.PI / 180));
            var fig = new PathFigure { StartPoint = P(0), IsClosed = false };
            fig.Segments.Add(new ArcSegment(P(angle), new Size(radius, radius), 0, angle > 180, SweepDirection.Clockwise, true));
            Arc.Data = new PathGeometry(new[] { fig });
        }

        void Action_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button b && b.Tag is string page && page != "") Main.Navigate(page);
        }

        async void Fix_Click(object sender, RoutedEventArgs e)
        {
            if (_report == null || _busy) return;
            var files = _report.JunkFiles;
            var lines = _report.JunkTargets
                .Select(t => (t.Name, Size: files.Where(f => f.TargetKey == t.Key).Sum(f => f.Size),
                                       Count: files.Count(f => f.TargetKey == t.Key)))
                .Where(x => x.Count > 0)
                .Select(x => L.F("· {0}：{1} 个文件（{2}）", x.Name, x.Count, FsUtil.HumanSize(x.Size)));
            string msg = L.F("将永久删除以下 {0} 个文件，共 {1}：\n\n", files.Count, FsUtil.HumanSize(files.Sum(f => f.Size))) +
                         string.Join("\n", lines) +
                         L.T("\n\n想逐个查看文件，可以到“垃圾清理”页扫描后点“查看明细”。正在使用的文件会自动跳过。");
            if (!Dialog.Confirm(Main, L.T("确认清理"), msg, L.T("确认清理"))) return;

            _busy = true;
            CheckButton.IsEnabled = FixButton.IsEnabled = false;
            ProgressPanel.Visibility = Visibility.Visible;
            var progress = new Progress<string>(s => ProgressText.Text = s);
            try
            {
                var result = await Task.Run(() => Cleaner.Delete(files, progress));
                string done = L.F("已删除 {0} 个文件，释放 {1}。", result.Deleted, FsUtil.HumanSize(result.Freed));
                if (result.Skipped.Count > 0) done += L.F("\n跳过 {0} 个（多为正在使用），详情见操作日志。", result.Skipped.Count);
                Dialog.Info(Main, L.T("清理完成"), done);
            }
            finally
            {
                _busy = false;
                CheckButton.IsEnabled = FixButton.IsEnabled = true;
                ProgressPanel.Visibility = Visibility.Collapsed;
            }
            await RunCheck();
        }
    }
}
