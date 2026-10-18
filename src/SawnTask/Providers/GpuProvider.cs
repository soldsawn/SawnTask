using SawnTask.Core;
using SawnTask.Native;

namespace SawnTask.Providers;

// GPU utilization from the "\GPU Engine(*)\Utilization Percentage" performance counters
// (the same data Windows Task Manager shows).
public sealed class GpuProvider : IMetricProvider, IDisposable
{
    readonly PdhQuery query = new();
    readonly IntPtr engines;

    public GpuProvider() => engines = query.Add(@"\GPU Engine(*)\Utilization Percentage");

    public string Id => "windows.gpu";
    public string Description => @"PDH \GPU Engine(*)\Utilization Percentage, summed per engine, busiest engine wins";
    public string Detail { get; private set; } = "";

    public void Collect(Snapshot s)
    {
        query.Collect();
        var values = query.Values(engines);
        if (values.Count == 0)
        {
            Detail = "no GPU engine data yet";
            return;
        }

        // Instance names look like
        //   pid_1864_luid_0x00000000_0x0000C2E1_phys_0_eng_0_engtype_3D
        // One instance per (process, engine). Sum over processes for each engine,
        // then report the busiest engine: the same rule Windows Task Manager uses.
        var perEngine = new Dictionary<string, double>();
        foreach (var (instance, value) in values)
        {
            int at = instance.IndexOf("luid_", StringComparison.Ordinal);
            string engine = at >= 0 ? instance[at..] : instance;
            perEngine[engine] = perEngine.GetValueOrDefault(engine) + value;
        }
        s.GpuPercent = Math.Min(100, perEngine.Values.Max());
        Detail = $"{perEngine.Count} engines across {values.Count} instances";
    }

    public void Dispose() => query.Dispose();
}
