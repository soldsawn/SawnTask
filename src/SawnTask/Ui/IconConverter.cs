using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace SawnTask.Ui;

// {Binding ImagePath, Converter={StaticResource IconOf}}: the program icon for an .exe path.
public sealed class IconConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        IconCache.For(value as string ?? "");

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

// {Binding Heat, Converter={StaticResource HeatBrush}}: a green wash that grows with the value.
public sealed class HeatBrushConverter : IValueConverter
{
    static readonly SolidColorBrush[] Steps = Enumerable.Range(0, 33)
        .Select(i => { var b = new SolidColorBrush(System.Windows.Media.Color.FromArgb((byte)(i * 2), 0x4C, 0xAF, 0x50)); b.Freeze(); return b; })
        .ToArray();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        Steps[(int)Math.Round(Math.Clamp(value is double h ? h : 0, 0, 1) * 32)];

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

// {Binding PrivateBytes, Converter={StaticResource ByteText}}: "123.4 MB".
public sealed class ByteTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        Core.Fmt.Bytes(value is long b && b > 0 ? b : null);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
