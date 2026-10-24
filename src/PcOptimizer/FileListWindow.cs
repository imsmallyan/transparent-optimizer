using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using PcOptimizer.Core;

namespace PcOptimizer
{
    public class FileListWindow : Window
    {
        class Row
        {
            public string Path { get; set; } = "";
            public string SizeText { get; set; } = "";
            public string Time { get; set; } = "";
        }

        public FileListWindow(string title, IEnumerable<FoundFile> files)
        {
            var list = files.OrderByDescending(f => f.Size).ToList();
            Title = L.F("{0} - {1} 个文件 {2}", title, list.Count, FsUtil.HumanSize(list.Sum(f => f.Size)));
            Width = 860; Height = 560;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            FontFamily = (FontFamily)Application.Current.FindResource("UiFont");
            SetResourceReference(BackgroundProperty, "CardBg");
            SetResourceReference(ForegroundProperty, "TextMain");
            Theme.Track(this);
            Language = App.XmlLang;

            var view = new GridView();
            view.Columns.Add(new GridViewColumn { Header = L.T("大小"), Width = 90, DisplayMemberBinding = new Binding("SizeText") });
            view.Columns.Add(new GridViewColumn { Header = L.T("修改时间"), Width = 140, DisplayMemberBinding = new Binding("Time") });
            view.Columns.Add(new GridViewColumn { Header = L.T("路径（双击在资源管理器中定位）"), Width = 600, DisplayMemberBinding = new Binding("Path") });

            var lv = new ListView
            {
                View = view, BorderThickness = new Thickness(0),
                ItemsSource = list.Select(f => new Row
                {
                    Path = f.Path, SizeText = FsUtil.HumanSize(f.Size),
                    Time = f.MtimeUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
                }).ToList(),
            };
            VirtualizingPanel.SetIsVirtualizing(lv, true);
            lv.MouseDoubleClick += (s, e) => { if (lv.SelectedItem is Row r) MainWindow.Reveal(r.Path); };

            var tip = new TextBlock
            {
                Text = L.T("这里只是查看，不会删除。是否删除由清理页上的勾选决定。"),
                Margin = new Thickness(12, 8, 12, 8), Foreground = (Brush)Application.Current.FindResource("TextSub"),
            };
            var dock = new DockPanel();
            DockPanel.SetDock(tip, Dock.Top);
            dock.Children.Add(tip);
            dock.Children.Add(lv);
            Content = dock;
        }
    }
}
