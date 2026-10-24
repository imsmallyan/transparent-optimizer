using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace PcOptimizer.Core
{
    public static class AppSettings
    {
        public static string FilePath { get; set; } = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.ini");

        public static string? Get(string key)
        {
            try
            {
                if (!File.Exists(FilePath)) return null;
                foreach (var line in File.ReadAllLines(FilePath))
                    if (line.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase)) return line.Substring(key.Length + 1).Trim();
            }
            catch (Exception) { }
            return null;
        }

        public static bool Set(string key, string value)
        {
            try
            {
                var lines = new List<string>();
                if (File.Exists(FilePath))
                    foreach (var line in File.ReadAllLines(FilePath))
                        if (!line.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase) && line.Trim().Length > 0) lines.Add(line);
                lines.Add(key + "=" + value);
                File.WriteAllText(FilePath, string.Join("\r\n", lines) + "\r\n", new UTF8Encoding(false));
                return true;
            }
            catch (Exception) { return false; }
        }
    }
}
