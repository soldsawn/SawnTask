namespace SawnTask.Core;

// Which processes used the CPU, sample by sample, so the History page can average any
// stretch of the graph ("Average CPU use"). Samples are numbered like the graph's
// series (History.Cpu.Total - 1), so a selection made on the graph maps straight onto them.
// Only processes that used some CPU are stored per sample; names and memory come from the
// newest sample they appeared in.
public sealed class ProcessHistory(int capacity)
{
    (long Index, (ProcessKey Key, float Cpu)[] Entries)?[] ring = new (long, (ProcessKey, float)[])?[capacity];
    readonly Dictionary<ProcessKey, ProcessSample> info = [];
    int added;

    public void Add(long index, Snapshot s)
    {
        var entries = new List<(ProcessKey, float)>();
        foreach (var p in s.Processes)
            if (p.Key.Pid != 0 && p.CpuPercent is > 0)
            {
                entries.Add((p.Key, (float)p.CpuPercent.Value));
                info[p.Key] = p;
            }
        ring[index % capacity] = (index, entries.ToArray());
        if (++added % capacity == 0) Prune();
    }

    // A new capacity: the samples are placed again by their number; where two share a slot the newer wins.
    public void Resize(int newCapacity)
    {
        if (newCapacity == capacity) return;
        var old = ring;
        ring = new (long, (ProcessKey, float)[])?[newCapacity];
        capacity = newCapacity;
        foreach (var tick in old)
            if (tick is { } t && (ring[t.Index % capacity] is not { } there || there.Index < t.Index))
                ring[t.Index % capacity] = t;
        Prune();
    }

    public void Clear()
    {
        Array.Clear(ring);
        info.Clear();
        added = 0;
    }

    // Forget processes that are no longer in any retained sample.
    void Prune()
    {
        var alive = new HashSet<ProcessKey>(ring.Where(t => t is not null).SelectMany(t => t!.Value.Entries.Select(e => e.Key)));
        foreach (var key in info.Keys.Where(k => !alive.Contains(k)).ToList()) info.Remove(key);
    }

    // Average CPU of every process over samples from..to (inclusive), busiest first. A process
    // counts as 0 % in the samples where it used nothing. Quiet running processes from `running`
    // fill the list up to `take` rows (shown as 0.0 %).
    public List<TopProcess> Average(long from, long to, IEnumerable<ProcessSample> running, int take, out int samples)
    {
        var sum = new Dictionary<ProcessKey, double>();
        samples = 0;
        foreach (var tick in ring)
        {
            if (tick is not { } t || t.Index < from || t.Index > to) continue;
            samples++;
            foreach (var (key, cpu) in t.Entries) sum[key] = sum.GetValueOrDefault(key) + cpu;
        }
        var result = new List<TopProcess>();
        if (samples == 0) return result;

        foreach (var (key, total) in sum.OrderByDescending(kv => kv.Value).Take(take))
        {
            var p = info[key];
            result.Add(new TopProcess(p.Name, key.Pid, total / samples, p.PrivateBytes, p.ImagePath));
        }
        var listed = result.Select(r => r.Pid).ToHashSet();
        foreach (var p in running.Where(p => p.Key.Pid != 0 && !listed.Contains(p.Key.Pid)).OrderByDescending(p => p.PrivateBytes))
        {
            if (result.Count >= take) break;
            result.Add(new TopProcess(p.Name, p.Key.Pid, 0, p.PrivateBytes, p.ImagePath));
        }
        return result;
    }
}
