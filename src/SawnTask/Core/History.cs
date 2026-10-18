namespace SawnTask.Core;

// Fixed-size ring of values. NaN marks a gap (not measured, or a graph break).
public sealed class Series
{
    readonly double[] buffer;
    int head;

    public Series(int capacity) => buffer = new double[capacity];

    public int Capacity => buffer.Length;
    public int Count { get; private set; }
    public long Total { get; private set; } // values ever added; keeps growing when the ring is full

    // Index 0 is the oldest retained value.
    public double this[int index] => buffer[(head - Count + index + buffer.Length) % buffer.Length];

    public void Add(double value)
    {
        buffer[head] = value;
        head = (head + 1) % buffer.Length;
        if (Count < buffer.Length) Count++;
        Total++;
    }

    public void Clear()
    {
        head = 0;
        Count = 0;
        Total = 0;
    }

    public double MaxValue()
    {
        double max = 0;
        for (int i = 0; i < Count; i++)
        {
            double v = this[i];
            if (!double.IsNaN(v) && v > max) max = v;
        }
        return max;
    }
}

public sealed class History
{
    // When the time since the previous sample exceeds 4.25 x the collection interval
    // (the PC slept, or the app was stalled), the graph shows a break instead of
    // joining the two points with a line that would invent the values in between.
    public const double GraphBreakFactor = 4.25;

    readonly int coreCapacity;
    bool hasSamples;

    // capacity: samples kept for the main graphs; the per-core graphs keep coreCapacity.
    public History(int capacity, int coreCapacity)
    {
        this.coreCapacity = coreCapacity;
        Cpu = new Series(capacity);
        CpuKernel = new Series(capacity);
        Mem = new Series(capacity);
        NetRx = new Series(capacity);
        NetTx = new Series(capacity);
        DiskActive = new Series(capacity);
        Gpu = new Series(capacity);
        CpuTemp = new Series(capacity);
        CpuPower = new Series(capacity);
    }

    public Series Cpu { get; }
    public Series CpuKernel { get; }
    public Series Mem { get; }
    public Series NetRx { get; }
    public Series NetTx { get; }
    public Series DiskActive { get; }
    public Series Gpu { get; }
    public Series CpuTemp { get; }
    public Series CpuPower { get; }
    public List<Series> Cores { get; } = [];
    public int GraphBreaks { get; private set; }

    IEnumerable<Series> All => new[] { Cpu, CpuKernel, Mem, NetRx, NetTx, DiskActive, Gpu, CpuTemp, CpuPower }.Concat(Cores);

    public void Append(SampleFrame f)
    {
        if (hasSamples && f.ActualMs > GraphBreakFactor * f.IntervalMs)
        {
            foreach (var s in All) s.Add(double.NaN);
            GraphBreaks++;
        }
        hasSamples = true;

        Cpu.Add(f.Cpu ?? double.NaN);
        CpuKernel.Add(f.CpuKernel ?? double.NaN);
        Mem.Add(f.Mem ?? double.NaN);
        NetRx.Add(f.NetRx ?? double.NaN);
        NetTx.Add(f.NetTx ?? double.NaN);
        DiskActive.Add(f.DiskActive ?? double.NaN);
        Gpu.Add(f.Gpu ?? double.NaN);
        CpuTemp.Add(f.CpuTemp ?? double.NaN);
        CpuPower.Add(f.CpuPower ?? double.NaN);

        int cores = f.Cores?.Length ?? 0;
        while (Cores.Count < cores) Cores.Add(new Series(coreCapacity));
        for (int i = 0; i < Cores.Count; i++)
            Cores[i].Add(i < cores ? f.Cores![i] : double.NaN);
    }

    public void Clear()
    {
        foreach (var s in All) s.Clear();
        hasSamples = false;
        GraphBreaks = 0;
    }
}
