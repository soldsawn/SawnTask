using System.IO;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using SawnTask.Native;

namespace SawnTask.Core;

// What this computer is made of: processor, memory modules, graphics, disks, power, network and
// Windows itself (the System Info page and the facts under the Performance graphs). Read
// once at start-up and again when the user presses Refresh; nothing here changes while running,
// and nothing needs administrator rights. Every part is read on its own, so one that fails
// (a virtual machine without SMBIOS, a locked-down event log) only lands in Problems.
public sealed record MemoryModule(string Slot, long SizeBytes, string Type, string Form, int SpeedMTs, int ConfiguredMTs, string Manufacturer, string PartNumber);
public sealed record GpuInfo(string Name, string DriverVersion, string DriverDate, string Provider, long VramBytes);
public sealed record VolumeInfo(string Name, string Format, long TotalBytes, long FreeBytes, string Kind);
public sealed record PowerInfo(bool? OnAc, bool HasBattery, int? BatteryPercent, string Scheme);
public sealed record AdapterInfo(string Name, string Description, long SpeedBps, string Mac, List<string> Addresses, string Gateway, List<string> Dns);
public sealed record OsInfo(string Name, string Version, string Build, DateTime? Installed, string ComputerName, string UserName, string Architecture, string TimeZone);

public sealed class SystemInfoData
{
    // Processor
    public string CpuName { get; set; } = "";
    public string CpuVendor { get; set; } = "";
    public double BaseMhz { get; set; }
    public CpuTopology.Info Topology { get; set; } = new(1, 0, Environment.ProcessorCount, 0, 0, 0);
    public bool? VirtualizationEnabled { get; set; }
    public bool VirtualMachine { get; set; }

    // Memory
    public ulong TotalMemory { get; set; }
    public List<MemoryModule> Modules { get; } = [];

    // Machine
    public string SystemManufacturer { get; set; } = "";
    public string SystemProduct { get; set; } = "";
    public string BoardManufacturer { get; set; } = "";
    public string BoardProduct { get; set; } = "";
    public string BiosVendor { get; set; } = "";
    public string BiosVersion { get; set; } = "";
    public string BiosDate { get; set; } = "";

    // Devices
    public List<GpuInfo> Gpus { get; } = [];
    public List<StorageDevices.Disk> Disks { get; } = [];
    public List<VolumeInfo> Volumes { get; } = [];
    public PowerInfo? Power { get; set; }
    public List<AdapterInfo> Adapters { get; } = [];

    // Windows
    public OsInfo? Os { get; set; }
    public DateTime BootTimeUtc { get; set; }
    public ShutdownLog.Reason Shutdown { get; set; } = new(ShutdownLog.Kind.Unknown, "");
    public bool? FastStartup { get; set; }

    // Parts that could not be read, as "part: reason".
    public List<string> Problems { get; } = [];

    public TimeSpan Uptime => DateTime.UtcNow - BootTimeUtc;
}

public static class SystemInfo
{
    [DllImport("kernel32.dll")]
    static extern bool IsProcessorFeaturePresent(uint feature);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

    [StructLayout(LayoutKind.Sequential)]
    struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public uint BatteryLifeTime, BatteryFullLifeTime;
    }

    [DllImport("powrprof.dll")]
    static extern uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr schemeGuid);

    [DllImport("powrprof.dll")]
    static extern uint PowerReadFriendlyName(IntPtr root, ref Guid scheme, IntPtr subGroup, IntPtr setting, IntPtr buffer, ref uint size);

    // PROCESSOR_POWER_INFORMATION per logical processor: Number, MaxMhz, CurrentMhz, MhzLimit, MaxIdleState, CurrentIdleState.
    [DllImport("powrprof.dll")]
    static extern uint CallNtPowerInformation(int level, IntPtr input, uint inputSize, IntPtr output, uint outputSize);

    const int ProcessorInformation = 11;

    [DllImport("kernel32.dll")]
    static extern IntPtr LocalFree(IntPtr memory);

    [DllImport("ntdll.dll")]
    static extern int RtlGetVersion(ref OSVERSIONINFOW info);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct OSVERSIONINFOW
    {
        public uint Size, Major, Minor, Build, Platform;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string CSDVersion;
    }

    const uint PF_VIRT_FIRMWARE_ENABLED = 21;
    const string DisplayClass = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    // Called with each part's name just before it is read (the --sysinfo switch uses it to show where a slow part hangs).
    public static Action<string>? Trace { get; set; }

    static Task<SystemInfoData>? current;

    // The facts do not change while the program runs, so they are read once in the background and
    // shared by the System Info and Performance pages. refresh: read again (the Refresh button).
    public static Task<SystemInfoData> Load(bool refresh = false)
    {
        if (refresh || current is null) current = Task.Run(Collect);
        return current;
    }

    public static SystemInfoData Collect()
    {
        var d = new SystemInfoData { BootTimeUtc = DateTime.UtcNow.AddMilliseconds(-Environment.TickCount64) };
        void Part(string name, Action read)
        {
            Trace?.Invoke(name);
            try { read(); }
            catch (Exception e) { d.Problems.Add($"{name}: {e.Message}"); }
        }

        Part("processor", () => ReadProcessor(d));
        Part("topology", () => d.Topology = CpuTopology.Read());
        Part("memory", () =>
        {
            var status = new NativeMethods.MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<NativeMethods.MEMORYSTATUSEX>() };
            if (NativeMethods.GlobalMemoryStatusEx(ref status)) d.TotalMemory = status.ullTotalPhys;
        });
        Part("SMBIOS", () => ReadSmbios(d));
        Part("graphics", () => ReadGraphics(d));
        Part("storage", () => d.Disks.AddRange(StorageDevices.Read()));
        Part("volumes", () => ReadVolumes(d));
        Part("power", () => d.Power = ReadPower());
        Part("network", () => ReadNetwork(d));
        Part("Windows", () => ReadWindows(d));
        Part("shutdown reason", () => d.Shutdown = ShutdownLog.Read(d.BootTimeUtc));
        return d;
    }

    static void ReadProcessor(SystemInfoData d)
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
        d.CpuName = Collapse(key?.GetValue("ProcessorNameString") as string);
        d.CpuVendor = key?.GetValue("VendorIdentifier") as string ?? "";
        d.BaseMhz = key?.GetValue("~MHz") is int mhz ? mhz : 0; // measured at boot: only a fallback
        d.BaseMhz = RatedMhz() ?? d.BaseMhz;
        // With Hyper-V (or Windows' virtualization-based security) running, the firmware flag is
        // hidden from Windows, but then virtualization is obviously on.
        d.VirtualizationEnabled = IsProcessorFeaturePresent(PF_VIRT_FIRMWARE_ENABLED) || HypervisorRunning();
    }

    // The speed the processor is rated for, as Task Manager's "Base speed" (the power manager's MaxMhz).
    public static double? RatedMhz()
    {
        int count = Environment.ProcessorCount;
        IntPtr buffer = Marshal.AllocHGlobal(count * 24);
        try
        {
            if (CallNtPowerInformation(ProcessorInformation, IntPtr.Zero, 0, buffer, (uint)(count * 24)) != 0) return null;
            int mhz = Marshal.ReadInt32(buffer, 4);
            return mhz > 0 ? mhz : null;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    // CPUID leaf 1, ECX bit 31: "a hypervisor is present" (the host's own Hyper-V counts too).
    static bool HypervisorRunning() =>
        System.Runtime.Intrinsics.X86.X86Base.IsSupported && (System.Runtime.Intrinsics.X86.X86Base.CpuId(1, 0).Ecx & (1 << 31)) != 0;

    static void ReadSmbios(SystemInfoData d)
    {
        var table = Smbios.Read() ?? throw new InvalidOperationException("Windows did not provide the SMBIOS table");
        foreach (var s in table)
            switch (s.Type)
            {
                case 0: // BIOS
                    d.BiosVendor = s.Text(0x04);
                    d.BiosVersion = s.Text(0x05);
                    d.BiosDate = s.Text(0x08);
                    break;
                case 1: // System
                    d.SystemManufacturer = s.Text(0x04);
                    d.SystemProduct = s.Text(0x05);
                    break;
                case 2: // Baseboard
                    d.BoardManufacturer = s.Text(0x04);
                    d.BoardProduct = s.Text(0x05);
                    break;
                case 17: // Memory device: one per slot, empty slots have size 0
                    long size = s.U16(0x0C);
                    if (size == 0) break;
                    if (size == 0x7FFF) size = (long)s.U32(0x1C) * 1024 * 1024;       // extended size, in MB
                    else if ((size & 0x8000) != 0) size = (size & 0x7FFF) * 1024;      // in KB
                    else size *= 1024 * 1024;                                          // in MB
                    string slot = s.Text(0x10);
                    d.Modules.Add(new MemoryModule(slot, size, Smbios.MemoryType(s.U8(0x12)), Smbios.FormFactor(s.U8(0x0E)),
                        s.U16(0x15), s.U16(0x20), s.Text(0x17), s.Text(0x1A)));
                    break;
            }

        string maker = (d.SystemManufacturer + " " + d.SystemProduct).ToLowerInvariant();
        d.VirtualMachine = new[] { "vmware", "virtualbox", "innotek", "qemu", "kvm", "xen", "virtual machine", "parallels", "bochs", "google compute" }
            .Any(maker.Contains);
    }

    static void ReadGraphics(SystemInfoData d)
    {
        using var root = Registry.LocalMachine.OpenSubKey(DisplayClass);
        if (root is null) return;
        foreach (string name in root.GetSubKeyNames().Where(n => n.All(char.IsDigit)))
        {
            using var key = root.OpenSubKey(name);
            if (key?.GetValue("DriverDesc") is not string desc) continue;
            // The video memory size is stored as a 64-bit number (or, on older drivers, 32-bit).
            long vram = key.GetValue("HardwareInformation.qwMemorySize") switch
            {
                long q => q,
                byte[] b when b.Length >= 8 => BitConverter.ToInt64(b, 0),
                _ => key.GetValue("HardwareInformation.MemorySize") is int m ? (uint)m : 0,
            };
            d.Gpus.Add(new GpuInfo(desc, key.GetValue("DriverVersion") as string ?? "", key.GetValue("DriverDate") as string ?? "",
                key.GetValue("ProviderName") as string ?? "", vram));
        }
    }

    static void ReadVolumes(SystemInfoData d)
    {
        foreach (var drive in DriveInfo.GetDrives().Where(x => x.IsReady && x.DriveType is DriveType.Fixed or DriveType.Removable))
            d.Volumes.Add(new VolumeInfo(drive.Name, drive.DriveFormat, drive.TotalSize, drive.AvailableFreeSpace, drive.DriveType.ToString()));
    }

    static PowerInfo ReadPower()
    {
        bool? onAc = null;
        bool hasBattery = false;
        int? percent = null;
        if (GetSystemPowerStatus(out var status))
        {
            onAc = status.ACLineStatus switch { 0 => false, 1 => true, _ => null };
            hasBattery = status.BatteryFlag != 128 && status.BatteryFlag != 255; // 128: no battery
            if (hasBattery && status.BatteryLifePercent <= 100) percent = status.BatteryLifePercent;
        }
        return new PowerInfo(onAc, hasBattery, percent, ActivePowerPlan());
    }

    // The name of the active power plan ("Balanced", "High performance", ...).
    static string ActivePowerPlan()
    {
        if (PowerGetActiveScheme(IntPtr.Zero, out IntPtr pointer) != 0 || pointer == IntPtr.Zero) return "";
        try
        {
            var scheme = Marshal.PtrToStructure<Guid>(pointer);
            uint size = 0;
            PowerReadFriendlyName(IntPtr.Zero, ref scheme, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, ref size);
            if (size == 0) return scheme.ToString();
            IntPtr buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                return PowerReadFriendlyName(IntPtr.Zero, ref scheme, IntPtr.Zero, IntPtr.Zero, buffer, ref size) == 0
                    ? Marshal.PtrToStringUni(buffer) ?? ""
                    : scheme.ToString();
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        finally { LocalFree(pointer); }
    }

    static void ReadNetwork(SystemInfoData d)
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces()
                     .Where(n => n.OperationalStatus == OperationalStatus.Up
                                 && n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)))
        {
            var ip = nic.GetIPProperties();
            // Windows lists its filter layers and WAN miniports as adapters too; they have no address.
            if (!ip.UnicastAddresses.Any(a => a.Address.AddressFamily == AddressFamily.InterNetwork)) continue;
            string mac = string.Join(":", nic.GetPhysicalAddress().GetAddressBytes().Select(b => b.ToString("X2")));
            var addresses = ip.UnicastAddresses.Select(a => a.Address)
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork || (a.AddressFamily == AddressFamily.InterNetworkV6 && !a.IsIPv6LinkLocal))
                .Select(a => a.ToString()).ToList();
            string gateway = ip.GatewayAddresses.Select(g => g.Address).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)?.ToString() ?? "";
            var dns = ip.DnsAddresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork).Select(a => a.ToString()).ToList();
            d.Adapters.Add(new AdapterInfo(nic.Name, nic.Description, nic.Speed, mac, addresses, gateway, dns));
        }
    }

    static void ReadWindows(SystemInfoData d)
    {
        var version = new OSVERSIONINFOW { Size = (uint)Marshal.SizeOf<OSVERSIONINFOW>(), CSDVersion = "" };
        RtlGetVersion(ref version); // GetVersionEx lies to programs without a manifest; this one does not

        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        string name = key?.GetValue("ProductName") as string ?? "Windows";
        if (version.Build >= 22000) name = name.Replace("Windows 10", "Windows 11"); // the registry still says 10
        string display = key?.GetValue("DisplayVersion") as string ?? key?.GetValue("ReleaseId") as string ?? "";
        string build = $"{version.Build}" + (key?.GetValue("UBR") is int ubr ? $".{ubr}" : "");
        DateTime? installed = key?.GetValue("InstallDate") is int seconds ? DateTimeOffset.FromUnixTimeSeconds(seconds).LocalDateTime : null;

        d.Os = new OsInfo(name, display, build, installed, Environment.MachineName, Environment.UserName,
            Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit", TimeZoneInfo.Local.DisplayName);

        using var power = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\Power");
        if (power?.GetValue("HiberbootEnabled") is int fast) d.FastStartup = fast != 0;
    }

    // Registry and SMBIOS names carry padding and doubled spaces ("Intel(R) Core(TM)   i5-12450H").
    static string Collapse(string? text) =>
        string.Join(" ", (text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries));
}
