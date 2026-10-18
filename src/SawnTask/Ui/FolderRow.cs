using System.ComponentModel;
using System.Windows;
using SawnTask.Core;

namespace SawnTask.Ui;

// One row of the TreeSize-style tree. WPF has no tree-with-columns control, so the tree is
// a flat list: each row knows its depth (Level), and expanding a folder inserts its
// children right below it. The DataGrid then only has to draw the rows on screen.
public sealed class FolderRow : INotifyPropertyChanged
{
    const double BarWidthAt100 = 70;

    FolderRow(DirNode? node, DirNode parentOrSelf, int level, string name, long size, long allocated, int files, int folders,
        DateTime modified, long parentSize)
    {
        Node = node;
        Owner = parentOrSelf;
        Level = level;
        Name = name;
        Size = size;
        Allocated = allocated;
        Files = files;
        Folders = folders;
        ModifiedUtc = modified;
        Percent = parentSize > 0 ? 100.0 * size / parentSize : 100;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    // Node is null for the "[N files]" row that stands for the files directly in a folder.
    public DirNode? Node { get; }
    public DirNode Owner { get; }
    public int Level { get; }
    public string Name { get; }
    public long Size { get; }
    public long Allocated { get; }
    public int Files { get; }
    public int Folders { get; }
    public DateTime ModifiedUtc { get; }
    public double Percent { get; }

    public bool CanExpand => Node is { IsLink: false } n && (n.Children.Count > 0 || n.OwnFiles > 0);
    public bool IsExpanded { get; private set; }
    public string Glyph => !CanExpand ? "" : IsExpanded ? "▾" : "▸";
    public Thickness Indent => new(Level * 12, 0, 0, 0);
    public string Icon => Node is null ? "▤" : Node.IsLink ? "↪" : Node.Denied ? "⊘" : "▰";
    public string FullPath => Node?.FullPath ?? Owner.FullPath;

    public string SizeText => Node is { Denied: true, Size: 0 } ? Loc.T("Disk.Denied") : Node is { IsLink: true } ? Loc.T("Disk.Link") : Fmt.Bytes(Size);
    public string AllocatedText => Fmt.Bytes(Allocated);
    public string PercentText => $"{Percent:0.0}%";
    public double BarWidth => Math.Max(0, Percent / 100 * BarWidthAt100);
    public string FilesText => Files.ToString("N0");
    public string FoldersText => Node is null ? "" : Folders.ToString("N0");
    public string Modified => ModifiedUtc == default ? "" : ModifiedUtc.ToLocalTime().ToString("yyyy-MM-dd");

    public static FolderRow ForRoot(DirNode root) =>
        new(root, root, 0, root.Name, root.Size, root.Allocated, root.Files, root.Folders, root.LastWriteUtc, root.Size);

    // Children of a folder, biggest first (the scanner already sorted them), with the
    // folder's own files as one "[N files]" row placed by size like TreeSize does.
    public static List<FolderRow> ChildrenOf(DirNode node, int level)
    {
        var rows = node.Children
            .Select(c => new FolderRow(c, node, level, c.Name, c.Size, c.Allocated, c.Files, c.Folders, c.LastWriteUtc, node.Size))
            .ToList();
        if (node.OwnFiles > 0)
        {
            var files = new FolderRow(null, node, level, Loc.F("Disk.FilesRowName", node.OwnFiles), node.OwnFilesSize,
                node.Allocated - node.Children.Sum(c => c.Allocated), node.OwnFiles, 0, default, node.Size);
            int at = rows.FindIndex(r => r.Size < files.Size);
            rows.Insert(at < 0 ? rows.Count : at, files);
        }
        return rows;
    }

    public void SetExpanded(bool expanded)
    {
        IsExpanded = expanded;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Glyph)));
    }
}
