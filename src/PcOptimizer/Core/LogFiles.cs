using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;

namespace PcOptimizer.Core
{
    public enum LogKind { Operation, RegBackup, RemovedApps }

    public class LogFile : INotifyPropertyChanged
    {
        public string Path { get; set; } = "";
        public LogKind Kind { get; set; }
        public long Size { get; set; }
        public DateTime Time { get; set; }

        bool _selected;
        public bool Selected { get => _selected; set { _selected = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Selected))); } }

        public string Name => System.IO.Path.GetFileName(Path);
        public string KindText => Kind == LogKind.Operation ? L.T("操作日志")
                                : Kind == LogKind.RegBackup ? L.T("注册表备份") : L.T("预装应用删除记录");
        public string Meta => KindText + " · " + FsUtil.HumanSize(Size) + " · " + Time.ToString("yyyy-MM-dd HH:mm");
        public string Warning => Kind == LogKind.RegBackup ? L.T("删除后就不能用它把卸载时删掉的注册表项恢复回来。")
                               : Kind == LogKind.RemovedApps ? L.T("删除后“预装应用清理”里就不再列出删过的应用。") : "";
        public bool HasWarning => Warning.Length > 0;

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    public static class LogFiles
    {
        public static List<LogFile> List() => List(OpLog.LogDir, DateTime.Today);

        public static List<LogFile> List(string dir, DateTime today)
        {
            var list = new List<LogFile>();
            if (!Directory.Exists(dir)) return list;
            void Add(string path, LogKind kind)
            {
                try
                {
                    var fi = new FileInfo(path);
                    list.Add(new LogFile
                    {
                        Path = fi.FullName, Kind = kind, Size = fi.Length, Time = fi.LastWriteTime,
                        Selected = kind == LogKind.Operation && fi.LastWriteTime.Date < today,
                    });
                }
                catch (Exception) { }
            }
            foreach (var f in Directory.GetFiles(dir, "*.log")) Add(f, LogKind.Operation);
            string removed = System.IO.Path.Combine(dir, "removed-apps.txt");
            if (File.Exists(removed)) Add(removed, LogKind.RemovedApps);
            string backup = System.IO.Path.Combine(dir, "backup");
            if (Directory.Exists(backup))
                foreach (var f in Directory.GetFiles(backup, "*.reg")) Add(f, LogKind.RegBackup);
            return list.OrderBy(f => f.Kind).ThenByDescending(f => f.Time).ToList();
        }

        public static string Preview(string path, int maxChars = 200_000)
        {
            try
            {
                string text;
                bool cut = false;
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    long maxBytes = (long)maxChars * 3;
                    if (fs.Length > maxBytes) { fs.Seek(-maxBytes, SeekOrigin.End); cut = true; }
                    using (var r = new StreamReader(fs, Encoding.UTF8, !cut))
                        text = r.ReadToEnd();
                }
                if (cut)
                {
                    int nl = text.IndexOf('\n');
                    if (nl >= 0) text = text.Substring(nl + 1);
                }
                if (text.Length > maxChars) { text = text.Substring(text.Length - maxChars); cut = true; }
                if (cut) return L.F("（文件较大，只显示最后 {0} 个字符）", text.Length) + Environment.NewLine + text;
                return text.Length == 0 ? L.T("（空文件）") : text;
            }
            catch (Exception ex) { return L.T("读取失败：") + ex.Message; }
        }
    }
}
