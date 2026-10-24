using System;
using System.IO;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using PcOptimizer.Core;

namespace PcOptimizer
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            try
            {
                Directory.CreateDirectory(OpLog.LogDir);
                string probe = Path.Combine(OpLog.LogDir, ".write-test");
                File.WriteAllText(probe, "");
                File.Delete(probe);
            }
            catch (Exception)
            {
                OpLog.LogDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PcOptimizer", "logs");
            }
            L.Init();
            ApplyFont();
            FrameworkElement.LanguageProperty.OverrideMetadata(typeof(FrameworkElement), new FrameworkPropertyMetadata(XmlLang));
            Theme.Init();
            if (SysInfo.IsAdmin()) NetTrace.StopLeftover();
            base.OnStartup(e);
        }

        protected override void OnExit(ExitEventArgs e)
        {
            if (NetTrace.Running) NetTrace.StopLeftover();
            base.OnExit(e);
        }

        public static XmlLanguage XmlLang =>
            XmlLanguage.GetLanguage(L.Lang == "ja" ? "ja-JP" : L.Lang == "en" ? "en-US" : L.Lang);

        public static void ApplyFont()
        {
            string font = L.Lang == "zh-TW" ? "Microsoft JhengHei UI, Segoe UI"
                        : L.Lang == "ja" ? "Yu Gothic UI, Meiryo UI, Segoe UI"
                        : L.Lang == "en" ? "Segoe UI, Microsoft YaHei UI"
                        : "Microsoft YaHei UI, Segoe UI";
            Current.Resources.MergedDictionaries[0]["UiFont"] = new FontFamily(font);
        }

        void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            OpLog.Write("ERROR", e.Exception.ToString().Replace(Environment.NewLine, " | "));
            MessageBox.Show(L.T("程序遇到了一个错误，已记录到日志：\n\n") + e.Exception.Message, L.T("出错了"),
                MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
        }
    }
}
