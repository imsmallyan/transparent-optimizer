using System;
using System.IO;

namespace PcOptimizer.Core
{
    public static class OpLog
    {
        public static string LogDir { get; set; } =
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");

        static readonly object Gate = new object();

        public static void Write(string action, string detail)
        {
            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(LogDir);
                    string file = Path.Combine(LogDir, DateTime.Now.ToString("yyyy-MM-dd") + ".log");
                    File.AppendAllText(file, $"{DateTime.Now:HH:mm:ss}\t{action}\t{detail}{Environment.NewLine}");
                }
            }
            catch (Exception)
            {
            }
        }
    }
}
