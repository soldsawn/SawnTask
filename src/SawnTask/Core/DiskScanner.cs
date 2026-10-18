using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Enumeration;

namespace SawnTask.Core;

// One folder in the scanned tree. Only folders are kept in memory; files are summed
// into their folder as they are read (a full drive has millions of files, but only
// tens of thousands of folders).
public sealed class DirNode(string name, DirNode? parent)
{
    public string Name { get; } = name;
    public DirNode? Parent { get; } = parent;
    public List<DirNode> Children { get; } = [];

    public long Size { get; set; }           // all files below this folder, in bytes
    public long Allocated { get; set; }      // estimated size on disk (clusters; cloud-only files = 0)
    public long OwnFilesSize { get; set; }   // files directly in this folder
    public int OwnFiles { get; set; }
    public int Files { get; set; }           // all files below
    public int Folders { get; set; }         // all folders below
    public DateTime LastWriteUtc { get; set; }
    public bool Denied { get; set; }         // could not be read (permissions)
    public bool IsLink { get; set; }         // junction / symlink: shown, not followed

    public string FullPath => Parent is null ? Name : Path.Combine(Parent.FullPath, Name);

    // Removes this folder from the tree after it was moved to the Recycle Bin, and takes
    // its totals off every folder above it.
    public void RemoveFromTree()
    {
        if (Parent is null) return;
        Parent.Children.Remove(this);
        for (var p = Parent; p is not null; p = p.Parent)
        {
            p.Size -= Size;
            p.Allocated -= Allocated;
            p.Files -= Files;
            p.Folders -= Folders + 1;
        }
    }

    // Takes one deleted file off this folder and every folder above it.
    public void RemoveFile(long size, long allocated)
    {
        OwnFilesSize -= size;
        OwnFiles--;
        for (var p = this; p is not null; p = p.Parent)
        {
            p.Size -= size;
            p.Allocated -= allocated;
            p.Files--;
        }
    }

    // Finds a folder below this one by its full path (case-insensitive, like Windows).
    public DirNode? Find(string fullPath)
    {
        string root = FullPath.TrimEnd('\\');
        string target = fullPath.TrimEnd('\\');
        if (target.Equals(root, StringComparison.OrdinalIgnoreCase)) return this;
        if (!target.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase)) return null;
        var node = this;
        foreach (var part in target[(root.Length + 1)..].Split('\\'))
        {
            node = node.Children.FirstOrDefault(c => c.Name.Equals(part, StringComparison.OrdinalIgnoreCase));
            if (node is null) return null;
        }
        return node;
    }
}

public sealed record FileEntry(string Name, string Folder, long Size, DateTime LastWriteUtc)
{
    public string SizeText => Fmt.Bytes(Size);
    public string FullPath => Path.Combine(Folder, Name);
    public string Modified => LastWriteUtc.ToLocalTime().ToString("yyyy-MM-dd");
}

public sealed record TypeStat(string Extension, long Size, int Count, double Percent)
{
    // Same rule the scanner uses to group files by extension.
    public static string ExtensionOf(string fileName)
    {
        string ext = Path.GetExtension(fileName);
        return ext.Length == 0 || ext.Length > 12 ? "(none)" : ext;
    }

    public string SizeText => Fmt.Bytes(Size);
    public double BarWidth => Math.Max(1, Percent * 0.5); // the bar is 50 px wide at 100 %
}

public sealed record ScanResult(DirNode Root, List<FileEntry> LargestFiles, List<TypeStat> Types,
    long Files, long Folders, int DeniedFolders, List<string> DeniedPaths, int Links, TimeSpan Elapsed, bool Cancelled,
    long ClusterSize);

// Disk Space scanner, laid out like TreeSize.
// The tree's first levels are scanned in parallel; the counters can be read at any
// time from another thread to show progress.
public sealed class DiskScanner
{
    const int TopFileCount = 200;
    const int ParallelDepth = 3; // folders this close to the root are scanned on several threads

    // Cloud placeholders (OneDrive "online-only") report their full size but use no disk space.
    const FileAttributes RecallOnOpen = (FileAttributes)0x00040000;
    const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;
    const FileAttributes CloudOnly = FileAttributes.Offline | RecallOnOpen | RecallOnDataAccess;

    static readonly EnumerationOptions Options = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = false, // we want to know which folders were denied
        AttributesToSkip = 0,       // include hidden and system files: they use space too
        ReturnSpecialDirectories = false,
    };

    readonly object topGate = new();
    readonly PriorityQueue<FileEntry, long> top = new(); // min-heap: smallest of the top N first
    long topThreshold;
    readonly ConcurrentDictionary<string, (long Size, int Count)> types = new(StringComparer.OrdinalIgnoreCase);
    long clusterSize = 4096;
    int denied, links;
    readonly ConcurrentQueue<string> deniedPaths = new(); // a sample, for the "restart as admin" question

    long filesSeen, foldersSeen, bytesSeen;
    public long FilesSeen => Interlocked.Read(ref filesSeen);
    public long FoldersSeen => Interlocked.Read(ref foldersSeen);
    public long BytesSeen => Interlocked.Read(ref bytesSeen);
    public string CurrentFolder { get; private set; } = "";

    public Task<ScanResult> ScanAsync(string rootPath, CancellationToken cancel) => Task.Run(() => Scan(rootPath, cancel), CancellationToken.None);

    ScanResult Scan(string rootPath, CancellationToken cancel)
    {
        var clock = Stopwatch.StartNew();
        clusterSize = ClusterSize(rootPath);
        var root = new DirNode(rootPath.TrimEnd('\\') + (rootPath.Length <= 3 ? "\\" : ""), null);
        bool cancelled = false;
        try
        {
            ScanFolder(root, rootPath, 0, cancel);
        }
        catch (OperationCanceledException)
        {
            cancelled = true; // keep what was measured so far
        }
        catch (AggregateException e) when (e.Flatten().InnerExceptions.All(x => x is OperationCanceledException))
        {
            cancelled = true; // a parallel branch saw the cancel first
        }

        List<FileEntry> largest;
        lock (topGate) largest = top.UnorderedItems.Select(x => x.Element).OrderByDescending(f => f.Size).ToList();
        long total = Math.Max(1, root.Size);
        var typeList = types.Select(t => new TypeStat(t.Key, t.Value.Size, t.Value.Count, 100.0 * t.Value.Size / total))
            .OrderByDescending(t => t.Size).ToList();
        return new ScanResult(root, largest, typeList, FilesSeen, FoldersSeen, denied,
            deniedPaths.OrderBy(p => p.Length).ToList(), links, clock.Elapsed, cancelled, clusterSize);
    }

    void ScanFolder(DirNode node, string path, int depth, CancellationToken cancel)
    {
        cancel.ThrowIfCancellationRequested();
        CurrentFolder = path;
        Interlocked.Increment(ref foldersSeen);

        var localTypes = new Dictionary<string, (long Size, int Count)>(StringComparer.OrdinalIgnoreCase);
        var subfolders = new List<(DirNode Node, string Path)>();
        try
        {
            // FileSystemEnumerable reads the directory in large batches and lets us pick
            // fields straight from the native entry, without creating a FileInfo per file.
            var entries = new FileSystemEnumerable<(string Name, bool IsDir, long Length, FileAttributes Attr, DateTime Write)>(
                path,
                (ref FileSystemEntry e) => (e.FileName.ToString(), e.IsDirectory, e.Length, e.Attributes, e.LastWriteTimeUtc.UtcDateTime),
                Options);

            foreach (var e in entries)
            {
                if (e.IsDir)
                {
                    var child = new DirNode(e.Name, node) { LastWriteUtc = e.Write };
                    node.Children.Add(child);
                    if ((e.Attr & FileAttributes.ReparsePoint) != 0)
                    {
                        // Junctions and symlinks point at folders counted elsewhere (or
                        // loop back): show them, never follow them, like TreeSize.
                        child.IsLink = true;
                        Interlocked.Increment(ref links);
                        continue;
                    }
                    subfolders.Add((child, Path.Combine(path, e.Name)));
                    continue;
                }

                long onDisk = (e.Attr & CloudOnly) != 0 ? 0 : (e.Length + clusterSize - 1) / clusterSize * clusterSize;
                node.OwnFilesSize += e.Length;
                node.Allocated += onDisk;
                node.OwnFiles++;
                if (e.Write > node.LastWriteUtc) node.LastWriteUtc = e.Write;

                string ext = TypeStat.ExtensionOf(e.Name);
                var t = localTypes.GetValueOrDefault(ext);
                localTypes[ext] = (t.Size + e.Length, t.Count + 1);

                if (e.Length > Volatile.Read(ref topThreshold)) OfferLargest(new FileEntry(e.Name, path, e.Length, e.Write));
            }
        }
        catch (UnauthorizedAccessException)
        {
            node.Denied = true;
            if (Interlocked.Increment(ref denied) <= 50) deniedPaths.Enqueue(path);
        }
        catch (IOException)
        {
            node.Denied = true; // vanished while scanning, or device error
            Interlocked.Increment(ref denied);
        }

        Interlocked.Add(ref filesSeen, node.OwnFiles);
        Interlocked.Add(ref bytesSeen, node.OwnFilesSize);
        foreach (var (ext, value) in localTypes)
            types.AddOrUpdate(ext, value, (_, old) => (old.Size + value.Size, old.Count + value.Count));

        if (depth < ParallelDepth && subfolders.Count > 1)
        {
            var parallel = new ParallelOptions { CancellationToken = cancel, MaxDegreeOfParallelism = Environment.ProcessorCount };
            Parallel.ForEach(subfolders, parallel, s => ScanFolder(s.Node, s.Path, depth + 1, cancel));
        }
        else
        {
            foreach (var s in subfolders) ScanFolder(s.Node, s.Path, depth + 1, cancel);
        }

        // Children are complete; add them up. Each node is only written by its own call,
        // so no locking is needed here.
        node.Size = node.OwnFilesSize;
        node.Files = node.OwnFiles;
        foreach (var c in node.Children)
        {
            node.Size += c.Size;
            node.Allocated += c.Allocated;
            node.Files += c.Files;
            node.Folders += c.Folders + 1;
            if (c.LastWriteUtc > node.LastWriteUtc) node.LastWriteUtc = c.LastWriteUtc;
        }
        node.Children.Sort((a, b) => b.Size.CompareTo(a.Size)); // TreeSize order: biggest first
    }

    void OfferLargest(FileEntry file)
    {
        lock (topGate)
        {
            if (top.Count < TopFileCount) top.Enqueue(file, file.Size);
            else if (file.Size > top.Peek().Size) top.EnqueueDequeue(file, file.Size);
            if (top.Count == TopFileCount) Volatile.Write(ref topThreshold, top.Peek().Size);
        }
    }

    static long ClusterSize(string path)
    {
        try
        {
            string root = Path.GetPathRoot(Path.GetFullPath(path)) ?? path;
            return GetDiskFreeSpaceW(root, out uint sectorsPerCluster, out uint bytesPerSector, out _, out _)
                ? (long)sectorsPerCluster * bytesPerSector
                : 4096;
        }
        catch
        {
            return 4096;
        }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern bool GetDiskFreeSpaceW(string root, out uint sectorsPerCluster, out uint bytesPerSector, out uint freeClusters, out uint totalClusters);
}
