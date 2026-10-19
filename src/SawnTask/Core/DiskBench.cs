using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using Microsoft.Win32.SafeHandles;

namespace SawnTask.Core;

public enum BenchProfile { Quick, Normal, Thorough }

// FileBytes: size of the test file. Passes: every measurement is repeated and the best pass counts.
// RandomSeconds: how long one random pass runs. SequentialSeconds: the least a sequential pass runs (it moves the
// whole file once and goes round again if that was quicker, so a fast disk is still measured long enough).
public sealed record BenchOptions(long FileBytes, int Passes, double RandomSeconds, double SequentialSeconds)
{
    public static BenchOptions For(BenchProfile profile) => profile switch
    {
        BenchProfile.Quick => new BenchOptions(256L << 20, 1, 2, 1),
        BenchProfile.Thorough => new BenchOptions(4L << 30, 5, 5, 2),
        _ => new BenchOptions(1L << 30, 3, 3, 1),
    };
}

public sealed record BenchTest(string Name, bool Sequential, int BlockBytes, int QueueDepth);

public sealed record BenchResult(string Test, bool Write, double MBps, double Iops, double LatencyUs);

// Phase is "prepare" while the test file is written, "run" while a test measures.
public sealed record BenchProgress(int Step, int Steps, string Phase, string Test, bool Write, int Pass, int Passes, double LiveMBps);

public sealed class BenchException(string message) : Exception(message);

// A disk speed test in the style of the usual 1 MiB / 4 KiB sequential and random benchmarks.
// The test file is opened without Windows' file cache (no buffering, write through), so the
// numbers are the disk's and not the memory's. Queue depth is the number of requests in
// flight: one thread per request, each doing plain synchronous reads or writes.
public static class DiskBench
{
    public static readonly BenchTest[] Tests =
    [
        new("SEQ1M Q8T1", true, 1 << 20, 8),
        new("SEQ1M Q1T1", true, 1 << 20, 1),
        new("RND4K Q32T1", false, 4096, 32),
        new("RND4K Q1T1", false, 4096, 1),
    ];

    const FileOptions NoBuffering = (FileOptions)0x20000000;
    const int Alignment = 4096;

    // The system drive gets a folder in the user's own data (always writable); other drives a
    // folder in their root.
    public static string FolderFor(string driveRoot)
    {
        string system = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.System)) ?? "";
        return string.Equals(driveRoot, system, StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SawnTask", "bench")
            : Path.Combine(driveRoot, "SawnTaskBench");
    }

    // Runs every test (read, then write) on one drive. Results arrive through onResult as they
    // are finished; the full list is returned. Cancelling returns what was finished so far.
    public static List<BenchResult> Run(string driveRoot, BenchOptions options, Action<BenchProgress> onProgress,
        Action<BenchResult> onResult, CancellationToken cancel)
    {
        var results = new List<BenchResult>();
        string folder = FolderFor(driveRoot);
        string path = Path.Combine(folder, "SawnTask-bench.tmp");

        long free;
        try { free = new DriveInfo(driveRoot).AvailableFreeSpace; }
        catch (Exception e) { throw new BenchException(e.Message); }
        if (free < options.FileBytes + (256L << 20)) throw new BenchException("space");

        SafeFileHandle handle;
        try
        {
            Directory.CreateDirectory(folder);
            handle = File.OpenHandle(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None,
                FileOptions.WriteThrough | FileOptions.DeleteOnClose | FileOptions.Asynchronous | NoBuffering, options.FileBytes);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new BenchException(e.Message);
        }

        int steps = Tests.Length * 2;
        try
        {
            using (handle)
            {
                // Reads need something to read: write the whole file once.
                onProgress(new BenchProgress(0, steps, "prepare", "", true, 1, 1, 0));
                Measure(handle, true, 1 << 20, 2, true, options.FileBytes, 0,
                    mbps => onProgress(new BenchProgress(0, steps, "prepare", "", true, 1, 1, mbps)), cancel);

                int step = 0;
                foreach (var test in Tests)
                    foreach (bool write in new[] { false, true })
                    {
                        if (cancel.IsCancellationRequested) return results;
                        step++;
                        BenchResult? best = null;
                        for (int pass = 1; pass <= options.Passes && !cancel.IsCancellationRequested; pass++)
                        {
                            onProgress(new BenchProgress(step, steps, "run", test.Name, write, pass, options.Passes, 0));
                            var r = Measure(handle, test.Sequential, test.BlockBytes, test.QueueDepth, write, options.FileBytes,
                                test.Sequential ? options.SequentialSeconds : options.RandomSeconds, mbps => onProgress(new BenchProgress(step, steps, "run", test.Name, write, pass, options.Passes, mbps)), cancel);
                            if (r is { } m && (best is null || m.mbps > best.MBps))
                                best = new BenchResult(test.Name, write, m.mbps, m.iops, m.latencyUs);
                        }
                        if (best is null) return results;
                        results.Add(best);
                        onResult(best);
                    }
            }
        }
        finally
        {
            try { File.Delete(path); } catch { /* DeleteOnClose already removed it */ }
        }
        return results;
    }

    // One pass: queueDepth threads share the work until the file has been moved once and the time is up (sequential)
    // or the time is up (random). Returns null if cancelled before anything was done.
    static unsafe (double mbps, double iops, double latencyUs)? Measure(SafeFileHandle handle, bool sequential, int block, int queueDepth,
        bool write, long fileBytes, double seconds, Action<double> live, CancellationToken cancel)
    {
        long blocks = fileBytes / block, next = -1, bytes = 0, ops = 0, latencyTicks = 0;
        Exception? failure = null;
        var clock = Stopwatch.StartNew();
        long startStamp = Stopwatch.GetTimestamp(), lastEnd = startStamp;
        double limit = seconds;

        var workers = new Thread[queueDepth];
        for (int w = 0; w < queueDepth; w++)
        {
            int seed = Environment.TickCount + w * 7919;
            workers[w] = new Thread(() =>
            {
                try
                {
                    // Aligned memory is required by unbuffered I/O; a pinned array, cut at an aligned address.
                    var array = GC.AllocateUninitializedArray<byte>(block + Alignment, pinned: true);
                    int skip = (int)((Alignment - (long)Unsafe.AsPointer(ref array[0]) % Alignment) % Alignment);
                    var buffer = array.AsSpan(skip, block);
                    var random = new Random(seed);
                    if (write) random.NextBytes(buffer);

                    long myBytes = 0, myOps = 0, myTicks = 0;
                    while (!cancel.IsCancellationRequested)
                    {
                        long index;
                        if (sequential)
                        {
                            index = Interlocked.Increment(ref next);
                            if (index >= blocks && clock.Elapsed.TotalSeconds >= limit) break;
                            index %= blocks;
                        }
                        else
                        {
                            if (clock.Elapsed.TotalSeconds >= limit) break;
                            index = random.NextInt64(blocks);
                        }
                        long started = Stopwatch.GetTimestamp();
                        if (write) RandomAccess.Write(handle, buffer, index * block);
                        else if (RandomAccess.Read(handle, buffer, index * block) < block) break;
                        myTicks += Stopwatch.GetTimestamp() - started;
                        myOps++;
                        myBytes += block;
                        if ((myOps & 15) == 0 || block >= 1 << 20)
                        {
                            Interlocked.Add(ref bytes, myBytes);
                            Interlocked.Add(ref ops, myOps);
                            myBytes = myOps = 0;
                        }
                    }
                    Interlocked.Add(ref bytes, myBytes);
                    Interlocked.Add(ref ops, myOps);
                    Interlocked.Add(ref latencyTicks, myTicks);
                    // The pass ends when its last worker does, not when the progress loop below notices.
                    long ended = Stopwatch.GetTimestamp(), seen;
                    while ((seen = Interlocked.Read(ref lastEnd)) < ended && Interlocked.CompareExchange(ref lastEnd, ended, seen) != seen) { }
                }
                catch (Exception e)
                {
                    failure ??= e;
                }
            }) { IsBackground = true };
            workers[w].Start();
        }

        // The live speed: what was moved since the last look. If the pass ended during the wait, the
        // stretch ends where the work did, not when we looked (that made the last value of every pass too low),
        // and a very short last stretch is left out.
        long lastBytes = 0;
        double lastTime = 0;
        while (workers.Any(t => t.IsAlive))
        {
            Thread.Sleep(250);
            bool done = !workers.Any(t => t.IsAlive);
            double now = done ? Math.Max(0, Interlocked.Read(ref lastEnd) - startStamp) / (double)Stopwatch.Frequency : clock.Elapsed.TotalSeconds;
            long total = Interlocked.Read(ref bytes);
            if (now - lastTime >= (done ? 0.1 : 0.001)) live((total - lastBytes) / 1e6 / (now - lastTime));
            (lastBytes, lastTime) = (total, now);
        }
        double elapsed = Math.Max(0, lastEnd - startStamp) / (double)Stopwatch.Frequency;
        if (failure is not null) throw new BenchException(failure.Message);
        if (ops == 0 || elapsed <= 0 || cancel.IsCancellationRequested) return null;
        return (bytes / 1e6 / elapsed, ops / elapsed, latencyTicks / (double)ops * 1e6 / Stopwatch.Frequency);
    }
}
