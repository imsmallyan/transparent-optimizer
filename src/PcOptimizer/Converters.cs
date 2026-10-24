using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using PcOptimizer.Core;

namespace PcOptimizer
{
    public class LevelToBrush : IValueConverter
    {
        public object Convert(object value, Type t, object p, CultureInfo c)
        {
            string key = value is CheckLevel l
                ? (l == CheckLevel.Bad ? "Bad" : l == CheckLevel.Warn ? "Warn" : "Good")
                : "TextSub";
            return Application.Current.FindResource(key);
        }
        public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotSupportedException();
    }

    public class LevelToGlyph : IValueConverter
    {
        public object Convert(object value, Type t, object p, CultureInfo c) =>
            value is CheckLevel l && l != CheckLevel.Good ? "" : "";
        public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotSupportedException();
    }

    public class ToVisibility : IValueConverter
    {
        public object Convert(object value, Type t, object p, CultureInfo c)
        {
            bool show = value switch
            {
                null => false,
                string s => s.Length > 0,
                bool b => b,
                int i => i != 0,
                _ => true,
            };
            if (p as string == "invert") show = !show;
            return show ? Visibility.Visible : Visibility.Collapsed;
        }
        public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotSupportedException();
    }
}
