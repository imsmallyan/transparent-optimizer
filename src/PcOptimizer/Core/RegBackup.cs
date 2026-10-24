using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Win32;

namespace PcOptimizer.Core
{
    public static class RegBackup
    {
        public static string BackupDir => Path.Combine(OpLog.LogDir, "backup");

        public static string HiveName(RegistryHive hive) =>
            hive == RegistryHive.CurrentUser ? "HKEY_CURRENT_USER" : "HKEY_LOCAL_MACHINE";

        public static string ExportKey(RegistryHive hive, string path, string label)
        {
            var sb = new StringBuilder("Windows Registry Editor Version 5.00\r\n\r\n");
            using (var k = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64).OpenSubKey(path))
            {
                if (k == null) throw new InvalidOperationException(L.T("注册表项不存在：") + path);
                AppendKey(sb, $"{HiveName(hive)}\\{path}", k);
            }
            return Save(sb.ToString(), label);
        }

        public static string ExportValue(RegistryHive hive, string path, string name, string label)
        {
            var sb = new StringBuilder("Windows Registry Editor Version 5.00\r\n\r\n");
            using (var k = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64).OpenSubKey(path))
            {
                if (k == null) throw new InvalidOperationException(L.T("注册表项不存在：") + path);
                sb.Append($"[{HiveName(hive)}\\{path}]\r\n");
                sb.Append(FormatValue(name, k.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames), k.GetValueKind(name)));
                sb.Append("\r\n");
            }
            return Save(sb.ToString(), label);
        }

        static void AppendKey(StringBuilder sb, string fullPath, RegistryKey k)
        {
            sb.Append($"[{fullPath}]\r\n");
            foreach (var name in k.GetValueNames())
            {
                try
                {
                    sb.Append(FormatValue(name, k.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames), k.GetValueKind(name)));
                }
                catch (Exception) { }
            }
            sb.Append("\r\n");
            foreach (var sub in k.GetSubKeyNames())
            {
                try
                {
                    using (var sk = k.OpenSubKey(sub))
                        if (sk != null) AppendKey(sb, fullPath + "\\" + sub, sk);
                }
                catch (Exception) { }
            }
        }

        public static string FormatValue(string name, object? value, RegistryValueKind kind)
        {
            string key = name.Length == 0 ? "@" : "\"" + Escape(name) + "\"";
            switch (kind)
            {
                case RegistryValueKind.String:
                    return $"{key}=\"{Escape(value?.ToString() ?? "")}\"\r\n";
                case RegistryValueKind.DWord:
                    return $"{key}=dword:{unchecked((uint)Convert.ToInt32(value)):x8}\r\n";
                case RegistryValueKind.QWord:
                    return $"{key}=hex(b):{Hex(BitConverter.GetBytes(Convert.ToInt64(value)))}\r\n";
                case RegistryValueKind.ExpandString:
                    return $"{key}=hex(2):{Hex(Encoding.Unicode.GetBytes((value?.ToString() ?? "") + "\0"))}\r\n";
                case RegistryValueKind.MultiString:
                    var parts = value as string[] ?? new string[0];
                    return $"{key}=hex(7):{Hex(Encoding.Unicode.GetBytes(string.Join("\0", parts) + "\0\0"))}\r\n";
                default:
                    return $"{key}=hex:{Hex(value as byte[] ?? new byte[0])}\r\n";
            }
        }

        static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

        static string Hex(IEnumerable<byte> bytes) => string.Join(",", bytes.Select(b => b.ToString("x2")));

        static string Save(string content, string label)
        {
            Directory.CreateDirectory(BackupDir);
            string safe = new string(label.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray());
            if (safe.Length > 60) safe = safe.Substring(0, 60);
            string file = Path.Combine(BackupDir, $"{DateTime.Now:yyyyMMdd-HHmmss}-{safe}.reg");
            for (int i = 2; File.Exists(file); i++)
                file = Path.Combine(BackupDir, $"{DateTime.Now:yyyyMMdd-HHmmss}-{safe}-{i}.reg");
            File.WriteAllText(file, content, Encoding.Unicode);
            return file;
        }
    }
}
