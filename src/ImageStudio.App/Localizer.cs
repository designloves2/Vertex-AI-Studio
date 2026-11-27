using ImageStudio.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;

namespace ImageStudio.App;

/// <summary>Translates the Korean text written in XAML (MainWindow.xaml) with <see cref="L"/> once after load.</summary>
static class Localizer
{
    public static void Apply(DependencyObject? node)
    {
        if (node == null || L.IsKorean) return;
        switch (node)
        {
            case TextBlock tb:
                if (tb.Inlines.Count <= 1) tb.Text = L.T(tb.Text);
                else foreach (var r in tb.Inlines.OfType<Run>()) r.Text = L.T(r.Text);
                break;
            case TextBox t:
                t.PlaceholderText = L.T(t.PlaceholderText);
                if (t.Header is string th) t.Header = L.T(th);
                break;
            case AutoSuggestBox a:
                a.PlaceholderText = L.T(a.PlaceholderText);
                break;
            case ComboBox cb:
                cb.PlaceholderText = L.T(cb.PlaceholderText);
                break;
            case ToggleSwitch ts:
                if (ts.OnContent is string on) ts.OnContent = L.T(on);
                if (ts.OffContent is string off) ts.OffContent = L.T(off);
                if (ts.Header is string h) ts.Header = L.T(h);
                break;
            case InfoBar ib:
                ib.Title = L.T(ib.Title);
                ib.Message = L.T(ib.Message);
                break;
            case Expander ex:
                if (ex.Header is string eh) ex.Header = L.T(eh);
                break;
        }
        if (node is FrameworkElement fe)
        {
            if (ToolTipService.GetToolTip(fe) is string tip) ToolTipService.SetToolTip(fe, L.T(tip));
            var an = AutomationProperties.GetName(fe);
            if (!string.IsNullOrEmpty(an)) AutomationProperties.SetName(fe, L.T(an));
        }
        if (node is ContentControl { Content: string s } cc) cc.Content = L.T(s);

        // Walk the logical content (works for collapsed parts that are not in the visual tree yet).
        switch (node)
        {
            case Panel p: foreach (var c in p.Children) Apply(c); break;
            case Border b: Apply(b.Child); break;
            case ScrollViewer sv: Apply(sv.Content as DependencyObject); break;
            case Expander e: Apply(e.Content as DependencyObject); if (e.Header is DependencyObject hd) Apply(hd); break;
            case ContentControl c2: Apply(c2.Content as DependencyObject); break;
        }
    }
}
