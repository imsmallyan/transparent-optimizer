using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using PcOptimizer.Core;

namespace PcOptimizer
{
    static class UiText
    {
        static readonly ConditionalWeakTable<DependencyObject, Dictionary<DependencyProperty, (string Source, string Shown)>> _sources =
            new ConditionalWeakTable<DependencyObject, Dictionary<DependencyProperty, (string, string)>>();

        public static void Attach(Window w)
        {
            bool pending = false;
            void Schedule()
            {
                if (pending || !L.IsTranslated) return;
                pending = true;
                w.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => { pending = false; Walk(w); }));
            }
            w.LayoutUpdated += (s, e) => Schedule();
            Schedule();
        }

        public static void Retranslate(Window w) => Walk(w);

        static void Walk(DependencyObject d)
        {
            Translate(d);
            int n = VisualTreeHelper.GetChildrenCount(d);
            for (int i = 0; i < n; i++) Walk(VisualTreeHelper.GetChild(d, i));
        }

        public static void Translate(DependencyObject d)
        {
            switch (d)
            {
                case TextBlock tb:
                    if (tb.Inlines.Count > 1 || tb.Inlines.FirstInline is Span) TranslateInlines(tb.Inlines);
                    else Prop(tb, TextBlock.TextProperty);
                    break;
                case ComboBox cb:
                    bool changed = false;
                    foreach (var item in cb.Items)
                        if (item is ComboBoxItem ci) changed |= Prop(ci, ContentControl.ContentProperty);
                    if (changed && cb.SelectedIndex >= 0)
                    {
                        int i = cb.SelectedIndex;
                        cb.SelectedIndex = -1;
                        cb.SelectedIndex = i;
                    }
                    break;
                case HeaderedItemsControl hi:
                    Prop(hi, HeaderedItemsControl.HeaderProperty);
                    break;
                case Window w:
                    Prop(w, Window.TitleProperty);
                    break;
                case ContentControl cc:
                    Prop(cc, ContentControl.ContentProperty);
                    if (cc is HeaderedContentControl hc) Prop(hc, HeaderedContentControl.HeaderProperty);
                    break;
            }
            if (d is FrameworkElement fe) Prop(fe, FrameworkElement.ToolTipProperty);
        }

        static void TranslateInlines(InlineCollection inlines)
        {
            foreach (var inline in inlines)
            {
                if (inline is Run r) Prop(r, Run.TextProperty);
                else if (inline is Span sp) TranslateInlines(sp.Inlines);
            }
        }

        static bool Prop(DependencyObject o, DependencyProperty p)
        {
            if (BindingOperations.IsDataBound(o, p)) return false;
            if (!(o.GetValue(p) is string cur) || cur.Length == 0) return false;
            _sources.TryGetValue(o, out var map);
            string source = map != null && map.TryGetValue(p, out var rec) && rec.Shown == cur ? rec.Source : cur;
            string t = L.T(source);
            if (t == cur) return false;
            if (map == null) _sources.Add(o, map = new Dictionary<DependencyProperty, (string, string)>());
            map[p] = (source, t);
            o.SetValue(p, t);
            return true;
        }
    }
}
