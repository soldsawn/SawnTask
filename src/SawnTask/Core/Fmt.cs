namespace SawnTask.Core;

public static class Fmt
{
    public const string Missing = "—";

    static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];

    public static string Pct(double? value) => value is double v ? $"{v:0.0}%" : Missing;

    public static string Bytes(double? value)
    {
        if (value is not double v) return Missing;
        int unit = 0;
        while (v >= 1024 && unit < Units.Length - 1)
        {
            v /= 1024;
            unit++;
        }
        return unit == 0 ? $"{v:0} {Units[unit]}" : $"{v:0.0} {Units[unit]}";
    }

    public static string Rate(double? value) => value is null ? Missing : Bytes(value) + "/s";
}
