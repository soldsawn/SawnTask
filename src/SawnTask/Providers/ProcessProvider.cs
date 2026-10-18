using System.Diagnostics;
using System.Runtime.InteropServices;
using SawnTask.Core;
using static SawnTask.Native.NativeMethods;

namespace SawnTask.Providers;

// NtQuerySystemInformation(SystemProcessInformation): one call returns a linked list with every
// process on the machine, without opening any of them, so even protected
// processes (csrss, System) appear with CPU and memory.
public sealed class ProcessProvider : IMetricProvider, IDisposable
{
    // Field offsets inside one SYSTEM_PROCESS_INFORMATION record (x64).
    const int OffNextEntry = 0;    // uint: bytes to the next record, 0 = last
    const int OffThreadCount = 4;  // uint
    const int OffPrivateWs = 8;    // long: private working set (what Task Manager calls "Memory")
    const int OffCreateTime = 32;  // long: FILETIME, 100 ns since 1601
    const int OffUserTime = 40;    // long: 100 ns of user-mode CPU
    const int OffKernelTime = 48;  // long: 100 ns of kernel-mode CPU
    const int OffImageName = 56;   // UNICODE_STRING { ushort Length; ushort Max; pad; char* Buffer @ +8 }
    const int OffPid = 80;         // HANDLE (8 bytes)
    const int OffParentPid = 88;   // HANDLE
    const int OffHandleCount = 96; // uint
    const int OffSessionId = 100;  // uint
    const int OffPageFaults = 128; // uint: PageFaultCount
    const int OffPeakWorkingSet = 136; // SIZE_T
    const int OffWorkingSet = 144; // SIZE_T
    const int OffReadBytes = 232;  // LARGE_INTEGER: ReadTransferCount
    const int OffWriteBytes = 240; // LARGE_INTEGER: WriteTransferCount

    readonly Stopwatch clock = Stopwatch.StartNew();
    readonly int processors = Environment.ProcessorCount;
    readonly ProcessOwners users = new();
    IntPtr buffer;
    uint bufferSize = 1 << 20;
    Dictionary<ProcessKey, (long Cpu, long Read, long Write)> previous = [];
    double previousSeconds = double.NaN;

    public string Id => "windows.process";
    public string Description => "NtQuerySystemInformation(SystemProcessInformation): every process in one call, no handles opened";
    public string Detail { get; private set; } = "";

    public void Collect(Snapshot s)
    {
        Query();
        double nowSeconds = clock.Elapsed.TotalSeconds;
        double dt = double.IsNaN(previousSeconds) ? 0 : nowSeconds - previousSeconds;
        previousSeconds = nowSeconds;

        var list = new List<ProcessSample>(400);
        var next = new Dictionary<ProcessKey, (long, long, long)>(previous.Count + 16);
        var withWindow = Native.AppWindows.ProcessIds();
        IntPtr record = buffer;

        while (true)
        {
            uint pid = (uint)Marshal.ReadIntPtr(record, OffPid).ToInt64();
            var key = new ProcessKey(pid, Marshal.ReadInt64(record, OffCreateTime));
            long cpu = Marshal.ReadInt64(record, OffUserTime) + Marshal.ReadInt64(record, OffKernelTime);
            long read = Marshal.ReadInt64(record, OffReadBytes);
            long write = Marshal.ReadInt64(record, OffWriteBytes);

            // Rates need the same process (same key!) in the previous sample.
            double? cpuPct = null, readPs = null, writePs = null;
            if (dt > 0 && previous.TryGetValue(key, out var old))
            {
                cpuPct = Math.Clamp((cpu - old.Cpu) / (dt * 1e7 * processors) * 100.0, 0, 100);
                readPs = Math.Max(0, (read - old.Read) / dt);
                writePs = Math.Max(0, (write - old.Write) / dt);
            }
            next[key] = (cpu, read, write);

            var identity = users.Get(key);
            list.Add(new ProcessSample(
                key,
                ReadName(record),
                (uint)Marshal.ReadIntPtr(record, OffParentPid).ToInt64(),
                cpuPct,
                Marshal.ReadInt64(record, OffPrivateWs),
                Marshal.ReadInt64(record, OffWorkingSet),
                readPs,
                writePs,
                (uint)Marshal.ReadInt32(record, OffThreadCount),
                (uint)Marshal.ReadInt32(record, OffHandleCount),
                (uint)Marshal.ReadInt32(record, OffSessionId),
                identity.User,
                TimeSpan.FromTicks(cpu), // both are 100 ns units
                Marshal.ReadInt64(record, OffPeakWorkingSet),
                (uint)Marshal.ReadInt32(record, OffPageFaults),
                identity.ImagePath,
                withWindow.Contains(pid)));

            int nextOffset = Marshal.ReadInt32(record, OffNextEntry);
            if (nextOffset == 0) break;
            record += nextOffset;
        }

        previous = next;
        users.Forget(next.Keys);
        s.Processes = list;
        Detail = $"{list.Count} processes, buffer {Fmt.Bytes(bufferSize)}";
    }

    // The table size is not known in advance: ask, and if Windows answers
    // STATUS_INFO_LENGTH_MISMATCH, grow the buffer and ask again.
    void Query()
    {
        while (true)
        {
            if (buffer == IntPtr.Zero) buffer = Marshal.AllocHGlobal((int)bufferSize);
            uint status = NtQuerySystemInformation(SystemProcessInformation, buffer, bufferSize, out uint needed);
            if (status == STATUS_SUCCESS) return;
            if (status != STATUS_INFO_LENGTH_MISMATCH)
                throw new InvalidOperationException($"NtQuerySystemInformation failed: NTSTATUS 0x{status:X8}");

            Marshal.FreeHGlobal(buffer);
            buffer = IntPtr.Zero;
            bufferSize = Math.Max(needed + 64 * 1024, bufferSize * 2);
        }
    }

    static string ReadName(IntPtr record)
    {
        ushort lengthBytes = (ushort)Marshal.ReadInt16(record, OffImageName);
        IntPtr text = Marshal.ReadIntPtr(record, OffImageName + 8);
        return text == IntPtr.Zero || lengthBytes == 0
            ? "System Idle Process"
            : Marshal.PtrToStringUni(text, lengthBytes / 2);
    }

    public void Dispose()
    {
        if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
    }
}
