using System.ComponentModel;
using System.Numerics;
using System.Runtime.InteropServices;

namespace SawnTask.Native;

// Sockets, cores and caches, from GetLogicalProcessorInformationEx.
// The call fills a buffer with variable-sized records; each starts with the record's kind
// (Relationship, 4 bytes) and its size (4 bytes).
public static class CpuTopology
{
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetLogicalProcessorInformationEx(int relationship, IntPtr buffer, ref uint length);

    const int RelationProcessorCore = 0, RelationCache = 2, RelationProcessorPackage = 3, RelationAll = 0xFFFF;
    const int ERROR_INSUFFICIENT_BUFFER = 122;

    public sealed record Info(int Sockets, int PhysicalCores, int LogicalProcessors, long L1Bytes, long L2Bytes, long L3Bytes);

    internal static Info Read()
    {
        uint length = 0;
        GetLogicalProcessorInformationEx(RelationAll, IntPtr.Zero, ref length);
        if (Marshal.GetLastWin32Error() != ERROR_INSUFFICIENT_BUFFER || length == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error());

        IntPtr buffer = Marshal.AllocHGlobal((int)length);
        try
        {
            if (!GetLogicalProcessorInformationEx(RelationAll, buffer, ref length))
                throw new Win32Exception(Marshal.GetLastWin32Error());

            int sockets = 0, cores = 0, logical = 0;
            long l1 = 0, l2 = 0, l3 = 0;
            for (int offset = 0; offset < length;)
            {
                int relationship = Marshal.ReadInt32(buffer, offset);
                int size = Marshal.ReadInt32(buffer, offset + 4);
                if (size <= 0) break;
                switch (relationship)
                {
                    case RelationProcessorPackage:
                        sockets++;
                        break;
                    case RelationProcessorCore:
                        // PROCESSOR_RELATIONSHIP after the 8-byte header: Flags, EfficiencyClass,
                        // 20 reserved bytes, GroupCount (2 bytes, at +22), then GROUP_AFFINITY
                        // entries of 16 bytes (an 8-byte mask, a group number, 6 reserved bytes).
                        cores++;
                        int groups = Marshal.ReadInt16(buffer, offset + 8 + 22);
                        for (int g = 0; g < groups; g++)
                            logical += BitOperations.PopCount((ulong)Marshal.ReadInt64(buffer, offset + 8 + 24 + g * 16));
                        break;
                    case RelationCache:
                        // CACHE_RELATIONSHIP after the header: Level (1 byte), Associativity (1),
                        // LineSize (2), CacheSize (4, at +4), Type (4, at +8).
                        int level = Marshal.ReadByte(buffer, offset + 8);
                        long bytes = (uint)Marshal.ReadInt32(buffer, offset + 8 + 4);
                        if (level == 1) l1 += bytes;
                        else if (level == 2) l2 += bytes;
                        else if (level == 3) l3 += bytes;
                        break;
                }
                offset += size;
            }
            return new Info(Math.Max(1, sockets), cores, logical > 0 ? logical : Environment.ProcessorCount, l1, l2, l3);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
