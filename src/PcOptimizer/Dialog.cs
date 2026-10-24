using PcOptimizer.Core;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PcOptimizer
{
    public static class Dialog
    {
        public static void Info(Window? owner, string title, string message) =>
            Show(owner, title, message, L.T("知道了"), null, "", "Primary");

        public static void Warn(Window? owner, string title, string message) =>
            Show(owner, title, message, L.T("知道了"), null, "", "Warn");

        public static bool Confirm(Window? owner, string title, string message, string? okText = null) =>
            Show(owner, title, message, okText ?? L.T("确定"), L.T("取消"), "", "Warn");

        public static string? Prompt(Window? owner, string title, string message, string initial, string? okText = null)
        {
            var box = new TextBox { Text = initial, Margin = new Thickness(0, 10, 0, 0), Padding = new Thickness(6, 4, 6, 4), FontSize = 13 };
            box.Loaded += (s, e) => { box.Focus(); box.SelectAll(); };
            return Show(owner, title, message, okText ?? L.T("确定"), L.T("取消"), "", "Primary", box) ? box.Text : null;
        }

        static bool Show(Window? owner, string title, string message, string okText, string? cancelText,
                         string icon, string colorKey, UIElement? extra = null)
        {
            var app = Application.Current;
            var win = new Window
            {
                Title = title, Owner = owner, SizeToContent = SizeToContent.WidthAndHeight,
                ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false,
                WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
                MaxWidth = 560, FontFamily = (FontFamily)app.FindResource("UiFont"), FontSize = 13,
            };
            win.SetResourceReference(Window.BackgroundProperty, "CardBg");
            win.SetResourceReference(Window.ForegroundProperty, "TextMain");
            Theme.Track(win);
            win.Language = App.XmlLang;

            var iconText = new TextBlock
            {
                Text = icon, FontSize = 30, Margin = new Thickness(0, 2, 16, 0),
                FontFamily = (FontFamily)app.FindResource("IconFont"),
                Foreground = (Brush)app.FindResource(colorKey), VerticalAlignment = VerticalAlignment.Top,
            };
            var titleText = new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) };
            var body = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, LineHeight = 21, MaxWidth = 440,
                                       Foreground = (Brush)app.FindResource("TextMain") };
            var text = new StackPanel();
            text.Children.Add(titleText);
            text.Children.Add(body);
            if (extra != null) text.Children.Add(extra);

            var top = new DockPanel { Margin = new Thickness(24, 22, 24, 18) };
            DockPanel.SetDock(iconText, Dock.Left);
            top.Children.Add(iconText);
            top.Children.Add(text);

            bool result = false;
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
                                           Margin = new Thickness(24, 0, 24, 18) };
            if (cancelText != null)
            {
                var cancel = new Button { Content = cancelText, Style = (Style)app.FindResource("GhostButton"),
                                          Margin = new Thickness(0, 0, 10, 0), IsCancel = true, MinWidth = 80 };
                cancel.Click += (s, e) => win.Close();
                buttons.Children.Add(cancel);
            }
            var ok = new Button { Content = okText, Style = (Style)app.FindResource("PrimaryButton"),
                                  FontSize = 13, Padding = new Thickness(22, 6, 22, 6), IsDefault = cancelText == null || extra != null, MinWidth = 80 };
            ok.Click += (s, e) => { result = true; win.Close(); };
            buttons.Children.Add(ok);

            var root = new StackPanel();
            root.Children.Add(top);
            root.Children.Add(buttons);
            win.Content = root;
            win.ShowDialog();
            return result;
        }
    }
}
