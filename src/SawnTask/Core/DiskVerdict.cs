namespace SawnTask.Core;

public enum VerdictLevel { Fast, Normal, Low }

// What a disk speed result means for that kind of disk. ClassKey and HintKey are text keys
// (Strings/*.xaml); HintKey is set only when the result is lower than expected.
public sealed record DiskVerdict(VerdictLevel Level, string ClassKey, string? HintKey)
{
    // Sequential read (MB/s) a disk of each kind should reach: at "Fast" it is in the top range
    // for its kind, below "Normal" something holds it back.
    static readonly Dictionary<string, (double Fast, double Normal)> Expected = new()
    {
        ["Nvme"] = (3500, 1500),
        ["Sata"] = (480, 350),
        ["Hdd"] = (140, 60),
        ["Usb"] = (350, 80),
    };

    // bus: "NVMe", "SATA", "USB"... and kind: "SSD" / "HDD" / "" as StorageDevices reports them.
    public static string? ClassOf(string bus, string kind) =>
        bus.Equals("USB", StringComparison.OrdinalIgnoreCase) ? "Usb"
        : bus.Equals("NVMe", StringComparison.OrdinalIgnoreCase) ? "Nvme"
        : kind == "HDD" ? "Hdd"
        : kind == "SSD" ? "Sata"
        : null;

    // null when the kind of disk is unknown (no fair comparison possible).
    public static DiskVerdict? Evaluate(string bus, string kind, double seqReadMBps)
    {
        if (ClassOf(bus, kind) is not { } kindName) return null;
        var (fast, normal) = Expected[kindName];
        var level = seqReadMBps >= fast ? VerdictLevel.Fast : seqReadMBps >= normal ? VerdictLevel.Normal : VerdictLevel.Low;
        return new DiskVerdict(level, "DB.Class." + kindName, level == VerdictLevel.Low ? "DB.Hint." + kindName : null);
    }
}
