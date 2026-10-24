using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using PcOptimizer.Core;

namespace PcOptimizer
{
    public class LogWindow : Window
    {
        List<LogFile> _files = new List<LogFile>();
        readonly ListBox _list = new ListBox { BorderThickness = new Thickness(0) };
        readonly TextBox _preview = new TextBox
        {
            IsReadOnly = true, TextWrapping = TextWrapping.NoWrap, BorderThickness = new Thickness(0),
            FontFamily = new FontFamily("Consolas, Microsoft YaHei UI"), FontSize = 12, Padding = new Thickness(10),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        readonly TextBlock _summary = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        readonly Button _delete;

        public bool Changed { get; private set; }

        public LogWindow()
        {
            Title = L.T("本工具的日志");
            Width = 980; Height = 600; MinWidth = 720; MinHeight = 420;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            FontFamily = (FontFamily)Application.Current.FindResource("UiFont");
            SetResourceReference(BackgroundProperty, "PageBg");
            SetResourceReference(ForegroundProperty, "TextMain");
            Theme.Track(this);
            Language = App.XmlLang;

            _preview.SetResourceReference(BackgroundProperty, "CardBg");
            _preview.SetResourceReference(ForegroundProperty, "TextMain");
            _list.SetResourceReference(BackgroundProperty, "CardBg");
            _summary.SetResourceReference(TextBlock.ForegroundProperty, "TextSub");

            ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
            _list.ItemTemplate = (DataTemplate)System.Windows.Markup.XamlReader.Parse(@"
<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'>
  <DockPanel Margin='2,4'>
    <CheckBox DockPanel.Dock='Left' IsChecked='{Binding Selected, Mode=TwoWay}' VerticalAlignment='Top' Margin='0,2,10,0'/>
    <StackPanel>
      <TextBlock Text='{Binding Name}' ToolTip='{Binding Name}' FontWeight='SemiBold' TextTrimming='CharacterEllipsis'/>
      <TextBlock Text='{Binding Meta}' FontSize='12' Opacity='0.7' Margin='0,2,0,0'/>
    </StackPanel>
  </DockPanel>
</DataTemplate>");
            _list.SelectionChanged += (s, e) =>
            {
                if (_list.SelectedItem is LogFile f)
                {
                    _preview.Text = LogFiles.Preview(f.Path);
                    Dispatcher.BeginInvoke(new Action(_preview.ScrollToEnd), System.Windows.Threading.DispatcherPriority.Loaded);
                }
            };

            var tip = new TextBlock
            {
                Text = L.T("点左边的文件可以在右边查看内容。勾选的文件会移到回收站，删错了可以还原。默认只勾选今天以前的操作日志；注册表备份和预装应用删除记录还有用，要删请自己勾选。"),
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(16, 12, 16, 10),
            };
            tip.SetResourceReference(TextBlock.ForegroundProperty, "TextSub");

            var open = new Button { Content = L.T("打开日志文件夹"), Style = (Style)Application.Current.FindResource("GhostButton"), Margin = new Thickness(0, 0, 10, 0) };
            open.Click += (s, e) =>
            {
                try { Directory.CreateDirectory(OpLog.LogDir); Process.Start("explorer.exe", $"\"{OpLog.LogDir}\""); } catch (Exception) { }
            };
            _delete = new Button { Content = L.T("删除勾选的文件"), Style = (Style)Application.Current.FindResource("PrimaryButton") };
            _delete.Click += Delete_Click;

            var bottom = new DockPanel { Margin = new Thickness(16, 10, 16, 14) };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            buttons.Children.Add(open);
            buttons.Children.Add(_delete);
            DockPanel.SetDock(buttons, Dock.Right);
            bottom.Children.Add(buttons);
            bottom.Children.Add(_summary);

            var body = new Grid { Margin = new Thickness(16, 0, 16, 0) };
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(320) });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var left = new Border { Child = _list, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Padding = new Thickness(6), ClipToBounds = true };
            left.SetResourceReference(Border.BorderBrushProperty, "Line");
            left.SetResourceReference(Border.BackgroundProperty, "CardBg");
            var right = new Border { Child = _preview, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), ClipToBounds = true };
            right.SetResourceReference(Border.BorderBrushProperty, "Line");
            right.SetResourceReference(Border.BackgroundProperty, "CardBg");
            Grid.SetColumn(right, 2);
            body.Children.Add(left);
            body.Children.Add(right);

            var dock = new DockPanel();
            DockPanel.SetDock(tip, Dock.Top);
            DockPanel.SetDock(bottom, Dock.Bottom);
            dock.Children.Add(tip);
            dock.Children.Add(bottom);
            dock.Children.Add(body);
            Content = dock;

            Reload();
        }

        void Reload()
        {
            foreach (var f in _files) f.PropertyChanged -= File_Changed;
            _files = LogFiles.List();
            foreach (var f in _files) f.PropertyChanged += File_Changed;
            var view = new ListCollectionView(_files);
            view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(LogFile.KindText)));
            _list.GroupStyle.Clear();
            _list.GroupStyle.Add(new GroupStyle
            {
                HeaderTemplate = (DataTemplate)System.Windows.Markup.XamlReader.Parse(@"
<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'>
  <TextBlock Text='{Binding Name}' FontWeight='Bold' Margin='4,8,0,4'/>
</DataTemplate>"),
            });
            _list.ItemsSource = view;
            _preview.Text = _files.Count == 0 ? L.T("还没有日志。") : L.T("点左边的文件查看内容。");
            UpdateSummary();
        }

        void File_Changed(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => UpdateSummary();

        void UpdateSummary()
        {
            var sel = _files.Where(f => f.Selected).ToList();
            _summary.Text = L.F("共 {0} 个文件 {1} · 已勾选 {2} 个 {3}", _files.Count, FsUtil.HumanSize(_files.Sum(f => f.Size)),
                                sel.Count, FsUtil.HumanSize(sel.Sum(f => f.Size)));
            _delete.IsEnabled = sel.Count > 0;
        }

        async void Delete_Click(object sender, RoutedEventArgs e)
        {
            var sel = _files.Where(f => f.Selected).ToList();
            if (sel.Count == 0) return;
            var warnings = sel.Where(f => f.HasWarning).Select(f => f.Warning).Distinct().ToList();
            string msg = L.F("将把 {0} 个文件（{1}）移到回收站：\n\n", sel.Count, FsUtil.HumanSize(sel.Sum(f => f.Size))) +
                         string.Join("\n", sel.Take(12).Select(f => "· " + f.Name)) + (sel.Count > 12 ? L.F("\n…等 {0} 个", sel.Count) : "") +
                         (warnings.Count > 0 ? "\n\n" + L.T("请注意：") + "\n" + string.Join("\n", warnings) : "");
            if (!Dialog.Confirm(this, L.T("删除日志"), msg, L.T("移到回收站"))) return;
            _delete.IsEnabled = false;
            var failed = new List<string>();
            foreach (var f in sel)
            {
                try { await Sta.Run(() => { RecycleBin.Send(f.Path); return true; }); }
                catch (Exception ex) { failed.Add(f.Name + L.T("：") + ex.Message); }
            }
            OpLog.Write("LOG_CLEAN", $"files={sel.Count - failed.Count}");
            Changed = true;
            Reload();
            if (failed.Count > 0) Dialog.Warn(this, L.T("有文件没有删除"), string.Join("\n", failed));
        }
    }
}
