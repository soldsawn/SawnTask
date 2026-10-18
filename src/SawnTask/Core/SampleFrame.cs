namespace SawnTask.Core;

// PrivateBytes and ImagePath were added later; recordings made before have 0 and "".
public sealed record TopProcess(string Name, uint Pid, double Cpu, long PrivateBytes = 0, string ImagePath = "")
{
    // 0..1: how hot the row looks (set by the window, which smooths it between updates).
    [System.Text.Json.Serialization.JsonIgnore]
    public double Heat { get; set; }
}

// The small, graph-sized part of a Snapshot. History stores these, and the
// recorder writes one per line, so live view and replay share one code path.
public sealed class SampleFrame
{
    // The most rows the Top list can ever show (it shows as many as fit its card).
    public const int TopCount = 20;

    public long Generation { get; init; }
    public DateTime TimestampUtc { get; init; }
    public double IntervalMs { get; init; }
    public double ActualMs { get; init; }

    public double? Cpu { get; init; }
    public double? CpuKernel { get; init; }
    public double[]? Cores { get; init; }
    public double? CpuSpeed { get; init; }
    public double? Mem { get; init; }
    public ulong MemTotal { get; init; }
    public ulong MemUsed { get; init; }
    public double? NetRx { get; init; }
    public double? NetTx { get; init; }
    public double? DiskActive { get; init; }
    public double? DiskRead { get; init; }
    public double? DiskWrite { get; init; }
    public double? Gpu { get; init; }
    public double? CpuTemp { get; init; }
    public List<ThermalSensor> Thermals { get; init; } = [];
    public double? CpuPower { get; init; }
    public List<PowerReading> Power { get; init; } = [];
    public List<GpuDevice> Gpus { get; init; } = [];
    public List<TopProcess> Top { get; init; } = [];

    public static SampleFrame From(Snapshot s) => new()
    {
        Generation = s.Generation,
        TimestampUtc = s.TimestampUtc,
        IntervalMs = s.IntervalMs,
        ActualMs = s.ActualMs,
        Cpu = s.CpuPercent,
        CpuKernel = s.CpuKernelPercent,
        Cores = s.CoreUsage,
        CpuSpeed = s.CpuSpeedMhz,
        Mem = s.MemUsedPercent,
        MemTotal = s.MemTotal,
        MemUsed = s.MemUsed,
        NetRx = s.NetRecvPerSec,
        NetTx = s.NetSentPerSec,
        DiskActive = s.DiskActivePercent,
        DiskRead = s.DiskReadPerSec,
        DiskWrite = s.DiskWritePerSec,
        Gpu = s.GpuPercent,
        CpuTemp = s.CpuTempC,
        Thermals = s.Thermals.ToList(),
        CpuPower = s.CpuPowerW,
        Power = s.Power.ToList(),
        Gpus = s.Gpus.ToList(),
        Top = s.Processes
            .Where(p => p.Key.Pid != 0 && p.CpuPercent is not null)
            .OrderByDescending(p => p.CpuPercent)
            .Take(TopCount)
            .Select(p => new TopProcess(p.Name, p.Key.Pid, p.CpuPercent!.Value, p.PrivateBytes, p.ImagePath))
            .ToList(),
    };
}
