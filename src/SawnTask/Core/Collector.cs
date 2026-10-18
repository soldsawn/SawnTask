using System.Diagnostics;
using SawnTask.Providers;

namespace SawnTask.Core;

public interface IMetricProvider
{
    string Id { get; }
    string Description { get; }
    string Detail { get; }
    void Collect(Snapshot snapshot);
}

// Slow-tier source with "last good" caching: lists that change rarely (services,
// startup items, ...) are read every few seconds instead of every tick, and the
// last good list is kept when a read fails.
public sealed class SlowSource<T>(string id, string description, Func<List<T>> read, TimeSpan period)
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(12);

    public TimeSpan Period { get; } = period;
    public double NextDueMs { get; set; }

    IReadOnlyList<T> cache = [];
    DateTime lastGoodUtc = DateTime.MinValue;
    SourceHealth health = SourceHealth.NotYetRun;
    string detail = "not yet collected";

    public IReadOnlyList<T> Current => cache;
    public SourceStatus Status => new(id, description, health, detail, lastGoodUtc);

    public void Refresh()
    {
        try
        {
            cache = read();
            lastGoodUtc = DateTime.UtcNow;
            health = SourceHealth.Ok;
            detail = $"{cache.Count} items";
        }
        catch (Exception e)
        {
            // A source refreshed less often than MaxAge keeps its data for one extra period.
            bool young = DateTime.UtcNow - lastGoodUtc <= (Period > MaxAge ? Period * 2 : MaxAge);
            health = young ? SourceHealth.Stale : SourceHealth.Failed;
            detail = (young ? "showing last good data; " : "cache discarded; ") + e.Message;
            if (!young) cache = [];
        }
    }
}

// The heart of the app: one background thread, one clock, every source read
// in the same tick, and one numbered Snapshot published per tick (the
// "Generation" counter in the status bar).
public sealed class Collector : IDisposable
{
    public static readonly TimeSpan SlowTierPeriod = TimeSpan.FromSeconds(4);

    readonly IMetricProvider[] fast;
    readonly SlowSource<StartupItem> startup;
    readonly SlowSource<ServiceItem> services;
    readonly SlowSource<UserSession> sessions;
    readonly SlowSource<InstalledApp> installedApps;
    readonly SlowSource<DriverItem> drivers;
    readonly Dictionary<string, (SourceHealth Health, string Detail, DateTime LastGood)> fastState = [];
    readonly Stopwatch clock = Stopwatch.StartNew();
    readonly CancellationTokenSource stop = new();

    Thread? thread;
    long generation;
    double lastSampleMs = double.NaN;
    volatile int intervalMs = 1000;
    volatile bool paused;

    public Collector()
    {
        fast =
        [
            new CpuProvider(),
            new MemoryProvider(),
            new ProcessProvider(),
            new NetworkProvider(),
            new DiskProvider(),
            new GpuProvider(),
            new NvidiaProvider(),
            new ThermalProvider(),
            new ConnectionsProvider(), // after ProcessProvider: it names PIDs from this tick's process list
        ];
        startup = new SlowSource<StartupItem>("windows.startup",
            "HKCU/HKLM Run keys, StartupApproved state, per-user and all-users Startup folders", StartupProvider.Read, SlowTierPeriod);
        services = new SlowSource<ServiceItem>("windows.services",
            "Service Control Manager inventory (EnumServicesStatusExW)", ServiceProvider.Read, SlowTierPeriod);
        sessions = new SlowSource<UserSession>("windows.sessions",
            "WTS logon sessions (WTSEnumerateSessionsW + WTSQuerySessionInformationW)", SessionsProvider.Read, SlowTierPeriod);
        installedApps = new SlowSource<InstalledApp>("windows.installed",
            "Uninstall registry keys: HKLM (64- and 32-bit) and HKCU", InstalledAppsProvider.Read, TimeSpan.FromSeconds(30));
        drivers = new SlowSource<DriverItem>("windows.drivers",
            "SCM driver services + each .sys file's version resource", DriversProvider.Read, TimeSpan.FromSeconds(60));
    }

    // How long each source took in the latest tick, in milliseconds (the --selftest prints them).
    public Dictionary<string, double> Timings { get; } = [];

    public event Action<Snapshot>? SnapshotReady;

    public int IntervalMs
    {
        get => intervalMs;
        set => intervalMs = Math.Max(100, value);
    }

    // Pausing stops sampling entirely. On resume the gap is far larger than
    // 4.25 x the interval, so History inserts a graph break: nothing is invented.
    public bool Paused
    {
        get => paused;
        set => paused = value;
    }

    public void Start()
    {
        thread = new Thread(Run) { IsBackground = true, Name = "SawnTask collector" };
        thread.Start();
    }

    void Run()
    {
        while (!stop.IsCancellationRequested)
        {
            double started = clock.Elapsed.TotalMilliseconds;
            if (!paused)
            {
                var snapshot = CollectOnce();
                try { SnapshotReady?.Invoke(snapshot); } catch { /* a UI bug must not kill sampling */ }
            }
            double wait = intervalMs - (clock.Elapsed.TotalMilliseconds - started);
            stop.Token.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(Math.Max(1, wait)));
        }
    }

    // Public so the --selftest mode can drive it without a window.
    public Snapshot CollectOnce()
    {
        double now = clock.Elapsed.TotalMilliseconds;
        var s = new Snapshot
        {
            Generation = ++generation,
            TimestampUtc = DateTime.UtcNow,
            IntervalMs = intervalMs,
            ActualMs = double.IsNaN(lastSampleMs) ? intervalMs : now - lastSampleMs,
            LogicalProcessors = Environment.ProcessorCount,
        };
        lastSampleMs = now;

        var statuses = new List<SourceStatus>();
        foreach (var provider in fast)
        {
            // Each source succeeds or fails on its own; one broken source never
            // blanks the others: successful sources are published independently.
            long began = Stopwatch.GetTimestamp();
            try
            {
                provider.Collect(s);
                fastState[provider.Id] = (SourceHealth.Ok, provider.Detail, DateTime.UtcNow);
            }
            catch (SourceUnavailableException e)
            {
                var previous = fastState.GetValueOrDefault(provider.Id);
                fastState[provider.Id] = (SourceHealth.Unavailable, e.Message, previous.LastGood);
            }
            catch (Exception e)
            {
                var previous = fastState.GetValueOrDefault(provider.Id);
                fastState[provider.Id] = (SourceHealth.Failed, e.Message, previous.LastGood);
            }
            Timings[provider.Id] = Stopwatch.GetElapsedTime(began).TotalMilliseconds;
            var st = fastState[provider.Id];
            statuses.Add(new SourceStatus(provider.Id, provider.Description, st.Health, st.Detail, st.LastGood));
        }

        long slowBegan = Stopwatch.GetTimestamp();
        Refresh(startup, now);
        Refresh(services, now);
        Refresh(sessions, now);
        Refresh(installedApps, now);
        Refresh(drivers, now);
        Timings["slow sources"] = Stopwatch.GetElapsedTime(slowBegan).TotalMilliseconds;
        s.Startup = startup.Current;
        s.Services = services.Current;
        s.Sessions = sessions.Current;
        s.InstalledApps = installedApps.Current;
        s.Drivers = drivers.Current;
        statuses.Add(startup.Status);
        statuses.Add(services.Status);
        statuses.Add(sessions.Status);
        statuses.Add(installedApps.Status);
        statuses.Add(drivers.Status);

        s.Sources = statuses;
        return s;
    }

    static void Refresh<T>(SlowSource<T> source, double nowMs)
    {
        if (nowMs < source.NextDueMs) return;
        source.Refresh();
        source.NextDueMs = nowMs + source.Period.TotalMilliseconds;
    }

    // Lets the UI ask for an immediate re-read after it changed something (e.g. a startup toggle).
    public void InvalidateStartup() => startup.NextDueMs = 0;

    public void Dispose()
    {
        stop.Cancel();
        thread?.Join(2000);
        foreach (var provider in fast.OfType<IDisposable>()) provider.Dispose();
    }
}
