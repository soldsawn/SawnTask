using System.IO;
using SawnTask.Core;

namespace SawnTask;

// "SawnTask.exe --sysinfo out.txt": print everything SystemInfo.Collect reads, so the values can
// be checked against Task Manager or the device's own labels without opening the window.
public static class SystemInfoDump
{
    public static int Run(TextWriter o)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        SystemInfo.Trace = part => { o.WriteLine($"{watch.ElapsedMilliseconds,6} ms  reading {part}"); o.Flush(); };
        var d = SystemInfo.Collect();
        o.WriteLine($"collected in {watch.ElapsedMilliseconds} ms");

        o.WriteLine($"cpu: {d.CpuName} ({d.CpuVendor}), base {d.BaseMhz:0} MHz, virtualization {d.VirtualizationEnabled}, vm {d.VirtualMachine}");
        var t = d.Topology;
        o.WriteLine($"topology: {t.Sockets} socket(s), {t.PhysicalCores} cores, {t.LogicalProcessors} logical, L1 {Fmt.Bytes(t.L1Bytes)}, L2 {Fmt.Bytes(t.L2Bytes)}, L3 {Fmt.Bytes(t.L3Bytes)}");
        o.WriteLine($"memory: {Fmt.Bytes(d.TotalMemory)} in {d.Modules.Count} module(s)");
        foreach (var m in d.Modules)
            o.WriteLine($"  {m.Slot}: {Fmt.Bytes(m.SizeBytes)} {m.Type} {m.Form} {m.SpeedMTs}/{m.ConfiguredMTs} MT/s {m.Manufacturer} {m.PartNumber}");
        o.WriteLine($"system: {d.SystemManufacturer} {d.SystemProduct}; board {d.BoardManufacturer} {d.BoardProduct}; bios {d.BiosVendor} {d.BiosVersion} {d.BiosDate}");
        foreach (var g in d.Gpus) o.WriteLine($"gpu: {g.Name}, driver {g.DriverVersion} {g.DriverDate} ({g.Provider}), vram {Fmt.Bytes(g.VramBytes)}");
        foreach (var k in d.Disks) o.WriteLine($"disk {k.Number}: {k.Model} rev {k.Revision} serial {k.Serial} {k.Bus} {k.Kind} {Fmt.Bytes(k.SizeBytes)}");
        foreach (var v in d.Volumes) o.WriteLine($"volume {v.Name} {v.Format} {v.Kind}: {Fmt.Bytes(v.FreeBytes)} free of {Fmt.Bytes(v.TotalBytes)}");
        if (d.Power is { } p) o.WriteLine($"power: ac {p.OnAc}, battery {p.HasBattery} {p.BatteryPercent}%, plan '{p.Scheme}'");
        foreach (var a in d.Adapters) o.WriteLine($"net: {a.Name} / {a.Description}, {a.SpeedBps / 1_000_000} Mb/s, {a.Mac}, {string.Join(", ", a.Addresses)}, gw {a.Gateway}, dns {string.Join(", ", a.Dns)}");
        if (d.Os is { } os) o.WriteLine($"os: {os.Name} {os.Version} build {os.Build}, installed {os.Installed}, {os.ComputerName}/{os.UserName}, {os.Architecture}, {os.TimeZone}");
        o.WriteLine($"boot: {d.BootTimeUtc:u}, uptime {d.Uptime.ToString(@"d\:hh\:mm\:ss")}, last shutdown {d.Shutdown.Kind} '{d.Shutdown.Type}', fast startup {d.FastStartup}");
        foreach (string problem in d.Problems) o.WriteLine($"PROBLEM {problem}");
        return d.Problems.Count == 0 ? 0 : 1;
    }
}
