using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SawnTask.Ui;

// A one-line "type a name" dialog (WPF has none built in).
public sealed class PromptWindow : Window
{
    readonly TextBox box = new() { Margin = new Thickness(0, 8, 0, 14), MinWidth = 300, Padding = new Thickness(4, 3, 4, 3) };

    PromptWindow(string title, string label, string initial)
    {
        Title = title;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        UseLayoutRounding = true; // sharp text, as in the main window
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        SetResourceReference(BackgroundProperty, "Bg");
        SetResourceReference(ForegroundProperty, "Fg");

        box.Text = initial;
        var ok = new Button { Content = Loc.T("PP.Ok"), IsDefault = true, Padding = new Thickness(18, 4, 18, 4), Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = Loc.T("PP.Cancel"), IsCancel = true, Padding = new Thickness(18, 4, 18, 4) };
        ok.Click += (_, _) => DialogResult = true;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok, cancel } };
        Content = new StackPanel { Margin = new Thickness(18), Children = { new TextBlock { Text = label }, box, buttons } };
        Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
    }

    // The typed name, or null if the dialog was cancelled or the name is empty.
    public static string? Ask(Window owner, string title, string label, string initial)
    {
        var window = new PromptWindow(title, label, initial) { Owner = owner };
        return window.ShowDialog() == true && window.box.Text.Trim() is { Length: > 0 } name ? name : null;
    }
}
