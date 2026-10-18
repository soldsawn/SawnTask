using SawnTask.Core;
using SawnTask.Native;

namespace SawnTask.Providers;

// CPU package, integrated-graphics and board temperatures from Intel Dynamic
// Tuning, plus CPU package power. The Intel service answers over a named pipe, so a
// stuck service could block. Reads therefore run on their own worker thread; the
// collector only takes the latest cached result and drops it once it is older than MaxAge.
public sealed class ThermalProvider : IMetricProvider, IDisposable
{
    static readonly TimeSpan ReadPeriod = TimeSpan.FromSeconds(1);
    static readonly TimeSpan RetryPeriod = TimeSpan.FromSeconds(30);
    static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(5);

    readonly Thread worker;
    readonly CancellationTokenSource stop = new();
    readonly object gate = new();
    IReadOnlyList<ThermalSensor> latest = [];
    IReadOnlyList<PowerReading> latestPower = [];
    DateTime latestUtc = DateTime.MinValue;
    string state = "Intel Dynamic Tuning has not been tried yet";

    public ThermalProvider()
    {
        worker = new Thread(Run) { IsBackground = true, Name = "SawnTask Intel Dynamic Tuning" };
        worker.Start();
    }

    public string Id => "windows.thermal.intel-dtt";
    public string Description => "Intel Dynamic Tuning (ipfcore.dll, public IPF pipe): GET_TEMPERATURE on every domain, GET_RAPL_POWER on the CPU";
    public string Detail { get; private set; } = "";

    public void Collect(Snapshot s)
    {
        IReadOnlyList<ThermalSensor> sensors;
        IReadOnlyList<PowerReading> power;
        DateTime stamp;
        string current;
        lock (gate) (sensors, power, stamp, current) = (latest, latestPower, latestUtc, state);

        if (DateTime.UtcNow - stamp > MaxAge)
        {
            // A source that never produced data is "unavailable", not an error.
            if (stamp == DateTime.MinValue) throw new SourceUnavailableException(current);
            throw new InvalidOperationException($"no fresh reading for {(DateTime.UtcNow - stamp).TotalSeconds:0} s; {current}");
        }

        s.Thermals = sensors;
        s.CpuTempC = sensors.FirstOrDefault(t => t.IsCpuPackage)?.Celsius
                     ?? sensors.FirstOrDefault(t => t.Label == "CPU cores")?.Celsius;
        s.Power = [.. s.Power, .. power];
        s.CpuPowerW = power.FirstOrDefault(p => p.Label == "CPU package")?.Watts;
        Detail = $"{sensors.Count} temperatures, {power.Count} power readings; {current}";
    }

    void Run()
    {
        IntelDttClient? client = null;
        bool powerPrimed = false;
        while (!stop.IsCancellationRequested)
        {
            TimeSpan wait = ReadPeriod;
            try
            {
                if (client is null)
                {
                    client = IntelDttClient.Connect();
                    powerPrimed = false;
                    SetState($"connected to ipfcore {client.CoreVersion}");
                }

                var readings = client.ReadAll();
                bool anyPackage = readings.Any(r => IntelDttClient.IsPackage(r.Participant, r.Domain));
                var sensors = readings
                    .Select(r => new ThermalSensor(
                        IntelDttClient.Label(r.Participant, r.Domain),
                        $"{r.Participant.Name}.{r.Domain.Name}",
                        r.Celsius,
                        anyPackage ? IntelDttClient.IsPackage(r.Participant, r.Domain) : IntelDttClient.IsProcessor(r.Participant, r.Domain)))
                    .OrderByDescending(t => t.IsCpuPackage)
                    .ThenBy(t => t.Label)
                    .ToList();

                // RAPL power is the energy used since the previous read, so the very
                // first read only starts the measurement (it returns 0). Discard it.
                var power = new List<PowerReading>();
                var participants = client.Participants.ToDictionary(p => p.Handle);
                foreach (var d in client.Domains)
                {
                    if (!participants.TryGetValue(d.Participant, out var p) || !IntelDttClient.IsCpuParticipant(p)) continue;
                    if (client.ReadWatts(d, out _) is double w && powerPrimed)
                        power.Add(new PowerReading(IntelDttClient.Label(p, d), $"{p.Name}.{d.Name}", w));
                }
                powerPrimed = true;

                lock (gate)
                {
                    if (sensors.Count > 0)
                    {
                        latest = sensors;
                        latestPower = power.OrderByDescending(p => p.Label == "CPU package").ThenBy(p => p.Label).ToList();
                        latestUtc = DateTime.UtcNow;
                    }
                    else
                    {
                        state = "Intel Dynamic Tuning has announced no thermal participants yet";
                    }
                }
            }
            catch (Exception e)
            {
                // Keep going without Intel's data and retry later.
                SetState(e.Message);
                wait = RetryPeriod;
            }
            stop.Token.WaitHandle.WaitOne(wait);
        }
        client?.Dispose();
    }

    void SetState(string text)
    {
        lock (gate) state = text;
    }

    public void Dispose()
    {
        stop.Cancel();
        // Do not wait long: if the Intel service hangs, the worker is left behind.
        worker.Join(TimeSpan.FromSeconds(2));
    }
}
