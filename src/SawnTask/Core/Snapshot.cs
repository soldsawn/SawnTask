namespace SawnTask.Core;

public enum SourceHealth { NotYetRun, Ok, Stale, Failed, Unavailable }

// Thrown by a provider whose data source does not exist on this machine (for example
// Intel Dynamic Tuning on an AMD PC). Shown as "Unavailable", not as a failure.
public sealed class SourceUnavailableException(string message) : Exception(message);

// One row of the "Sources" page, and what the green "sources healthy" text in the
// status bar counts.
public sealed record SourceStatus(string Id, string Description, SourceHealth Health, string Detail, DateTime LastGoodUtc)
{
    public string LastGoodText => LastGoodUtc == DateTime.MinValue ? Fmt.Missing : LastGoodUtc.ToLocalTime().ToString("HH:mm:ss");
}

// Windows reuses PIDs, so each PID is paired with its creation time: a recycled PID
// can never redirect an action (End task, priority, ...) to the wrong process.
public readonly record struct ProcessKey(uint Pid, long CreateTime);

public sealed record ProcessSample(
    ProcessKey Key,
    string Name,
    uint ParentPid,
    double? CpuPercent,
    long PrivateBytes,
    long WorkingSetBytes,
    double? IoReadPerSec,
    double? IoWritePerSec,
    uint Threads,
    uint Handles,
    uint SessionId,
    string User,
    TimeSpan CpuTime,
    long PeakWorkingSetBytes,
    uint PageFaults,
    string ImagePath,   // full path of the .exe ("" if Windows does not tell us); used for the icon
    bool IsApp);        // owns a visible top-level window: shown in the "Apps" group

// ApprovedHive/ApprovedKey/ApprovedValue say where the enabled flag lives, so it can be toggled.
public sealed record StartupItem(bool Enabled, string Name, string Command, string Location,
    string ApprovedHive, string ApprovedKey, string ApprovedValue);

public sealed record ServiceItem(string Name, string DisplayName, string State, uint Pid, string Kind);

public sealed record ConnectionItem(string Protocol, string State, string Local, string Remote, string Service,
    string Exposure, uint Pid, string Process);

public sealed record UserSession(uint SessionId, string User, string State, string Station, string Client);

public sealed record InstalledApp(string Name, string Publisher, string Version, string Installed, long? SizeBytes,
    string Scope, string Source)
{
    public string SizeText => SizeBytes is long b ? Fmt.Bytes(b) : Fmt.Missing;
}

public sealed record DriverItem(string DisplayName, string Name, string State, string StartType, string Kind,
    string Version, string Vendor, string Path);

// One temperature from Intel Dynamic Tuning. Source is "<participant>.<domain>", e.g. "TCPU.PKG".
public sealed record ThermalSensor(string Label, string Source, double Celsius, bool IsCpuPackage);

// One NVIDIA GPU as reported by NVML. Every value is optional: a field the driver
// refuses stays null instead of becoming 0.
public sealed record GpuDevice(string Name, double? TempC, double? PowerW, double? CoreMHz, double? MemMHz,
    ulong? VramUsed, ulong? VramTotal, double? Utilization);

// One power reading (watts) from Intel RAPL or NVIDIA NVML.
public sealed record PowerReading(string Label, string Source, double Watts);

// Everything measured in one tick. A null value means "not measured": the UI
// shows a dash and the graph shows a gap. It is never turned into a zero.
public sealed class Snapshot
{
    public long Generation { get; set; }
    public DateTime TimestampUtc { get; set; }
    public double IntervalMs { get; set; }
    public double ActualMs { get; set; }
    public int LogicalProcessors { get; set; }

    public double? CpuPercent { get; set; }
    public double? CpuKernelPercent { get; set; }
    public double[]? CoreUsage { get; set; }
    public double? CpuSpeedMhz { get; set; }

    public double? MemUsedPercent { get; set; }
    public ulong MemTotal { get; set; }
    public ulong MemUsed { get; set; }
    public ulong MemCached { get; set; }
    public uint Threads { get; set; }
    public uint Handles { get; set; }
    public uint ProcessCount { get; set; }
    public ulong MemCommitted { get; set; }
    public ulong MemCommitLimit { get; set; }
    public ulong MemPagedPool { get; set; }
    public ulong MemNonPagedPool { get; set; }

    public double? NetRecvPerSec { get; set; }
    public double? NetSentPerSec { get; set; }

    public double? DiskActivePercent { get; set; }
    public double? DiskReadPerSec { get; set; }
    public double? DiskWritePerSec { get; set; }

    public double? GpuPercent { get; set; }

    public double? CpuTempC { get; set; }
    public IReadOnlyList<ThermalSensor> Thermals { get; set; } = [];
    public double? CpuPowerW { get; set; }
    public IReadOnlyList<PowerReading> Power { get; set; } = [];
    public IReadOnlyList<GpuDevice> Gpus { get; set; } = [];

    public IReadOnlyList<ProcessSample> Processes { get; set; } = [];
    public IReadOnlyList<StartupItem> Startup { get; set; } = [];
    public IReadOnlyList<ServiceItem> Services { get; set; } = [];
    public IReadOnlyList<ConnectionItem> Connections { get; set; } = [];
    public IReadOnlyList<UserSession> Sessions { get; set; } = [];
    public IReadOnlyList<InstalledApp> InstalledApps { get; set; } = [];
    public IReadOnlyList<DriverItem> Drivers { get; set; } = [];
    public IReadOnlyList<SourceStatus> Sources { get; set; } = [];
}
