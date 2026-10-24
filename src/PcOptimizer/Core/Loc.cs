using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;

namespace PcOptimizer.Core
{
    public static class L
    {
        public static readonly (string Code, string Name)[] Languages =
        {
            ("en", "English"), ("zh-CN", "简体中文"), ("zh-TW", "繁體中文"), ("ja", "日本語"),
        };

        public static string Lang { get; private set; } = "zh-CN";
        static Dictionary<string, string>? _map;

        public static void Init()
        {
            Use(AppSettings.Get("lang") ?? FromCulture(CultureInfo.CurrentUICulture));
        }

        static string FromCulture(CultureInfo c)
        {
            string n = c.Name;
            if (n.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
                return n.IndexOf("Hant", StringComparison.OrdinalIgnoreCase) >= 0 || n.EndsWith("TW") || n.EndsWith("HK") || n.EndsWith("MO") ? "zh-TW" : "zh-CN";
            if (n.StartsWith("ja", StringComparison.OrdinalIgnoreCase)) return "ja";
            return "en";
        }

        public static void Use(string code)
        {
            Lang = Array.Exists(Languages, l => l.Code == code) ? code : "zh-CN";
            _map = Lang == "zh-CN" ? null : Load(Lang);
        }

        public static bool Save(string code) => AppSettings.Set("lang", code);

        static Dictionary<string, string> Load(string code)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                var asm = Assembly.GetExecutingAssembly();
                string? name = Array.Find(asm.GetManifestResourceNames(), n => n.EndsWith("." + code + ".lang", StringComparison.OrdinalIgnoreCase));
                if (name == null) return map;
                using (var s = asm.GetManifestResourceStream(name)!)
                using (var r = new StreamReader(s, Encoding.UTF8))
                {
                    string? key = null, line;
                    while ((line = r.ReadLine()) != null)
                    {
                        if (line.StartsWith("#")) continue;
                        if (line.StartsWith("=> ") && key != null) { map[Unescape(key)] = Unescape(line.Substring(3)); key = null; }
                        else if (line.Length > 0) key = line;
                    }
                }
            }
            catch (Exception) { }
            return map;
        }

        static string Unescape(string s) => s.Replace("\\n", "\n").Replace("\\t", "\t");

        public static string T(string zh) => _map != null && _map.TryGetValue(zh, out var v) ? v : zh;

        public static string F(string zhFormat, params object?[] args) => string.Format(T(zhFormat), args);

        public static bool IsTranslated => _map != null;
    }
}
