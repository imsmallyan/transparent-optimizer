using PcOptimizer.Core;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using PcOptimizer.Pages.Tools;

namespace PcOptimizer.Pages
{
    public partial class MorePage : UserControl, IPage, IFileDropTarget
    {
        readonly Dictionary<string, UserControl> _tools = new Dictionary<string, UserControl>();

        public string? CurrentTool { get; private set; }

        public MorePage()
        {
            InitializeComponent();
        }

        public void OnShown()
        {
            if (ToolHost.Content is IPage p) p.OnShown();
        }

        public void AcceptFiles(string[] paths)
        {
            if (ToolHost.Content is IFileDropTarget t) t.AcceptFiles(paths);
            else ((MainWindow)Application.Current.MainWindow).SetStatus(L.T("请先打开“文件粉碎”或“病毒防护”，再把文件拖进来。"));
        }

        void Tool_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button b && b.Tag is string key) Open(key);
        }

        public void Open(string key)
        {
            if (!_tools.TryGetValue(key, out var tool))
            {
                switch (key)
                {
                    case "update": tool = new UpdateTool(); break;
                    case "apps": tool = new AppsTool(); break;
                    case "dedup": tool = new DedupTool(); break;
                    case "net": tool = new NetTool(); break;
                    case "shred": tool = new ShredTool(); break;
                    case "defender": tool = new DefenderTool(); break;
                    default: tool = new MenuTool(); break;
                }
                _tools[key] = tool;
            }
            ToolHost.Content = tool;
            CurrentTool = key;
            HomePanel.Visibility = Visibility.Collapsed;
            ToolPanel.Visibility = Visibility.Visible;
            if (tool is IPage p) p.OnShown();
        }

        void Back_Click(object sender, RoutedEventArgs e)
        {
            ToolHost.Content = null;
            CurrentTool = null;
            ToolPanel.Visibility = Visibility.Collapsed;
            HomePanel.Visibility = Visibility.Visible;
        }
    }
}
