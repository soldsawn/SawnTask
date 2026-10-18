using System.Globalization;
using System.Windows;

namespace SawnTask.Ui;

// Theme and language switching. App.xaml merges three dictionaries: [0] theme colours,
// [1] UI text, [2] styles. Replacing [0] or [1] makes every {DynamicResource ...} in the
// UI update at once, so no restart is needed.
public static class Appearance
{
    public static readonly string[] Languages = ["en", "tr"];
    public static readonly string[] Themes = ["Dark", "Light"];

    public static event Action? Changed;

    public static string Language { get; private set; } = "en";
    public static string Theme { get; private set; } = "Dark";

    // Turkish Windows starts in Turkish, everything else in English.
    public static string DefaultLanguage =>
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "tr" ? "tr" : "en";

    public static void Apply(string theme, string language)
    {
        theme = Themes.Contains(theme) ? theme : "Dark";
        language = Languages.Contains(language) ? language : "en";
        var merged = Application.Current.Resources.MergedDictionaries;
        if (theme != Theme || merged[0].Source is null)
            merged[0] = new ResourceDictionary { Source = new Uri($"Themes/{theme}.xaml", UriKind.Relative) };
        if (language != Language || merged[1].Source is null)
            merged[1] = new ResourceDictionary { Source = new Uri($"Strings/{language}.xaml", UriKind.Relative) };
        Theme = theme;
        Language = language;
        Changed?.Invoke();
    }
}

// Grid column headers: DataGrid columns are not part of the visual tree, so
// Header="{DynamicResource ...}" is read once and never follows a language switch.
// ui:LocHeader.Key="Col.Name" remembers the key and re-applies it after every switch.
public static class LocHeader
{
    static readonly List<WeakReference<System.Windows.Controls.DataGridColumn>> Columns = [];

    static LocHeader() => Appearance.Changed += Refresh;

    public static readonly DependencyProperty KeyProperty = DependencyProperty.RegisterAttached(
        "Key", typeof(string), typeof(LocHeader), new PropertyMetadata(null, OnKeyChanged));

    public static void SetKey(DependencyObject target, string value) => target.SetValue(KeyProperty, value);
    public static string GetKey(DependencyObject target) => (string)target.GetValue(KeyProperty);

    static void OnKeyChanged(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        if (target is not System.Windows.Controls.DataGridColumn column) return;
        column.Header = Loc.T((string)e.NewValue);
        Columns.Add(new WeakReference<System.Windows.Controls.DataGridColumn>(column));
    }

    static void Refresh()
    {
        Columns.RemoveAll(w => !w.TryGetTarget(out _));
        foreach (var weak in Columns)
            if (weak.TryGetTarget(out var column)) column.Header = Loc.T(GetKey(column));
    }
}

// Text for code: Loc.T("Nav.Summary"), Loc.F("Sum.MemDetail", used, total).
public static class Loc
{
    public static string T(string key) =>
        Application.Current?.TryFindResource(key) is string s ? s.Replace("\\n", "\n") : key;

    public static string F(string key, params object?[] args) =>
        string.Format(Appearance.Language == "tr" ? new CultureInfo("tr-TR") : CultureInfo.InvariantCulture, T(key), args);
}
