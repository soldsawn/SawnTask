using System.Runtime.InteropServices;
using static SawnTask.Native.NativeMethods;

namespace SawnTask.Native;

// Thin wrapper over a PDH (performance counter) query, so providers read like
// "add counter, collect, read".
public sealed class PdhQuery : IDisposable
{
    readonly IntPtr query;

    public PdhQuery()
    {
        Check(PdhOpenQueryW(null, IntPtr.Zero, out query), "PdhOpenQuery");
    }

    // "English" counter names work on every Windows display language
    // (your services list is Turkish; the counter names still are not).
    public IntPtr Add(string path)
    {
        Check(PdhAddEnglishCounterW(query, path, IntPtr.Zero, out IntPtr counter), path);
        return counter;
    }

    public void Collect() => Check(PdhCollectQueryData(query), "PdhCollectQueryData");

    // Rate counters ("per second", "% utilization") need two collections before
    // they have a value. Until then this returns null, never 0.
    public double? Value(IntPtr counter)
    {
        uint status = PdhGetFormattedCounterValue(counter, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, out _, out var value);
        return status == 0 && value.CStatus <= PDH_CSTATUS_NEW_DATA ? value.DoubleValue : null;
    }

    // Wildcard counters such as "\GPU Engine(*)\..." return one value per instance.
    public List<(string Instance, double Value)> Values(IntPtr counter)
    {
        var result = new List<(string, double)>();
        uint size = 0;
        uint status = PdhGetFormattedCounterArrayW(counter, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref size, out _, IntPtr.Zero);
        if (status != PDH_MORE_DATA || size == 0) return result;

        IntPtr buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            status = PdhGetFormattedCounterArrayW(counter, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref size, out uint count, buffer);
            if (status != 0) return result;

            // PDH_FMT_COUNTERVALUE_ITEM_W on x64: name pointer (8) + CStatus (4) + padding (4) + double (8) = 24 bytes.
            const int ItemSize = 24;
            for (int i = 0; i < count; i++)
            {
                IntPtr item = buffer + i * ItemSize;
                uint cstatus = (uint)Marshal.ReadInt32(item, 8);
                if (cstatus > PDH_CSTATUS_NEW_DATA) continue;
                string name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(item, 0)) ?? "";
                double value = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(item, 16));
                result.Add((name, value));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
        return result;
    }

    public void Dispose() => PdhCloseQuery(query);

    static void Check(uint status, string what)
    {
        if (status != 0) throw new InvalidOperationException($"{what} failed (PDH status 0x{status:X8})");
    }
}
