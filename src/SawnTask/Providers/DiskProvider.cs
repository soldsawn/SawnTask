using SawnTask.Core;
using SawnTask.Native;

namespace SawnTask.Providers;

// The PhysicalDisk performance counters: active time and throughput of all disks,
// no device handles needed.
public sealed class DiskProvider : IMetricProvider, IDisposable
{
    readonly PdhQuery query = new();
    readonly IntPtr idle, read, write;

    public DiskProvider()
    {
        idle = query.Add(@"\PhysicalDisk(_Total)\% Idle Time");
        read = query.Add(@"\PhysicalDisk(_Total)\Disk Read Bytes/sec");
        write = query.Add(@"\PhysicalDisk(_Total)\Disk Write Bytes/sec");
    }

    public string Id => "windows.disk";
    public string Description => @"PDH \PhysicalDisk(_Total): active time = 100 - % Idle Time, plus read/write bytes per second";
    public string Detail { get; private set; } = "";

    public void Collect(Snapshot s)
    {
        query.Collect();
        // Active = 100 - idle, like Task Manager; "% Disk Time" can exceed 100 on busy queues.
        double? idlePct = query.Value(idle);
        s.DiskActivePercent = idlePct is double v ? Math.Clamp(100 - v, 0, 100) : null;
        s.DiskReadPerSec = query.Value(read);
        s.DiskWritePerSec = query.Value(write);
        Detail = idlePct is null ? "waiting for a second sample" : "all physical disks combined";
    }

    public void Dispose() => query.Dispose();
}
