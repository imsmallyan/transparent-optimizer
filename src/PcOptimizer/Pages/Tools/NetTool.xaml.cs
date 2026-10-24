using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Security;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using PcOptimizer.Core;

namespace PcOptimizer.Pages.Tools
{
    public partial class NetTool : UserControl, IPage
    {
        List<NetApp> _all = new List<NetApp>();
        List<ListenPort> _ports = new List<ListenPort>();
        bool _busy;

        readonly NetRecorder _recorder = new NetRecorder();
        readonly ObservableCollection<SeenApp> _seen = new ObservableCollection<SeenApp>();
        readonly DispatcherTimer _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        HashSet<string> _blockedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        DateTime _recordStart, _recordEnd;
        bool _recording, _sampling;
        public static bool Recording { get; private set; }
        NetTrace? _trace;
        bool _precise, _preciseFailed;
        uint _preciseError;

        public NetTool()
        {
            InitializeComponent();
            RecordList.ItemsSource = _seen;
            _timer.Tick += async (s, e) => await Sample();
            Unloaded += (s, e) => StopRecording();
            IsVisibleChanged += (s, e) => { if (!IsVisible) StopRecording(); };
        }

        MainWindow Main => (MainWindow)Application.Current.MainWindow;

        public void OnShown() => _ = Refresh();

        async Task Refresh()
        {
            if (_busy) return;
            _busy = true;
            try
            {
                Headline.Text = L.T("正在读取联网情况…");
                var (apps, limits, fw, ports) = await Task.Run(() => (NetControl.List(), NetControl.Limits(), NetControl.FirewallOn(), NetControl.ListeningPorts(true)));
                _ports = ports;
                foreach (var a in apps)
                    if (a.Path.Length > 0 && limits.TryGetValue(a.Path, out long k)) a.LimitKBps = k;
                foreach (var kv in limits.Where(kv => !apps.Any(a => string.Equals(a.Path, kv.Key, StringComparison.OrdinalIgnoreCase))))
                    apps.Insert(0, new NetApp { Name = System.IO.Path.GetFileNameWithoutExtension(kv.Key), Path = kv.Key, LimitKBps = kv.Value });
                _all = apps;

                var warn = new List<string>();
                if (fw == false) warn.Add(L.T("Windows 防火墙在当前网络下是关着的，“禁止联网”的规则不会起作用。如果装了别的防火墙软件，请在那个软件里设置。"));
                if (fw == null) warn.Add(L.T("读不到 Windows 防火墙的状态，“禁止联网”可能用不了。"));
                if (!SysInfo.IsAdmin()) warn.Add(L.T("现在是普通权限：可以查看，但禁止联网和限速需要点底部的“以管理员身份重新打开”。"));
                WarnText.Text = string.Join("\n", warn);
                WarnCard.Visibility = warn.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                ApplyFilter();
            }
            finally { _busy = false; }
        }

        bool PortsMode => TabPorts.IsChecked == true;
        bool RecordMode => TabRecord.IsChecked == true;

        void ApplyFilter()
        {
            bool ports = PortsMode, record = RecordMode;
            AppsPanel.Visibility = !ports && !record ? Visibility.Visible : Visibility.Collapsed;
            PortsPanel.Visibility = ports ? Visibility.Visible : Visibility.Collapsed;
            RecordPanel.Visibility = record ? Visibility.Visible : Visibility.Collapsed;
            RecordHeader.Visibility = record ? Visibility.Visible : Visibility.Collapsed;
            foreach (UIElement e in new UIElement[] { ShowSystem, SystemLabel }) e.Visibility = ports ? Visibility.Collapsed : Visibility.Visible;
            foreach (UIElement e in new UIElement[] { ShowUdp, UdpLabel }) e.Visibility = ports ? Visibility.Visible : Visibility.Collapsed;
            if (ports) { ApplyPorts(); return; }
            if (record) { ApplyRecord(true); return; }

            var list = _all.Where(a => ShowSystem.IsChecked == true || !a.IsSystem || a.Blocked).ToList();
            AppList.ItemsSource = list;
            EmptyCard.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            int online = _all.Count(a => a.Established > 0 && !a.IsSystem);
            Headline.Text = L.F("{0} 个软件正在联网", online);
            Summary.Text = L.F("共 {0} 个连接", _all.Sum(a => a.Established)) +
                           (_all.Any(a => a.Blocked) ? L.F(" · 已禁止 {0} 个软件联网", _all.Count(a => a.Blocked)) : "");
            SystemLabel.Text = L.F("显示系统进程（{0}）", _all.Count(a => a.IsSystem));
        }

        void ApplyPorts()
        {
            var list = _ports.Where(p => ShowUdp.IsChecked == true || p.Protocol == "TCP").ToList();
            var view = new ListCollectionView(list);
            view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ListenPort.Group)));
            PortList.ItemsSource = view;
            PortsEmptyCard.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            Headline.Text = L.F("{0} 个端口能被其他电脑连上", list.Count(p => p.Exposed));
            int risky = list.Count(p => p.Risky);
            Summary.Text = L.F("共 {0} 个端口在监听", list.Count) + (risky > 0 ? L.F(" · {0} 个需要留意", risky) : "");
            UdpLabel.Text = L.F("显示 UDP 端口（{0}）", _ports.Count(p => p.Protocol == "UDP"));
        }

        bool ShowSeen(SeenApp a) => ShowSystem.IsChecked == true || !a.IsSystem || a.Blocked;

        void ApplyRecord(bool rebuild)
        {
            if (rebuild)
            {
                _seen.Clear();
                foreach (var a in _recorder.Apps.Where(ShowSeen)) _seen.Add(a);
            }
            int shown = _recorder.Apps.Count(a => !a.IsSystem);
            var span = (_recording ? DateTime.Now : _recordEnd) - _recordStart;
            string elapsed = span.TotalHours >= 1
                ? L.F("{0} 小时 {1} 分", (int)span.TotalHours, span.Minutes)
                : L.F("{0} 分 {1} 秒", span.Minutes, span.Seconds);
            if (_recording)
                Headline.Text = L.F("正在记录：{0} 个软件联过网", shown);
            else if (_recorder.Samples > 0)
                Headline.Text = L.F("记录结束：{0} 个软件联过网", shown);
            else
                Headline.Text = L.T("持续记录联网的软件");
            Summary.Text = _recorder.Samples == 0 ? "" :
                           _precise ? L.F("已记录 {0}", elapsed) + L.T(" · 精确模式") : L.F("已记录 {0}", elapsed);
            if (SysInfo.IsAdmin() && !_preciseFailed)
                RecordInfo.Text = L.T("以管理员身份运行时使用精确模式：通过 Windows 的事件跟踪（ETW，资源监视器用的也是它）接收每一次联网，很短的连接和 DNS 查询这类 UDP 数据也能记到。记录只在这个页面开着时进行，离开页面就停止，不会在后台运行。如果本程序在记录途中意外关闭（比如程序出错或被强制结束），请再以管理员身份打开一次本程序，它会关掉留在 Windows 里的记录。");
            else if (_preciseFailed)
                RecordInfo.Text = L.F("精确模式没能启动（错误代码 {0}），已改为每秒查看一次连接表。不到一秒就断开的连接可能记不到；只记录 TCP 连接。记录只在这个页面开着时进行，离开页面就停止。", _preciseError);
            else
                RecordInfo.Text = L.T("开始后每秒查看一次连接表，把这段时间里连过网的软件都列出来。记录只在这个页面开着时进行，离开页面就停止，不会在后台运行。不到一秒就断开的连接可能记不到；只记录 TCP 连接。以管理员身份运行时会改用精确模式，一次都不会漏。");
            SystemLabel.Text = L.F("显示系统进程（{0}）", _recorder.Apps.Count(a => a.IsSystem));
            RecordButton.Content = _recording ? L.T("停止记录") : _recorder.Samples > 0 ? L.T("重新开始") : L.T("开始记录");
            RecordEmptyText.Text = _recorder.Samples == 0 ? L.T("点“开始记录”，然后照常使用电脑或打开想检查的软件，过一会儿回来看。")
                                                          : L.T("到目前为止还没有发现软件联网。");
            RecordEmptyCard.Visibility = _seen.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        void Record_Click(object sender, RoutedEventArgs e)
        {
            if (_recording) { StopRecording(); return; }
            _recorder.Clear();
            _seen.Clear();
            _blockedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try { foreach (var (path, _) in NetControl.BlockedRules()) _blockedPaths.Add(path); }
            catch (Exception) { }
            _precise = false;
            _preciseFailed = false;
            if (SysInfo.IsAdmin())
            {
                _trace = NetTrace.TryStart(out uint err);
                _precise = _trace != null;
                _preciseFailed = _trace == null;
                _preciseError = err;
            }
            _recordStart = DateTime.Now;
            _recording = true;
            Recording = true;
            _timer.Start();
            _ = Sample();
        }

        void StopRecording()
        {
            if (!_recording) return;
            _recording = false;
            Recording = false;
            _timer.Stop();
            _recordEnd = DateTime.Now;
            var trace = _trace;
            _trace = null;
            if (trace != null) Task.Run(() => trace.Dispose());
            if (RecordMode) ApplyRecord(false);
        }

        async Task Sample()
        {
            if (_sampling || !_recording) return;
            _sampling = true;
            try
            {
                var trace = _trace;
                var (conns, described) = await Task.Run(() =>
                {
                    var c = NetControl.ActiveRemotes();
                    var d = new Dictionary<int, PidInfo>();
                    if (trace != null)
                        foreach (var kv in trace.Drain())
                        {
                            c.Add(kv.Key);
                            if (!d.ContainsKey(kv.Key.Pid)) d[kv.Key.Pid] = kv.Value;
                        }
                    foreach (int pid in _recorder.UnknownPids(c))
                        if (!d.ContainsKey(pid)) d[pid] = NetControl.Describe(pid);
                    return (c, d);
                });
                if (!_recording) return;
                foreach (var a in _recorder.Add(DateTime.Now, conns, described, _blockedPaths))
                    if (ShowSeen(a)) _seen.Add(a);
                if (RecordMode) ApplyRecord(false);
            }
            catch (Exception) { }
            finally { _sampling = false; }
        }

        void BlockSeen_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button b && b.Tag is SeenApp a) Block(a.Name, a.Path);
        }

        void UnblockSeen_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button b && b.Tag is SeenApp a)
                Run(() => { NetControl.Unblock(a.Path); MarkBlocked(a.Path, false); }, L.F("已允许“{0}”联网", a.Name));
        }

        void Tab_Changed(object sender, RoutedEventArgs e) { if (IsLoaded) ApplyFilter(); }

        void Filter_Changed(object sender, RoutedEventArgs e) => ApplyFilter();
        async void Refresh_Click(object sender, RoutedEventArgs e) => await Refresh();

        void Block_Click(object sender, RoutedEventArgs e)
        {
            if (!(sender is Button b) || !(b.Tag is NetApp a)) return;
            Block(a.Name, a.Path);
        }

        void BlockPort_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button b && b.Tag is ListenPort p) Block(p.ProcessName, p.Path);
        }

        void UnblockPort_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button b && b.Tag is ListenPort p)
                Run(() => { NetControl.Unblock(p.Path); MarkBlocked(p.Path, false); }, L.F("已允许“{0}”联网", p.ProcessName));
        }

        void MarkBlocked(string path, bool blocked)
        {
            foreach (var a in _all.Where(a => string.Equals(a.Path, path, StringComparison.OrdinalIgnoreCase))) a.Blocked = blocked;
            foreach (var p in _ports.Where(p => string.Equals(p.Path, path, StringComparison.OrdinalIgnoreCase))) p.Blocked = blocked;
            foreach (var s in _recorder.Apps.Where(s => string.Equals(s.Path, path, StringComparison.OrdinalIgnoreCase))) s.Blocked = blocked;
            if (blocked) _blockedPaths.Add(path); else _blockedPaths.Remove(path);
        }

        void Block(string name, string path)
        {
            if (!Dialog.Confirm(Main, L.T("禁止联网"),
                    L.F("将在 Windows 防火墙里加两条规则，阻止“{0}”收发任何网络数据：\n{1}\n\n", name, path) +
                    L.T("已经建立的连接可能要等软件重连时才会断开。随时可以点“允许联网”恢复，规则名以“") + NetControl.RulePrefix + L.T("”开头。"),
                    L.T("禁止联网")))
                return;
            Run(() => { NetControl.Block(path, name); MarkBlocked(path, true); }, L.F("已禁止“{0}”联网", name));
        }

        void Unblock_Click(object sender, RoutedEventArgs e)
        {
            if (!(sender is Button b) || !(b.Tag is NetApp a)) return;
            Run(() => { NetControl.Unblock(a.Path); MarkBlocked(a.Path, false); }, L.F("已允许“{0}”联网", a.Name));
        }

        async void Limit_Click(object sender, RoutedEventArgs e)
        {
            if (!(sender is Button b) || !(b.Tag is NetApp a)) return;
            string? input = Dialog.Prompt(Main, L.T("上传限速"),
                L.F("限制“{0}”的上传速度，单位 KB/s。填 0 取消限速。\n\n", a.Name) +
                L.T("Windows 自带的办法只能限制上传；下载限速需要安装网络驱动，本工具不装驱动，所以做不到。"),
                a.LimitKBps > 0 ? a.LimitKBps.ToString() : "500", L.T("确定"));
            if (input == null) return;
            if (!long.TryParse(input.Trim(), out long kbps) || kbps < 0)
            {
                Dialog.Warn(Main, L.T("输入不对"), L.T("请输入一个整数，比如 500 表示每秒最多上传 500 KB。"));
                return;
            }
            b.IsEnabled = false;
            try
            {
                await Task.Run(() => NetControl.SetLimit(a.Path, kbps));
                a.LimitKBps = kbps;
                Main.SetStatus(kbps > 0 ? L.F("已把“{0}”的上传限制为 {1} KB/s", a.Name, kbps) : L.F("已取消“{0}”的上传限速", a.Name));
            }
            catch (Exception ex)
            {
                Dialog.Warn(Main, L.T("没有设置成功"), ex.Message +
                    (SysInfo.IsAdmin() ? "" : L.T("\n\n需要管理员权限，请点底部的“以管理员身份重新打开”。")));
            }
            finally { b.IsEnabled = true; }
        }

        void Run(Action action, string done)
        {
            try
            {
                action();
                Main.SetStatus(done);
                ApplyFilter();
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException || ex is SecurityException ||
                                       ex is System.Runtime.InteropServices.COMException && ((System.Runtime.InteropServices.COMException)ex).HResult == unchecked((int)0x80070005))
            {
                Dialog.Warn(Main, L.T("权限不足"), L.T("修改防火墙规则需要管理员权限。\n\n请点底部的“以管理员身份重新打开”。"));
            }
            catch (Exception ex)
            {
                Dialog.Warn(Main, L.T("没有改成功"), ex.Message);
            }
        }
    }
}
