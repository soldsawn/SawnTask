using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using SawnTask.Core;

namespace SawnTask.Providers;

// NVIDIA GPUs: clocks, temperature, power and memory from NVML (nvmlInit_v2,
// nvmlDeviceGetTemperature, nvmlDeviceGetPowerUsage, ...). NVML is NVIDIA's documented
// library, installed with the driver as C:\Windows\System32\nvml.dll.
//
// On a laptop the NVIDIA GPU sleeps when idle, and a query can wake it (a read that slow
// had to wake the device, so it is not repeated every sample). So reads run
// on a worker thread, and a slow read pushes the next one 30 s away.
public sealed unsafe class NvidiaProvider : IMetricProvider, IDisposable
{
    static readonly TimeSpan FastPeriod = TimeSpan.FromSeconds(2);
    static readonly TimeSpan SlowPeriod = TimeSpan.FromSeconds(30);
    static readonly TimeSpan SlowRead = TimeSpan.FromMilliseconds(300);
    static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(45);

    const int NVML_SUCCESS = 0;
    const int NVML_TEMPERATURE_GPU = 0;
    const int NVML_CLOCK_GRAPHICS = 0, NVML_CLOCK_MEM = 2;

    [StructLayout(LayoutKind.Sequential)]
    struct NvmlMemory { public ulong Total, Free, Used; }

    [StructLayout(LayoutKind.Sequential)]
    struct NvmlUtilization { public uint Gpu, Memory; }

    readonly Thread worker;
    readonly CancellationTokenSource stop = new();
    readonly object gate = new();
    IReadOnlyList<GpuDevice> latest = [];
    DateTime latestUtc = DateTime.MinValue;
    string state = "NVML has not been tried yet";
    bool unavailable;

    delegate* unmanaged<int> init, shutdown;
    delegate* unmanaged<uint*, int> getCount;
    delegate* unmanaged<uint, IntPtr*, int> getHandle;
    delegate* unmanaged<IntPtr, byte*, uint, int> getName;
    delegate* unmanaged<IntPtr, int, uint*, int> getTemperature;
    delegate* unmanaged<IntPtr, uint*, int> getPower;
    delegate* unmanaged<IntPtr, int, uint*, int> getClock;
    delegate* unmanaged<IntPtr, NvmlMemory*, int> getMemory;
    delegate* unmanaged<IntPtr, NvmlUtilization*, int> getUtilization;

    public NvidiaProvider()
    {
        worker = new Thread(Run) { IsBackground = true, Name = "SawnTask NVML" };
        worker.Start();
    }

    public string Id => "windows.gpu.nvml";
    public string Description => "NVIDIA NVML (nvml.dll): temperature, power, clocks, VRAM; backs off when a read wakes the GPU";
    public string Detail { get; private set; } = "";

    public void Collect(Snapshot s)
    {
        IReadOnlyList<GpuDevice> gpus;
        DateTime stamp;
        string current;
        bool missing;
        lock (gate) (gpus, stamp, current, missing) = (latest, latestUtc, state, unavailable);

        if (missing) throw new SourceUnavailableException(current);
        if (stamp == DateTime.MinValue) throw new SourceUnavailableException(current);
        if (DateTime.UtcNow - stamp > MaxAge) throw new InvalidOperationException($"no fresh NVML reading; {current}");

        s.Gpus = gpus;
        s.Power = [.. s.Power, .. gpus.Where(g => g.PowerW is not null).Select(g => new PowerReading(g.Name, "nvml", g.PowerW!.Value))];
        Detail = $"{gpus.Count} GPU(s); {current}";
    }

    void Run()
    {
        if (!Load(out string why))
        {
            lock (gate) (state, unavailable) = (why, true);
            return;
        }

        while (!stop.IsCancellationRequested)
        {
            var timer = Stopwatch.StartNew();
            TimeSpan wait = FastPeriod;
            try
            {
                var gpus = ReadAll();
                timer.Stop();
                bool slow = timer.Elapsed > SlowRead;
                if (slow) wait = SlowPeriod;
                lock (gate)
                {
                    latest = gpus;
                    latestUtc = DateTime.UtcNow;
                    state = slow
                        ? $"read took {timer.ElapsedMilliseconds} ms (GPU was probably asleep); next read in 30 s"
                        : $"read took {timer.ElapsedMilliseconds} ms";
                }
            }
            catch (Exception e)
            {
                lock (gate) state = e.Message;
                wait = SlowPeriod;
            }
            stop.Token.WaitHandle.WaitOne(wait);
        }
        shutdown();
    }

    bool Load(out string why)
    {
        string path = Path.Combine(Environment.SystemDirectory, "nvml.dll");
        if (!NativeLibrary.TryLoad(path, out IntPtr lib))
        {
            why = "nvml.dll not found: no NVIDIA driver installed";
            return false;
        }
        try
        {
            init = (delegate* unmanaged<int>)NativeLibrary.GetExport(lib, "nvmlInit_v2");
            shutdown = (delegate* unmanaged<int>)NativeLibrary.GetExport(lib, "nvmlShutdown");
            getCount = (delegate* unmanaged<uint*, int>)NativeLibrary.GetExport(lib, "nvmlDeviceGetCount_v2");
            getHandle = (delegate* unmanaged<uint, IntPtr*, int>)NativeLibrary.GetExport(lib, "nvmlDeviceGetHandleByIndex_v2");
            getName = (delegate* unmanaged<IntPtr, byte*, uint, int>)NativeLibrary.GetExport(lib, "nvmlDeviceGetName");
            getTemperature = (delegate* unmanaged<IntPtr, int, uint*, int>)NativeLibrary.GetExport(lib, "nvmlDeviceGetTemperature");
            getPower = (delegate* unmanaged<IntPtr, uint*, int>)NativeLibrary.GetExport(lib, "nvmlDeviceGetPowerUsage");
            getClock = (delegate* unmanaged<IntPtr, int, uint*, int>)NativeLibrary.GetExport(lib, "nvmlDeviceGetClockInfo");
            getMemory = (delegate* unmanaged<IntPtr, NvmlMemory*, int>)NativeLibrary.GetExport(lib, "nvmlDeviceGetMemoryInfo");
            getUtilization = (delegate* unmanaged<IntPtr, NvmlUtilization*, int>)NativeLibrary.GetExport(lib, "nvmlDeviceGetUtilizationRates");
        }
        catch (EntryPointNotFoundException e)
        {
            why = "nvml.dll was found but is missing an expected symbol: " + e.Message;
            return false;
        }

        int rc = init();
        if (rc != NVML_SUCCESS)
        {
            why = $"nvmlInit_v2 failed: {rc}";
            return false;
        }
        why = "";
        return true;
    }

    List<GpuDevice> ReadAll()
    {
        uint count = 0;
        int rc = getCount(&count);
        if (rc != NVML_SUCCESS) throw new InvalidOperationException($"nvmlDeviceGetCount_v2 failed: {rc}");

        var result = new List<GpuDevice>();
        byte* name = stackalloc byte[96];
        for (uint i = 0; i < count; i++)
        {
            IntPtr device;
            if (getHandle(i, &device) != NVML_SUCCESS) continue;

            string text = getName(device, name, 96) == NVML_SUCCESS
                ? Encoding.UTF8.GetString(name, new ReadOnlySpan<byte>(name, 96).IndexOf((byte)0) is int n and >= 0 ? n : 96)
                : $"NVIDIA GPU {i}";

            uint temp, milliwatts, core, mem;
            NvmlMemory vram;
            NvmlUtilization util;
            result.Add(new GpuDevice(
                text,
                getTemperature(device, NVML_TEMPERATURE_GPU, &temp) == NVML_SUCCESS ? temp : null,
                getPower(device, &milliwatts) == NVML_SUCCESS ? Math.Round(milliwatts / 1000.0, 1) : null,
                getClock(device, NVML_CLOCK_GRAPHICS, &core) == NVML_SUCCESS ? core : null,
                getClock(device, NVML_CLOCK_MEM, &mem) == NVML_SUCCESS ? mem : null,
                getMemory(device, &vram) == NVML_SUCCESS ? vram.Used : null,
                getMemory(device, &vram) == NVML_SUCCESS ? vram.Total : null,
                getUtilization(device, &util) == NVML_SUCCESS ? util.Gpu : null));
        }
        return result;
    }

    public void Dispose()
    {
        stop.Cancel();
        worker.Join(TimeSpan.FromSeconds(2));
    }
}
