using System.Runtime.InteropServices;
using SawnTask.Core;
using SawnTask.Native;
using static SawnTask.Native.NativeMethods;

namespace SawnTask.Providers;

// GetSystemTimes for the total and NtQuerySystemInformation for the per-core graphs.
public sealed class CpuProvider : IMetricProvider, IDisposable
{
    // SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION: Idle, Kernel, User, Dpc, Interrupt
    // (8 bytes each) + InterruptCount (4) + padding (4) = 48 bytes per processor.
    const int CoreRecordSize = 48;

    readonly int bufferSize = Environment.ProcessorCount * CoreRecordSize;
    readonly IntPtr buffer;
    long prevIdle, prevKernel, prevUser;
    long[] prevCoreIdle = [], prevCoreTotal = [];
    bool hasPrevious;

    // Current speed = base speed x "% Processor Performance" / 100 (the counter Task Manager's Speed uses).
    PdhQuery? speedQuery;
    IntPtr speedCounter;
    double baseMhz;

    public CpuProvider()
    {
        buffer = Marshal.AllocHGlobal(bufferSize);
        try
        {
            baseMhz = SystemInfo.RatedMhz() ?? 0;
            speedQuery = new PdhQuery();
            speedCounter = speedQuery.Add(@"\Processor Information(_Total)\% Processor Performance");
        }
        catch { speedQuery?.Dispose(); speedQuery = null; }
    }

    public string Id => "windows.cpu";
    public string Description => "GetSystemTimes for total/kernel; NtQuerySystemInformation(SystemProcessorPerformanceInformation) per core";
    public string Detail { get; private set; } = "";

    public void Collect(Snapshot s)
    {
        // All three are counters of 100 ns units since boot. Kernel time INCLUDES idle time.
        if (!GetSystemTimes(out long idle, out long kernel, out long user))
            throw new InvalidOperationException("GetSystemTimes failed");

        if (hasPrevious)
        {
            long dIdle = idle - prevIdle, dKernel = kernel - prevKernel, dUser = user - prevUser;
            long total = dKernel + dUser;
            if (total > 0)
            {
                s.CpuPercent = 100.0 * (total - dIdle) / total;
                s.CpuKernelPercent = 100.0 * (dKernel - dIdle) / total;
            }
        }
        (prevIdle, prevKernel, prevUser) = (idle, kernel, user);

        s.CoreUsage = ReadCores();
        if (speedQuery is not null && baseMhz > 0)
        {
            speedQuery.Collect();
            if (speedQuery.Value(speedCounter) is double perf) s.CpuSpeedMhz = baseMhz * perf / 100;
        }
        hasPrevious = true;
    }

    double[]? ReadCores()
    {
        uint status = NtQuerySystemInformation(SystemProcessorPerformanceInformation, buffer, (uint)bufferSize, out uint returned);
        if (status != STATUS_SUCCESS)
        {
            Detail = $"per-core read failed: NTSTATUS 0x{status:X8}";
            return null;
        }

        // This information class reports the calling thread's processor group
        // (up to 64 logical processors). Machines with more than that are not
        // handled.
        int count = (int)(returned / CoreRecordSize);
        Detail = $"{count} logical processors";
        if (prevCoreIdle.Length != count)
        {
            prevCoreIdle = new long[count];
            prevCoreTotal = new long[count];
        }

        var usage = new double[count];
        for (int i = 0; i < count; i++)
        {
            int at = i * CoreRecordSize;
            long idle = Marshal.ReadInt64(buffer, at + 0);
            long total = Marshal.ReadInt64(buffer, at + 8) + Marshal.ReadInt64(buffer, at + 16);
            long dTotal = total - prevCoreTotal[i];
            usage[i] = dTotal > 0 ? 100.0 * (dTotal - (idle - prevCoreIdle[i])) / dTotal : 0;
            prevCoreIdle[i] = idle;
            prevCoreTotal[i] = total;
        }
        return hasPrevious ? usage : null;
    }

    public void Dispose()
    {
        speedQuery?.Dispose();
        Marshal.FreeHGlobal(buffer);
    }
}
