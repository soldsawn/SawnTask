using System.IO;
using SawnTask.Core;

namespace SawnTask;

public static class SelfTest
{
    public static int Run(TextWriter o)
    {
        int failures = 0;
        using var collector = new Collector();

        o.WriteLine($"WPF render tier {System.Windows.Media.RenderCapability.Tier >> 16} (2 = hardware, 0 = software)");
        // Rates need two samples; the third shows steady state.
        Snapshot s = collector.CollectOnce();
        o.WriteLine($"tick 1: CPU = {Fmt.Pct(s.CpuPercent)} (expected '{Fmt.Missing}': no previous sample yet)");
        Thread.Sleep(1000);
        collector.CollectOnce();
        Thread.Sleep(1000);
        s = collector.CollectOnce();
        // The Intel Dynamic Tuning worker connects in the background; give it a few ticks.
        for (int i = 0; i < 5 && s.CpuTempC is null; i++)
        {
            Thread.Sleep(1000);
            s = collector.CollectOnce();
        }

        o.WriteLine($"generation {s.Generation}, interval {s.IntervalMs} ms, actual {s.ActualMs:0} ms");
        o.WriteLine();
        o.WriteLine("SOURCES");
        foreach (var src in s.Sources)
        {
            o.WriteLine($"  [{src.Health,-8}] {src.Id,-18} {src.Detail}");
            if (src.Health is SourceHealth.Failed) failures++;
        }

        o.WriteLine();
        o.WriteLine("VALUES");
        o.WriteLine($"  CPU        {Fmt.Pct(s.CpuPercent)} (kernel {Fmt.Pct(s.CpuKernelPercent)}), {s.CoreUsage?.Length ?? 0} cores");
        o.WriteLine($"  Memory     {Fmt.Bytes(s.MemUsed)} / {Fmt.Bytes(s.MemTotal)} ({Fmt.Pct(s.MemUsedPercent)}), cached {Fmt.Bytes(s.MemCached)}");
        o.WriteLine($"  CPU speed  {(s.CpuSpeedMhz is double mhz ? $"{mhz / 1000:0.00} GHz" : Fmt.Missing)}");
        o.WriteLine($"  Mem detail commit {Fmt.Bytes(s.MemCommitted)} / {Fmt.Bytes(s.MemCommitLimit)}, paged {Fmt.Bytes(s.MemPagedPool)}, non-paged {Fmt.Bytes(s.MemNonPagedPool)}");
        o.WriteLine($"  Network    down {Fmt.Rate(s.NetRecvPerSec)}  up {Fmt.Rate(s.NetSentPerSec)}");
        o.WriteLine($"  Disk       active {Fmt.Pct(s.DiskActivePercent)}  read {Fmt.Rate(s.DiskReadPerSec)}  write {Fmt.Rate(s.DiskWritePerSec)}");
        o.WriteLine($"  GPU        {Fmt.Pct(s.GpuPercent)}");
        o.WriteLine($"  CPU temp   {(s.CpuTempC is double t ? $"{t:0.0} °C" : Fmt.Missing)}" +
                    (s.Thermals.Count > 0 ? "   [" + string.Join(", ", s.Thermals.Select(x => $"{x.Label} {x.Celsius:0.0}")) + "]" : ""));
        o.WriteLine($"  CPU power  {(s.CpuPowerW is double w ? $"{w:0.0} W" : Fmt.Missing)}   [{string.Join(", ", s.Power.Select(p => $"{p.Label} {p.Watts:0.0} W"))}]");
        foreach (var g in s.Gpus)
            o.WriteLine($"  NVIDIA     {g.Name}: {g.TempC} °C, {g.PowerW} W, {g.CoreMHz} MHz core, {g.MemMHz} MHz mem, VRAM {Fmt.Bytes(g.VramUsed)} / {Fmt.Bytes(g.VramTotal)}, util {g.Utilization}%");
        o.WriteLine($"  Processes  {s.Processes.Count}, threads {s.Threads}, handles {s.Handles}; " +
                    $"users known for {s.Processes.Count(p => p.User != "Access denied")}, access denied for {s.Processes.Count(p => p.User == "Access denied")}");
        o.WriteLine($"  Connections {s.Connections.Count} sockets: {s.Connections.Count(c => c.State == "Established")} established, {s.Connections.Count(c => c.State == "Listening")} listening, {s.Connections.Count(c => c.Process == "?")} unnamed");
        o.WriteLine($"  Sessions   {string.Join(", ", s.Sessions.Select(u => $"{u.User} (#{u.SessionId}, {u.State})"))}");
        o.WriteLine($"  Installed  {s.InstalledApps.Count} apps; Drivers {s.Drivers.Count} ({s.Drivers.Count(d => d.State == "Running")} running, {s.Drivers.Count(d => d.Version == Fmt.Missing)} without version)");
        o.WriteLine($"  Startup    {s.Startup.Count} entries, {s.Startup.Count(i => i.Enabled)} enabled");
        o.WriteLine($"  Services   {s.Services.Count}, {s.Services.Count(i => i.State == "Running")} running");

        o.WriteLine();
        o.WriteLine("TOP 5 BY CPU");
        foreach (var p in SampleFrame.From(s).Top.Take(5))
            o.WriteLine($"  {p.Pid,7}  {p.Name,-32} {p.Cpu,5:0.0}%");

        // System information: the facts that can be cross-checked inside the machine itself.
        var sys = SystemInfo.Collect();
        bool sysOk = sys.Problems.Count == 0
                     && sys.Topology.LogicalProcessors == Environment.ProcessorCount
                     && sys.Topology.PhysicalCores > 0 && sys.Topology.PhysicalCores <= sys.Topology.LogicalProcessors
                     && sys.Topology.L2Bytes > 0 && sys.CpuName.Length > 0 && sys.TotalMemory > 0
                     && sys.Modules.Sum(m => m.SizeBytes) <= (long)sys.TotalMemory * 11 / 10 + (1L << 30) // modules add up to the installed RAM
                     && sys.Os is not null && sys.Uptime > TimeSpan.Zero;
        o.WriteLine();
        o.WriteLine($"SYSTEM INFO: {sys.CpuName}, {sys.Topology.PhysicalCores} cores / {sys.Topology.LogicalProcessors} logical, {sys.Modules.Count} memory module(s), {sys.Disks.Count} disk(s) {(sysOk ? "OK" : "FAIL")}"
                    + (sys.Problems.Count > 0 ? "  problems: " + string.Join("; ", sys.Problems) : ""));
        if (!sysOk) failures++;

        // Where one tick's time goes: the average over a few more ticks.
        var spent = new Dictionary<string, double>();
        const int Rounds = 8;
        for (int i = 0; i < Rounds; i++)
        {
            collector.CollectOnce();
            foreach (var (id, ms) in collector.Timings) spent[id] = spent.GetValueOrDefault(id) + ms / Rounds;
            Thread.Sleep(250);
        }
        o.WriteLine("TICK TIME (ms, average): " + string.Join(", ", spent.OrderByDescending(x => x.Value).Select(x => $"{x.Key} {x.Value:0.0}")));

        // The byte counters must agree with .NET's, counting only adapters that have an IPv4 address.
        var (nr, nt, na) = SawnTask.Providers.NetworkProvider.ReadCounters();
        long dotnetRx = 0, dotnetTx = 0;
        foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up
                || nic.NetworkInterfaceType is System.Net.NetworkInformation.NetworkInterfaceType.Loopback or System.Net.NetworkInformation.NetworkInterfaceType.Tunnel) continue;
            // WAN miniports and virtual switches report the traffic of the adapter they sit on again; only adapters with an address count once.
            if (!nic.GetIPProperties().UnicastAddresses.Any(u => u.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)) continue;
            var stats = nic.GetIPStatistics();
            dotnetRx += stats.BytesReceived;
            dotnetTx += stats.BytesSent;
        }
        bool netOk = nr > 0 && Math.Abs(nr - dotnetRx) <= Math.Max(1_000_000, dotnetRx / 50);
        o.WriteLine($"NETWORK COUNTERS: GetIfTable2 {Fmt.Bytes(nr)} in / {Fmt.Bytes(nt)} out ({na} adapters); .NET {Fmt.Bytes(dotnetRx)} in / {Fmt.Bytes(dotnetTx)} out {(netOk ? "OK" : "FAIL")}");
        if (!netOk) failures++;

        // Power plans: copy the active plan, rename the copy, change a value on it, read it back, delete it.
        bool powerOk = false;
        string powerNote = "";
        Guid copy = Guid.Empty;
        try
        {
            var active = PowerPlans.ActiveId() ?? throw new InvalidOperationException("no active plan");
            copy = PowerPlans.Duplicate(active, "SawnTask selftest");
            var display = PowerPlans.Settings[0];
            PowerPlans.Write(copy, display, pluggedIn: true, 123);
            PowerPlans.Rename(copy, "SawnTask selftest 2");
            powerOk = PowerPlans.Read(copy, display, true) == 123 && PowerPlans.Name(copy) == "SawnTask selftest 2"
                      && PowerPlans.List().Any(p => p.Id == copy) && PowerPlans.ActiveId() == active;
        }
        catch (Exception ex) { powerNote = ex.Message; }
        finally
        {
            try { if (copy != Guid.Empty) PowerPlans.Delete(copy); } catch (Exception ex) { powerNote += " delete: " + ex.Message; powerOk = false; }
        }
        if (PowerPlans.ActiveId() is Guid activePlan)
        {
            var started = System.Diagnostics.Stopwatch.StartNew();
            var advanced = PowerPlans.Advanced(activePlan);
            o.WriteLine($"ADVANCED POWER SETTINGS: {advanced.Count} groups, {advanced.Sum(g => g.Settings.Count)} settings, read in {started.ElapsedMilliseconds} ms");
        }
        o.WriteLine($"POWER PLANS: copy, rename, edit, delete {(powerOk ? "OK" : "FAIL " + powerNote)}");
        if (!powerOk) failures++;

        // The graph-break rule: a 5 s gap at a 1 s interval is more than 4.25x.
        var history = new History(10, 10);
        history.Append(new SampleFrame { IntervalMs = 1000, ActualMs = 1000, Cpu = 10 });
        history.Append(new SampleFrame { IntervalMs = 1000, ActualMs = 4000, Cpu = 11 });
        history.Append(new SampleFrame { IntervalMs = 1000, ActualMs = 5000, Cpu = 12 });
        bool breakOk = history.GraphBreaks == 1 && double.IsNaN(history.Cpu[2]);
        o.WriteLine();
        o.WriteLine($"GRAPH BREAK RULE: 4 s gap -> no break, 5 s gap -> break. breaks={history.GraphBreaks} {(breakOk ? "OK" : "FAIL")}");
        if (!breakOk) failures++;

        // Recorder round trip: write the live frame plus a gap frame, read them back.
        using (var recorder = new Recorder())
        {
            recorder.Start();
            recorder.Write(SampleFrame.From(s));
            recorder.Write(new SampleFrame { IntervalMs = 1000, ActualMs = 9000, Cpu = 50 });
            recorder.Stop();
            var back = Recorder.Load(recorder.CurrentPath!);
            bool recOk = back.Count == 2 && back[0].Generation == s.Generation && back[0].Cpu == s.CpuPercent && back[1].ActualMs == 9000;
            o.WriteLine($"RECORDER ROUND TRIP: {back.Count} frames read back from {Path.GetFileName(recorder.CurrentPath)} {(recOk ? "OK" : "FAIL")}");
            if (!recOk) failures++;
            File.Delete(recorder.CurrentPath!);
        }

        failures += TestProcessActions(o);
        failures += TestRecycleAndTreeUpdate(o);

        // "Restart as admin" passes the scan path on the command line; "C:\" must survive.
        bool argsOk = Elevation.FromArgs(Elevation.QuoteForArgs(@"C:\").Trim('"')) == @"C:\"
                      && Elevation.FromArgs(Elevation.QuoteForArgs(@"D:\Games\").Trim('"')) == @"D:\Games";
        o.WriteLine($"ELEVATION ARGS ROUND TRIP: {(argsOk ? "OK" : "FAIL")}  (running elevated now: {Elevation.IsElevated})");
        if (!argsOk) failures++;

        o.WriteLine(failures == 0 ? "RESULT: PASS" : $"RESULT: {failures} FAILURE(S)");
        return failures == 0 ? 0 : 1;
    }

    // Runs every process action on a throwaway "ping" process that this test starts itself.
    static int TestProcessActions(TextWriter o)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("ping", "-n 60 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false };
        using var victim = System.Diagnostics.Process.Start(psi)!;
        // Process.StartTime is the same FILETIME that NtQuerySystemInformation reports, in local time.
        var key = new ProcessKey((uint)victim.Id, victim.StartTime.ToFileTime());
        var wrongKey = key with { CreateTime = key.CreateTime - 1 };
        int failures = 0;

        void Check(string what, bool ok, string error)
        {
            o.WriteLine($"  {what,-34} {(ok ? "OK" : "FAIL " + error)}");
            if (!ok) failures++;
        }

        o.WriteLine();
        o.WriteLine($"PROCESS ACTIONS on ping.exe PID {victim.Id}");
        Check("guard refuses wrong creation time", !ProcessActions.TryEndTask(wrongKey, out string guardError) && !victim.HasExited, guardError);
        Check("set priority Low", ProcessActions.TrySetPriority(key, 0x40, out string e1) && Refresh(victim).PriorityClass == System.Diagnostics.ProcessPriorityClass.Idle, e1);
        Check("read priority back (Low)", ProcessActions.GetPriority(key) == 0x40, "GetPriority did not return 0x40");
        Check("suspend", ProcessActions.TrySuspend(key, true, out string e2), e2);
        Check("resume", ProcessActions.TrySuspend(key, false, out string e3), e3);
        Check("end task", ProcessActions.TryEndTask(key, out string e4) && victim.WaitForExit(3000), e4);
        return failures;
    }

    // Builds a small folder tree in %TEMP%, scans it, moves one folder and one file to the
    // Recycle Bin, updates the scanned tree in place like the Disk space page does, and
    // checks the result against a fresh scan.
    static int TestRecycleAndTreeUpdate(TextWriter o)
    {
        string root = Path.Combine(Path.GetTempPath(), "sawntask-selftest-" + Guid.NewGuid().ToString("N")[..8]);
        string keep = Path.Combine(root, "keep"), doomed = Path.Combine(root, "sawntask-selftest-folder");
        Directory.CreateDirectory(Path.Combine(doomed, "inner"));
        Directory.CreateDirectory(keep);
        File.WriteAllBytes(Path.Combine(doomed, "a.bin"), new byte[300_000]);
        File.WriteAllBytes(Path.Combine(doomed, "inner", "b.bin"), new byte[200_000]);
        File.WriteAllBytes(Path.Combine(keep, "c.dat"), new byte[100_000]);
        string doomedFile = Path.Combine(keep, "sawntask-selftest-file.dat");
        File.WriteAllBytes(doomedFile, new byte[50_000]);

        int failures = 0;
        void Check(string what, bool ok, string detail = "")
        {
            o.WriteLine($"  {what,-44} {(ok ? "OK" : "FAIL " + detail)}");
            if (!ok) failures++;
        }

        o.WriteLine();
        o.WriteLine($"RECYCLE BIN + TREE UPDATE in {root}");
        try
        {
            var scan = new DiskScanner().ScanAsync(root, CancellationToken.None).GetAwaiter().GetResult();
            Check("first scan: 650,000 bytes in 4 files", scan.Root.Size == 650_000 && scan.Root.Files == 4, $"{scan.Root.Size} / {scan.Root.Files}");

            var folder = scan.Root.Find(doomed)!;
            Check("move folder to Recycle Bin", RecycleBin.Move(doomed, IntPtr.Zero, out string e1) && !Directory.Exists(doomed), e1);
            folder.RemoveFromTree();

            long onDisk = (50_000 + scan.ClusterSize - 1) / scan.ClusterSize * scan.ClusterSize;
            Check("move file to Recycle Bin", RecycleBin.Move(doomedFile, IntPtr.Zero, out string e2) && !File.Exists(doomedFile), e2);
            scan.Root.Find(keep)!.RemoveFile(50_000, onDisk);

            var fresh = new DiskScanner().ScanAsync(root, CancellationToken.None).GetAwaiter().GetResult();
            Check("updated tree == fresh scan (size, on disk, files, folders)",
                scan.Root.Size == fresh.Root.Size && scan.Root.Allocated == fresh.Root.Allocated
                && scan.Root.Files == fresh.Root.Files && scan.Root.Folders == fresh.Root.Folders,
                $"updated {scan.Root.Size}/{scan.Root.Allocated}/{scan.Root.Files}/{scan.Root.Folders} vs fresh {fresh.Root.Size}/{fresh.Root.Allocated}/{fresh.Root.Files}/{fresh.Root.Folders}");
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* best effort */ }
        }
        return failures;
    }

    static System.Diagnostics.Process Refresh(System.Diagnostics.Process p)
    {
        p.Refresh();
        return p;
    }
}
