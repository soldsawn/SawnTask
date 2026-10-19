using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace SawnTask.Ui;

// A message box in the app's own theme (the Windows one ignores Dark/Light). Same call shape as
// MessageBox.Show; ExtraButton adds one more action button (e.g. "Open folder") before the others.
public sealed class ThemedMessageBox : Window
{
    MessageBoxResult result = MessageBoxResult.None;

    ThemedMessageBox(string text, string caption, MessageBoxButton buttons, MessageBoxImage icon,
        MessageBoxResult defaultResult, (string Label, Action Run)? extra)
    {
        Title = caption;
        SizeToContent = SizeToContent.WidthAndHeight;
        MaxWidth = 520;
        MinWidth = 320;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "Bg");
        SetResourceReference(ForegroundProperty, "Fg");

        var message = new TextBlock { Text = text.Replace("\\", "\\​"), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 420 };
        var body = new DockPanel { Margin = new Thickness(0, 0, 0, 18) };
        if (Glyph(icon) is { } g)
        {
            var symbol = new TextBlock
            {
                Text = g.Glyph,
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = 28,
                Foreground = new SolidColorBrush(g.Color),
                Margin = new Thickness(0, 0, 14, 0),
                VerticalAlignment = VerticalAlignment.Top,
            };
            DockPanel.SetDock(symbol, Dock.Left);
            body.Children.Add(symbol);
        }
        body.Children.Add(message);

        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        if (extra is { } e) row.Children.Add(MakeButton(e.Label, MessageBoxResult.None, false, false, e.Run));
        (MessageBoxResult, string)[] shown = buttons switch
        {
            MessageBoxButton.OK => [(MessageBoxResult.OK, "PP.Ok")],
            MessageBoxButton.OKCancel => [(MessageBoxResult.OK, "PP.Ok"), (MessageBoxResult.Cancel, "PP.Cancel")],
            MessageBoxButton.YesNo => [(MessageBoxResult.Yes, "Msg.Yes"), (MessageBoxResult.No, "Msg.No")],
            _ => [(MessageBoxResult.Yes, "Msg.Yes"), (MessageBoxResult.No, "Msg.No"), (MessageBoxResult.Cancel, "PP.Cancel")],
        };
        // The default button is the one with focus; without a stated default the first one.
        var first = defaultResult != MessageBoxResult.None ? defaultResult : shown[0].Item1;
        foreach (var (value, key) in shown)
        {
            bool cancels = value == MessageBoxResult.Cancel || buttons == MessageBoxButton.OK
                           || (buttons == MessageBoxButton.YesNo && value == MessageBoxResult.No);
            row.Children.Add(MakeButton(Loc.T(key), value, value == first, cancels, null));
        }

        Content = new StackPanel { Margin = new Thickness(20, 18, 20, 16), Children = { body, row } };
        // Closing with the title-bar X or Esc counts as Cancel, or No / OK when those are the only way out.
        Closing += (_, _) =>
        {
            if (result == MessageBoxResult.None)
                result = buttons switch
                {
                    MessageBoxButton.OK => MessageBoxResult.OK,
                    MessageBoxButton.YesNo => MessageBoxResult.No,
                    _ => MessageBoxResult.Cancel,
                };
        };
    }

    Button MakeButton(string label, MessageBoxResult value, bool isDefault, bool isCancel, Action? run)
    {
        var button = new Button
        {
            Content = label,
            IsDefault = isDefault,
            IsCancel = isCancel,
            Padding = new Thickness(18, 4, 18, 4),
            MinWidth = 78,
            Margin = new Thickness(8, 0, 0, 0),
        };
        button.Click += (_, _) =>
        {
            if (run is not null) { run(); return; } // an extra action leaves the box open
            result = value;
            DialogResult = true;
        };
        if (isDefault) Loaded += (_, _) => button.Focus();
        return button;
    }

    static (string Glyph, Color Color)? Glyph(MessageBoxImage icon) => icon switch
    {
        MessageBoxImage.Error => ("", Color.FromRgb(0xE5, 0x48, 0x4D)),
        MessageBoxImage.Warning => ("", Color.FromRgb(0xF2, 0xA6, 0x2C)),
        MessageBoxImage.Question => ("", Color.FromRgb(0x4C, 0x8D, 0xE0)),
        MessageBoxImage.Information => ("", Color.FromRgb(0x4C, 0x8D, 0xE0)),
        _ => null,
    };

    // For --snapshot: shows the box without waiting, saves a picture of it, closes it.
    internal static void SaveSnapshot(string path, string text, string caption, MessageBoxButton buttons,
        MessageBoxImage icon, (string Label, Action Run)? extra = null)
    {
        var box = new ThemedMessageBox(text, caption, buttons, icon, MessageBoxResult.None, extra) { WindowStartupLocation = WindowStartupLocation.CenterScreen };
        box.Show();
        box.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        box.UpdateLayout();
        var content = (FrameworkElement)VisualTreeHelper.GetChild(box, 0); // the window's root, with its background
        var dpi = VisualTreeHelper.GetDpi(box);
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
            (int)(content.ActualWidth * dpi.DpiScaleX), (int)(content.ActualHeight * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var png = new System.Windows.Media.Imaging.PngBitmapEncoder();
        png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using (var file = System.IO.File.Create(path)) png.Save(file);
        box.Close();
    }

    public static MessageBoxResult Show(Window? owner, string text, string caption,
        MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage icon = MessageBoxImage.None,
        MessageBoxResult defaultResult = MessageBoxResult.None, (string Label, Action Run)? extraButton = null)
    {
        var box = new ThemedMessageBox(text, caption, buttons, icon, defaultResult, extraButton);
        if (owner is { IsLoaded: true }) box.Owner = owner;
        else box.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        box.ShowDialog();
        return box.result;
    }
}
