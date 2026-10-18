using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using SawnTask.Core;

namespace SawnTask.Providers;

// Network byte counters from GetIfTable2 (IPHLPAPI). (.NET's NetworkInterface class
// rebuilds every adapter object on each call, which took 45 ms per tick; this takes well under 1 ms.)
public sealed class NetworkProvider : IMetricProvider
{
    // MIB_IF_ROW2 on x64 is 1352 bytes; these are the offsets of the fields that are used.
    const int RowSize = 1352, FlagsAt = 1152, TypeAt = 1128, OperStatusAt = 1156, InOctetsAt = 1208, OutOctetsAt = 1280, FirstRowAt = 8;
    const int IfTypeLoopback = 24, IfTypeTunnel = 131, OperUp = 1;
    const int FilterInterface = 2, EndPointInterface = 128; // bits of InterfaceAndOperStatusFlags

    [DllImport("iphlpapi.dll")] static extern uint GetIfTable2(out IntPtr table);
    [DllImport("iphlpapi.dll")] static extern void FreeMibTable(IntPtr memory);

    readonly Stopwatch clock = Stopwatch.StartNew();
    long previousRx, previousTx;
    double previousSeconds = double.NaN;

    public string Id => "windows.network";
    public string Description => "GetIfTable2 per-interface byte counters, summed over active adapters";
    public string Detail { get; private set; } = "";

    // The byte counters of the active adapters, summed. Filter layers and endpoints are skipped:
    // they carry the same traffic as the adapter they sit on.
    public static (long Received, long Sent, int Adapters) ReadCounters()
    {
        uint status = GetIfTable2(out IntPtr table);
        if (status != 0) throw new Win32Exception((int)status);
        try
        {
            long rx = 0, tx = 0;
            int adapters = 0, count = Marshal.ReadInt32(table);
            for (int i = 0; i < count; i++)
            {
                IntPtr row = table + FirstRowAt + i * RowSize;
                int type = Marshal.ReadInt32(row, TypeAt), flags = Marshal.ReadByte(row, FlagsAt);
                if (Marshal.ReadInt32(row, OperStatusAt) != OperUp) continue;
                if (type is IfTypeLoopback or IfTypeTunnel || (flags & (FilterInterface | EndPointInterface)) != 0) continue;
                rx += Marshal.ReadInt64(row, InOctetsAt);
                tx += Marshal.ReadInt64(row, OutOctetsAt);
                adapters++;
            }
            return (rx, tx, adapters);
        }
        finally { FreeMibTable(table); }
    }

    public void Collect(Snapshot s)
    {
        var (rx, tx, adapters) = ReadCounters();

        double now = clock.Elapsed.TotalSeconds;
        if (!double.IsNaN(previousSeconds) && now > previousSeconds)
        {
            double dt = now - previousSeconds;
            s.NetRecvPerSec = Math.Max(0, (rx - previousRx) / dt);
            s.NetSentPerSec = Math.Max(0, (tx - previousTx) / dt);
        }
        (previousRx, previousTx, previousSeconds) = (rx, tx, now);
        Detail = $"{adapters} active adapters";
    }
}
