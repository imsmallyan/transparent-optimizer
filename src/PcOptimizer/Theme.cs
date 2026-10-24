using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using PcOptimizer.Core;

namespace PcOptimizer
{
    static class Theme
    {
        public static readonly (string Code, string Name)[] Modes =
        {
            ("system", "跟随系统"), ("light", "亮色"), ("dark", "暗色"),
        };

        public static string Mode { get; private set; } = "system";
        public static bool IsDark { get; private set; }

        static readonly List<Window> _windows = new List<Window>();
        static bool _applied;

        static readonly Dictionary<string, Color> Light = new Dictionary<string, Color>
        {
            ["WindowBg"] = C("#F3F3F3"), ["PageBg"] = C("#F9F9F9"), ["CardBg"] = C("#FFFFFF"), ["Line"] = C("#E5E5E5"),
            ["TextMain"] = C("#1B1B1B"), ["TextSub"] = C("#5F5F5F"), ["Subtle"] = C("#F0F0F0"),
            ["Hover"] = C("#0A000000"), ["Selected"] = C("#0F000000"),
            ["ControlBg"] = C("#FBFBFB"), ["ControlHover"] = C("#F3F3F3"), ["ControlBorder"] = C("#DCDCDC"),
            ["StrongBorder"] = C("#8A8A8A"), ["Track"] = C("#E3E3E3"), ["ScrollThumb"] = C("#8A8A8A"),
            ["Good"] = C("#0F7B0F"), ["Warn"] = C("#9D5D00"), ["Bad"] = C("#C42B1C"),
            ["GoodLight"] = C("#DFF6DD"), ["WarnLight"] = C("#FFF4CE"), ["BadLight"] = C("#FDE7E9"),
            ["CloseHover"] = C("#C42B1C"), ["OnAccent"] = C("#FFFFFF"),
        };

        static readonly Dictionary<string, Color> Dark = new Dictionary<string, Color>
        {
            ["WindowBg"] = C("#202020"), ["PageBg"] = C("#272727"), ["CardBg"] = C("#2E2E2E"), ["Line"] = C("#3A3A3A"),
            ["TextMain"] = C("#FFFFFF"), ["TextSub"] = C("#C2C2C2"), ["Subtle"] = C("#3A3A3A"),
            ["Hover"] = C("#0FFFFFFF"), ["Selected"] = C("#17FFFFFF"),
            ["ControlBg"] = C("#363636"), ["ControlHover"] = C("#3C3C3C"), ["ControlBorder"] = C("#444444"),
            ["StrongBorder"] = C("#9E9E9E"), ["Track"] = C("#454545"), ["ScrollThumb"] = C("#9E9E9E"),
            ["Good"] = C("#6CCB5F"), ["Warn"] = C("#FCE100"), ["Bad"] = C("#FF99A4"),
            ["GoodLight"] = C("#393D1B"), ["WarnLight"] = C("#433519"), ["BadLight"] = C("#442726"),
            ["CloseHover"] = C("#C42B1C"), ["OnAccent"] = C("#000000"),
        };

        public static void Init()
        {
            string? saved = AppSettings.Get("theme");
            Mode = Array.Exists(Modes, m => m.Code == saved) ? saved! : "system";
            Apply();
            SystemEvents.UserPreferenceChanged += (s, e) =>
            {
                if (e.Category == UserPreferenceCategory.General || e.Category == UserPreferenceCategory.Color)
                    Application.Current?.Dispatcher.BeginInvoke(new Action(Apply));
            };
        }

        public static bool Set(string mode)
        {
            Mode = mode;
            Apply();
            return AppSettings.Set("theme", mode);
        }

        static void Apply()
        {
            bool dark = Mode == "dark" || (Mode == "system" && SystemPrefersDark());
            var colors = new Dictionary<string, Color>(dark ? Dark : Light);
            var p = AccentPalette();
            Color accent = dark ? p[1] : p[4];
            colors["Primary"] = accent;
            colors["PrimaryHover"] = Mix(accent, colors["PageBg"], 0.12);
            colors["PrimaryDark"] = dark ? p[0] : p[5];
            colors["PrimaryLight"] = Mix(p[3], colors["CardBg"], dark ? 0.78 : 0.88);

            var res = Application.Current.Resources.MergedDictionaries[0];
            foreach (var kv in colors)
            {
                if (_applied && res[kv.Key] is SolidColorBrush b && !b.IsFrozen) b.Color = kv.Value;
                else res[kv.Key] = new SolidColorBrush(kv.Value);
            }
            _applied = true;
            IsDark = dark;
            foreach (var w in _windows.ToArray()) ApplyChrome(w);
        }

        public static void Track(Window w)
        {
            _windows.Add(w);
            w.Closed += (s, e) => _windows.Remove(w);
            if (new WindowInteropHelper(w).Handle != IntPtr.Zero) ApplyChrome(w);
            else w.SourceInitialized += (s, e) => ApplyChrome(w);
        }

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        static void ApplyChrome(Window w)
        {
            var h = new WindowInteropHelper(w).Handle;
            if (h == IntPtr.Zero) return;
            try
            {
                int dark = IsDark ? 1 : 0;
                if (DwmSetWindowAttribute(h, 20, ref dark, 4) != 0) DwmSetWindowAttribute(h, 19, ref dark, 4);
                int round = 2;
                DwmSetWindowAttribute(h, 33, ref round, 4);
            }
            catch (Exception) { }
        }

        static bool SystemPrefersDark()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                    return k?.GetValue("AppsUseLightTheme") is int v && v == 0;
            }
            catch (Exception) { return false; }
        }

        static Color[] AccentPalette()
        {
            var colors = new[] { C("#99EBFF"), C("#4CC2FF"), C("#0091F8"), C("#0078D4"), C("#0067C0"), C("#003E92"), C("#001A68"), C("#F7630C") };
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent"))
                    if (k?.GetValue("AccentPalette") is byte[] b && b.Length >= 32)
                        for (int i = 0; i < 8; i++) colors[i] = Color.FromRgb(b[i * 4], b[i * 4 + 1], b[i * 4 + 2]);
            }
            catch (Exception) { }
            return colors;
        }

        static Color Mix(Color a, Color b, double t) => Color.FromRgb(
            (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

        static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);
    }
}
