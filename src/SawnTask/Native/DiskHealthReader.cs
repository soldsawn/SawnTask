using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using SawnTask.Core;

namespace SawnTask.Native;

// The disk's own health report: the NVMe "SMART / Health" log page, or the SMART attribute
// table of a SATA disk. Windows may refuse this without administrator rights; then null.
public static class DiskHealthReader
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[] input, int inputSize, byte[] output, int outputSize, out int returned, IntPtr overlapped);

    const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400, IOCTL_STORAGE_PREDICT_FAILURE = 0x002D1100;
    const uint GENERIC_READ = 0x80000000, GENERIC_READ_WRITE = 0xC0000000;
    const int StorageDeviceProtocolSpecificProperty = 49;

    public static DiskHealth? Read(int diskNumber, string bus)
    {
        bool nvme = bus.Equals("NVMe", StringComparison.OrdinalIgnoreCase);
        // Querying with no access rights works without administrator rights on most systems; if not, ask for more.
        foreach (uint access in nvme ? new[] { 0u, GENERIC_READ, GENERIC_READ_WRITE } : new[] { GENERIC_READ, GENERIC_READ_WRITE })
        {
            using var handle = CreateFileW($@"\\.\PhysicalDrive{diskNumber}", access, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (handle.IsInvalid) continue;
            var health = nvme ? ReadNvme(handle) : ReadSata(handle);
            if (health is not null) return health;
        }
        return null;
    }

    // STORAGE_PROPERTY_QUERY (8 bytes) + STORAGE_PROTOCOL_SPECIFIC_DATA (40 bytes) ask for log page 2
    // of an NVMe disk; the answer is a STORAGE_PROTOCOL_DATA_DESCRIPTOR (8 bytes + the same 40) and the page.
    static DiskHealth? ReadNvme(SafeFileHandle handle)
    {
        const int Header = 8, Specific = 40, Page = 512;
        var buffer = new byte[Header + Specific + Page];
        BitConverter.GetBytes(StorageDeviceProtocolSpecificProperty).CopyTo(buffer, 0); // PropertyId; QueryType 0 = standard
        void Put(int index, int value) => BitConverter.GetBytes(value).CopyTo(buffer, Header + index * 4);
        Put(0, 3);        // ProtocolType: NVMe
        Put(1, 2);        // DataType: log page
        Put(2, 2);        // which page: SMART / Health information
        Put(3, 0);        // sub value
        Put(4, Specific); // where the data starts, counted from the start of this block
        Put(5, Page);     // how much data
        if (!DeviceIoControl(handle, IOCTL_STORAGE_QUERY_PROPERTY, buffer, buffer.Length, buffer, buffer.Length, out int returned, IntPtr.Zero)
            || returned < Header + Specific + 64)
            return null;

        int start = Header + BitConverter.ToInt32(buffer, Header + 4 * 4);
        if (start < 0 || start + 192 > buffer.Length) return null;
        ulong U(int offset) => BitConverter.ToUInt64(buffer, start + offset); // the low 8 of the 16 bytes
        int kelvin = BitConverter.ToUInt16(buffer, start + 1);
        return new DiskHealth(
            "NVMe",
            CriticalWarning: buffer[start],
            TemperatureC: kelvin > 0 ? kelvin - 273 : null,
            SpareLeftPercent: buffer[start + 3],
            SpareThresholdPercent: buffer[start + 4],
            PercentUsed: buffer[start + 5],
            TerabytesRead: U(32) * 512000.0 / 1e12,
            TerabytesWritten: U(48) * 512000.0 / 1e12,
            PowerCycles: (long)U(112),
            PowerOnHours: (long)U(128),
            UnsafeShutdowns: (long)U(144),
            MediaErrors: (long)U(160),
            PredictFailure: null,
            ReallocatedSectors: null,
            PendingSectors: null);
    }

    // STORAGE_PREDICT_FAILURE: a 4-byte verdict, then the 512-byte SMART attribute table
    // (two bytes of version, then 12-byte entries: id, flags, value, worst, six bytes raw, spare).
    static DiskHealth? ReadSata(SafeFileHandle handle)
    {
        var reply = new byte[4 + 512];
        if (!DeviceIoControl(handle, IOCTL_STORAGE_PREDICT_FAILURE, [], 0, reply, reply.Length, out int returned, IntPtr.Zero) || returned < 4)
            return null;
        bool predicted = BitConverter.ToUInt32(reply, 0) != 0;

        var raw = new Dictionary<int, (int Value, long Raw)>();
        for (int i = 0; i < 30; i++)
        {
            int at = 4 + 2 + i * 12;
            if (at + 12 > reply.Length) break;
            int id = reply[at];
            if (id == 0) continue;
            long rawValue = 0;
            for (int b = 5; b >= 0; b--) rawValue = (rawValue << 8) | reply[at + 5 + b];
            raw[id] = (reply[at + 3], rawValue);
        }

        long? Raw(int id) => raw.TryGetValue(id, out var r) ? r.Raw : null;
        // The wear attribute differs per maker; its normalised value counts down from 100.
        int? used = null;
        foreach (int id in new[] { 177, 231, 233 })
            if (raw.TryGetValue(id, out var w) && w.Value is > 0 and <= 100) { used = 100 - w.Value; break; }

        return new DiskHealth(
            "SMART",
            CriticalWarning: 0,
            TemperatureC: Raw(194) is long t ? (int)(t & 0xFF) : null,
            SpareLeftPercent: null,
            SpareThresholdPercent: null,
            PercentUsed: used,
            TerabytesRead: null,
            TerabytesWritten: null,
            PowerCycles: Raw(12),
            PowerOnHours: Raw(9) is long h ? h & 0xFFFFFFFF : null,
            UnsafeShutdowns: null,
            MediaErrors: Raw(187) ?? Raw(198),
            PredictFailure: predicted,
            ReallocatedSectors: Raw(5),
            PendingSectors: Raw(197));
    }
}
