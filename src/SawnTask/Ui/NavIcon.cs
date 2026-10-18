using System.Windows;

namespace SawnTask.Ui;

// A menu item's icon: ui:NavIcon.Glyph="&#xE80F;" is a Segoe Fluent Icons / Segoe MDL2 Assets
// character. Styles.xaml (NavItem) draws it left of the item's text.
public static class NavIcon
{
    public static readonly DependencyProperty GlyphProperty = DependencyProperty.RegisterAttached(
        "Glyph", typeof(string), typeof(NavIcon), new PropertyMetadata(""));

    public static void SetGlyph(DependencyObject target, string value) => target.SetValue(GlyphProperty, value);
    public static string GetGlyph(DependencyObject target) => (string)target.GetValue(GlyphProperty);
}
