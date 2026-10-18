using System.ComponentModel;
using System.Runtime.InteropServices;
using SawnTask.Core;
using static SawnTask.Native.NativeMethods;

namespace SawnTask.Providers;

// Physical memory from GlobalMemoryStatusEx; cache, commit, pools, threads and handles from K32GetPerformanceInfo.
public sealed class MemoryProvider : IMetricProvider
{
    public string Id => "windows.memory";
    public string Description => "GlobalMemoryStatusEx for physical memory; K32GetPerformanceInfo for cache, threads, handles";
    public string Detail { get; private set; } = "";

    public void Collect(Snapshot s)
    {
        var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref status))
            throw new Win32Exception(Marshal.GetLastWin32Error());

        s.MemTotal = status.ullTotalPhys;
        s.MemUsed = status.ullTotalPhys - status.ullAvailPhys;
        s.MemUsedPercent = 100.0 * s.MemUsed / s.MemTotal;

        if (GetPerformanceInfo(out var perf, (uint)Marshal.SizeOf<PERFORMANCE_INFORMATION>()))
        {
            s.MemCached = (ulong)perf.SystemCache * (ulong)perf.PageSize;
            s.Threads = perf.ThreadCount;
            s.Handles = perf.HandleCount;
            s.ProcessCount = perf.ProcessCount;
            ulong page = perf.PageSize;
            s.MemCommitted = perf.CommitTotal * page;
            s.MemCommitLimit = perf.CommitLimit * page;
            s.MemPagedPool = perf.KernelPaged * page;
            s.MemNonPagedPool = perf.KernelNonpaged * page;
        }
        Detail = $"{Fmt.Bytes(s.MemTotal)} physical";
    }
}
