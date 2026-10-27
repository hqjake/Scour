using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;

namespace Scour.App.Services;

/// <summary>
/// Translates the interface. English text is the key: each language file maps it to the translation, and
/// anything without one stays English. Fixed texts in the views are translated as they load; page names and
/// other bound texts go through <see cref="T"/> or the "Tr" converter.
/// </summary>
public static class Loc
{
    public sealed record LanguageOption(string Code, string Name);

    /// <summary>"" follows Windows' display language.</summary>
    public static readonly LanguageOption[] Languages =
    [
        new("", "Same as Windows"),
        new("en", "English"),
        new("de", "Deutsch"),
        new("es", "Español"),
        new("fr", "Français"),
        new("it", "Italiano"),
        new("pt", "Português"),
    ];

    private static Dictionary<string, string> _strings = new();

    /// <summary>The language in use ("en" when no translation is loaded).</summary>
    public static string Current { get; private set; } = "en";

    public static bool IsTranslating => _strings.Count > 0;

    /// <param name="setting">The saved choice; "" picks Windows' display language if Scour has it.</param>
    public static void Initialize(string? setting)
    {
        var code = string.IsNullOrEmpty(setting) ? CultureInfo.CurrentUICulture.TwoLetterISOLanguageName : setting;
        Current = "en";
        _strings = new();
        if (code == "en" || Languages.All(l => l.Code != code)) return;
        try
        {
            var stream = Application.GetResourceStream(new Uri($"pack://application:,,,/Scour;component/Assets/Lang/{code}.json"))?.Stream;
            if (stream is null) return;
            using (stream)
                _strings = JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? new();
            Current = code;
            EventManager.RegisterClassHandler(typeof(FrameworkElement), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnLoaded));
        }
        catch (Exception e)
        {
            ErrorLog.Write("Loading translations", e);
            _strings = new();
        }
    }

    /// <summary>The translation of an English interface text (keeping its surrounding spaces), or the text itself.</summary>
    public static string T(string english)
    {
        if (_strings.Count == 0 || string.IsNullOrWhiteSpace(english)) return english;
        var trimmed = english.Trim();
        if (!_strings.TryGetValue(trimmed, out var t) || t.Length == 0) return english;
        if (trimmed.Length == english.Length) return t;
        int lead = english.Length - english.TrimStart().Length;
        return english[..lead] + t + english[(lead + trimmed.Length)..];
    }

    // ---------------------------------------------------------------- views

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        try { Translate((DependencyObject)sender); }
        catch { /* never break a view over a translation */ }
    }

    private static void Translate(DependencyObject d)
    {
        switch (d)
        {
            case TextBlock tb:
                if (IsLiteral(tb, TextBlock.TextProperty) && tb.Inlines.Count <= 1) Set(tb, TextBlock.TextProperty, tb.Text);
                else foreach (var run in tb.Inlines.OfType<Run>())
                        if (IsLiteral(run, Run.TextProperty)) Set(run, Run.TextProperty, run.Text);
                break;
            case HeaderedContentControl h when h.Header is string s && IsLiteral(h, HeaderedContentControl.HeaderProperty):
                h.Header = T(s);
                break;
            case HeaderedItemsControl h when h.Header is string s && IsLiteral(h, HeaderedItemsControl.HeaderProperty):
                h.Header = T(s);
                break;
            case ContentControl c when c.Content is string s && IsLiteral(c, ContentControl.ContentProperty):
                c.Content = T(s);
                break;
            case DataGrid grid:
                foreach (var col in grid.Columns)
                    if (col.Header is string s && IsLiteral(col, DataGridColumn.HeaderProperty)) col.Header = T(s);
                break;
        }
        if (d is FrameworkElement fe && fe.ToolTip is string tip && IsLiteral(fe, FrameworkElement.ToolTipProperty)) fe.ToolTip = T(tip);
        if (d is Window w && IsLiteral(w, Window.TitleProperty)) w.Title = T(w.Title);
    }

    private static void Set(DependencyObject d, DependencyProperty p, string value)
    {
        var t = T(value);
        if (!ReferenceEquals(t, value)) d.SetValue(p, t);
    }

    /// <summary>
    /// Only texts written into the view itself are replaced: a binding, a style or a trigger owns its value,
    /// and overwriting it would break it.
    /// </summary>
    private static bool IsLiteral(DependencyObject d, DependencyProperty p)
    {
        var source = DependencyPropertyHelper.GetValueSource(d, p);
        return !source.IsExpression && !source.IsAnimated && !source.IsCoerced &&
               source.BaseValueSource is BaseValueSource.Local or BaseValueSource.ParentTemplate;
    }
}
