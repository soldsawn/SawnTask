using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace SawnTask.Native;

// Physical disks: model, bus, SSD or hard disk, size. Opening \\.\PhysicalDriveN with access 0
// asks Windows about the device without reading it, so no administrator rights are needed.
public static class StorageDevices
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[] input, int inputSize, byte[] output, int outputSize, out int returned, IntPtr overlapped);

    const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400;
    const uint IOCTL_DISK_GET_DRIVE_GEOMETRY_EX = 0x000700A0;
    const uint FILE_SHARE_READ_WRITE = 3, OPEN_EXISTING = 3;
    const int StorageDeviceProperty = 0, StorageDeviceSeekPenaltyProperty = 7;

    public sealed record Disk(int Number, string Model, string Revision, string Serial, string Bus, string Kind, long SizeBytes);

    internal static List<Disk> Read()
    {
        var disks = new List<Disk>();
        for (int n = 0; n < 16; n++)
        {
            using var handle = CreateFileW($@"\\.\PhysicalDrive{n}", 0, FILE_SHARE_READ_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (handle.IsInvalid) continue;

            // STORAGE_DEVICE_DESCRIPTOR: the text fields are offsets from the start of the reply.
            var reply = new byte[1024];
            if (!DeviceIoControl(handle, IOCTL_STORAGE_QUERY_PROPERTY, Query(StorageDeviceProperty), 12, reply, reply.Length, out _, IntPtr.Zero))
                continue;
            string vendor = AsciiAt(reply, BitConverter.ToInt32(reply, 12));
            string product = AsciiAt(reply, BitConverter.ToInt32(reply, 16));
            string revision = AsciiAt(reply, BitConverter.ToInt32(reply, 20));
            string serial = AsciiAt(reply, BitConverter.ToInt32(reply, 24)).TrimEnd('.'); // NVMe pads the number with a dot
            string bus = BusName(BitConverter.ToInt32(reply, 28));

            // DEVICE_SEEK_PENALTY_DESCRIPTOR: a disk with no seek penalty has no moving parts.
            string kind = "";
            var seek = new byte[16];
            if (DeviceIoControl(handle, IOCTL_STORAGE_QUERY_PROPERTY, Query(StorageDeviceSeekPenaltyProperty), 12, seek, seek.Length, out _, IntPtr.Zero))
                kind = seek[8] == 0 ? "SSD" : "HDD";

            // DISK_GEOMETRY_EX: 24 bytes of geometry, then the disk size.
            long size = 0;
            var geometry = new byte[64];
            if (DeviceIoControl(handle, IOCTL_DISK_GET_DRIVE_GEOMETRY_EX, [], 0, geometry, geometry.Length, out _, IntPtr.Zero))
                size = BitConverter.ToInt64(geometry, 24);

            string model = $"{vendor} {product}".Trim();
            disks.Add(new Disk(n, model.Length > 0 ? model : $"PhysicalDrive{n}", revision, serial, bus, kind, size));
        }
        return disks;
    }

    // STORAGE_PROPERTY_QUERY: PropertyId, QueryType (0 = standard) and one spare byte, padded to 12.
    static byte[] Query(int propertyId)
    {
        var query = new byte[12];
        BitConverter.GetBytes(propertyId).CopyTo(query, 0);
        return query;
    }

    static string AsciiAt(byte[] buffer, int offset)
    {
        if (offset <= 0 || offset >= buffer.Length) return "";
        int end = offset;
        while (end < buffer.Length && buffer[end] != 0) end++;
        return Encoding.ASCII.GetString(buffer, offset, end - offset).Trim();
    }

    static string BusName(int code) => code switch
    {
        1 => "SCSI", 2 => "ATAPI", 3 => "ATA", 4 => "IEEE 1394", 5 => "SSA", 6 => "Fibre Channel", 7 => "USB",
        8 => "RAID", 9 => "iSCSI", 10 => "SAS", 11 => "SATA", 12 => "SD", 13 => "MMC", 14 => "Virtual",
        15 => "File-backed virtual", 16 => "Storage Spaces", 17 => "NVMe", 18 => "SCM", 19 => "UFS", _ => "",
    };
}
