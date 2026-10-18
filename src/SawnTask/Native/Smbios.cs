using System.Runtime.InteropServices;
using System.Text;

namespace SawnTask.Native;

// The firmware's own description of the machine (SMBIOS), read with GetSystemFirmwareTable("RSMB").
// The table is a list of structures: a formatted part of known length, then text strings that
// are referred to by number (1 = first string) and end with an empty string (two zero bytes).
internal static class Smbios
{
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern uint GetSystemFirmwareTable(uint signature, uint tableId, byte[]? buffer, uint size);

    const uint RSMB = 0x52534D42; // 'R' 'S' 'M' 'B'

    internal sealed record Structure(byte Type, byte[] Data, List<string> Strings)
    {
        public byte U8(int offset) => offset < Data.Length ? Data[offset] : (byte)0;
        public ushort U16(int offset) => offset + 2 <= Data.Length ? BitConverter.ToUInt16(Data, offset) : (ushort)0;
        public uint U32(int offset) => offset + 4 <= Data.Length ? BitConverter.ToUInt32(Data, offset) : 0;

        // The text the byte at `offset` points to ("" when it is 0 or out of range).
        public string Text(int offset)
        {
            int index = U8(offset);
            return index >= 1 && index <= Strings.Count ? Strings[index - 1].Trim() : "";
        }
    }

    // null when Windows does not give the table (some virtual machines, very old firmware).
    internal static List<Structure>? Read()
    {
        uint size = GetSystemFirmwareTable(RSMB, 0, null, 0);
        if (size < 8) return null;
        var raw = new byte[size];
        if (GetSystemFirmwareTable(RSMB, 0, raw, size) == 0) return null;

        // RawSMBIOSData: UsedMethod, Major, Minor, DmiRevision (1 byte each), Length (4), then the data.
        int end = (int)Math.Min(size, 8 + BitConverter.ToUInt32(raw, 4));
        var result = new List<Structure>();
        int pos = 8;
        while (pos + 4 <= end)
        {
            byte type = raw[pos], length = raw[pos + 1];
            if (length < 4 || pos + length > end) break;
            var data = raw[pos..(pos + length)];

            var strings = new List<string>();
            int p = pos + length;
            if (p < end && raw[p] == 0) p += 2; // no strings: two zero bytes
            else
                while (p < end && raw[p] != 0)
                {
                    int start = p;
                    while (p < end && raw[p] != 0) p++;
                    strings.Add(Encoding.Latin1.GetString(raw, start, p - start));
                    p++; // the string's terminator
                    if (p < end && raw[p] == 0) { p++; break; } // empty string: end of this structure
                }

            result.Add(new Structure(type, data, strings));
            if (type == 127) break; // end-of-table marker
            pos = p;
        }
        return result;
    }

    // Memory type and form factor codes (SMBIOS specification, "Memory Device").
    internal static string MemoryType(byte code) => code switch
    {
        0x12 => "DDR", 0x13 => "DDR2", 0x18 => "DDR3", 0x1A => "DDR4", 0x1B => "LPDDR", 0x1C => "LPDDR2",
        0x1D => "LPDDR3", 0x1E => "LPDDR4", 0x20 => "HBM", 0x21 => "HBM2", 0x22 => "DDR5", 0x23 => "LPDDR5",
        _ => "",
    };

    internal static string FormFactor(byte code) => code switch
    {
        0x09 => "DIMM", 0x0D => "SODIMM", 0x0F => "FB-DIMM", 0x0B => "RIMM", 0x0C => "SODIMM",
        0x0E => "SRIMM", 0x10 => "Row of chips", 0x12 or 0x13 => "Soldered", _ => "",
    };
}
