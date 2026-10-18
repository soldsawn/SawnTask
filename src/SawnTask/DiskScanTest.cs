using System.IO;
using SawnTask.Core;

namespace SawnTask;

// "SawnTask.exe --disk-scan C:\ out.txt": run the Disk Space scanner without a window and
// compare its total with what Windows reports as used space on that drive.
public static class DiskScanTest
{
    public static int Run(string path, TextWriter o)
    {
        var scanner = new DiskScanner();
        var result = scanner.ScanAsync(path, CancellationToken.None).GetAwaiter().GetResult();
        var root = result.Root;

        o.WriteLine($"scanned {path} in {result.Elapsed.TotalSeconds:0.0} s: {result.Files:N0} files, {result.Folders:N0} folders, " +
                    $"{result.DeniedFolders} denied, {result.Links} links not followed");
        o.WriteLine($"total size {Fmt.Bytes(root.Size)}, on disk (estimated) {Fmt.Bytes(root.Allocated)}");

        if (path.Length <= 3)
        {
            var drive = new DriveInfo(path);
            long used = drive.TotalSize - drive.TotalFreeSpace;
            o.WriteLine($"Windows says used: {Fmt.Bytes(used)}; scan found {100.0 * root.Allocated / used:0.0}% of it " +
                        "(the rest is mostly folders we may not read without admin, plus NTFS metadata, VSS snapshots and the hibernation file)");
        }

        o.WriteLine();
        o.WriteLine("TOP FOLDERS");
        foreach (var c in root.Children.Take(12))
            o.WriteLine($"  {Fmt.Bytes(c.Size),10}  {100.0 * c.Size / Math.Max(1, root.Size),5:0.0}%  {c.Files,9:N0} files  {c.Name}{(c.Denied ? "  [denied]" : "")}{(c.IsLink ? "  [link]" : "")}");

        o.WriteLine();
        o.WriteLine("LARGEST FILES");
        foreach (var f in result.LargestFiles.Take(10))
            o.WriteLine($"  {f.SizeText,10}  {f.FullPath}");

        o.WriteLine();
        o.WriteLine("FILE TYPES");
        foreach (var t in result.Types.Take(10))
            o.WriteLine($"  {t.SizeText,10}  {t.Percent,5:0.0}%  {t.Count,9:N0}  {t.Extension}");
        return 0;
    }
}
