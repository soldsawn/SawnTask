using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using SawnTask.Core;

namespace SawnTask.Ui;

// TreeSize-style Disk Space page: pick a drive or folder, scan, then browse the folder tree
// sorted by size, with the largest files and a file-type breakdown on the right.
// Folders and files can be moved to the Recycle Bin; folders Windows hides from normal
// users can be included by restarting as administrator.
public partial class DiskSpacePage : UserControl
{
    readonly ObservableCollection<FolderRow> rows = [];
    readonly DispatcherTimer progressTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    DiskScanner? scanner;
    CancellationTokenSource? cancel;
    ScanResult? result;
    string scannedPath = "";
    long expectedBytes; // used space of the drive being scanned, for the progress bar (0 = unknown)
    DataGrid? activeGrid; // which list the toolbar button and the Delete key act on

    public DiskSpacePage()
    {
        InitializeComponent();
        TreeGrid.ItemsSource = rows;
        progressTimer.Tick += (_, _) => ShowProgress();
        Loaded += (_, _) => { if (DriveBox.Items.Count == 0) FillDrives(); };
        AdminBtn.Visibility = Elevation.IsElevated ? Visibility.Collapsed : Visibility.Visible;
    }

    // Set by the --snapshot test mode: never show dialogs that would wait for a click.
    public bool Automated { get; set; }

    void FillDrives()
    {
        foreach (var d in DriveInfo.GetDrives())
        {
            if (!d.IsReady || d.DriveType is not (DriveType.Fixed or DriveType.Removable)) continue;
            long used = d.TotalSize - d.TotalFreeSpace;
            string label = d.VolumeLabel.Length > 0 ? d.VolumeLabel : Loc.T("Disk.LocalDisk");
            DriveBox.Items.Add(new ComboBoxItem
            {
                Content = Loc.F("Disk.DriveItem", d.Name, label, Fmt.Bytes(used), Fmt.Bytes(d.TotalSize)),
                Tag = d.Name,
            });
        }
        DriveBox.SelectedIndex = 0;
    }

    // After a language switch: rebuild the texts this page writes from code.
    public void Relocalize()
    {
        int selected = DriveBox.SelectedIndex;
        DriveBox.Items.Clear();
        FillDrives();
        DriveBox.SelectedIndex = Math.Max(0, Math.Min(selected, DriveBox.Items.Count - 1));
        if (result is not null) RebuildRows(TreeGrid.SelectedItem is FolderRow row ? row.Node ?? row.Owner : null);
        if (result is not null) UpdateSummary();
        else if (scanner is null) StatusLine.Text = Loc.T("Disk.Pick");
    }

    void Scan_Click(object sender, RoutedEventArgs e)
    {
        if (DriveBox.SelectedItem is ComboBoxItem { Tag: string path }) _ = StartScan(path);
    }

    void ChooseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = Loc.T("Disk.ChooseFolder") };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) _ = StartScan(dialog.FolderName);
    }

    void Stop_Click(object sender, RoutedEventArgs e) => cancel?.Cancel();

    public async Task StartScan(string path)
    {
        if (scanner is not null) return; // one scan at a time
        cancel = new CancellationTokenSource();
        scanner = new DiskScanner();
        scannedPath = path;
        result = null;
        rows.Clear();
        FilesGrid.ItemsSource = null;
        TypesGrid.ItemsSource = null;

        expectedBytes = 0;
        if (Path.GetPathRoot(path) == path)
        {
            var drive = new DriveInfo(path);
            expectedBytes = drive.TotalSize - drive.TotalFreeSpace;
        }
        ScanProgress.IsIndeterminate = expectedBytes == 0;
        ScanProgress.Value = 0;
        ScanBtn.IsEnabled = false;
        StopBtn.IsEnabled = true;
        progressTimer.Start();

        ScanResult scanned;
        try
        {
            scanned = await scanner.ScanAsync(path, cancel.Token);
        }
        catch (Exception ex)
        {
            StatusLine.Text = Loc.F("Disk.ScanFailed", ex.Message);
            Finish();
            return;
        }
        Finish();
        Show(scanned);
        if (!scanned.Cancelled && scanned.DeniedFolders > 0 && !Elevation.IsElevated && !Automated)
            AskToElevate(path, scanned);
    }

    void Finish()
    {
        progressTimer.Stop();
        scanner = null;
        ScanBtn.IsEnabled = true;
        StopBtn.IsEnabled = false;
        ScanProgress.IsIndeterminate = false;
    }

    void ShowProgress()
    {
        if (scanner is null) return;
        long bytes = scanner.BytesSeen;
        if (expectedBytes > 0) ScanProgress.Value = Math.Min(100, 100.0 * bytes / expectedBytes);
        StatusLine.Text = (expectedBytes > 0 ? $"{ScanProgress.Value:0}%  ·  " : "") +
                          Loc.F("Disk.Progress", scanner.FilesSeen, scanner.FoldersSeen, Fmt.Bytes(bytes), scanner.CurrentFolder);
    }

    void Show(ScanResult scanned)
    {
        result = scanned;
        ScanProgress.Value = scanned.Cancelled ? ScanProgress.Value : 100;
        var root = FolderRow.ForRoot(scanned.Root);
        rows.Add(root);
        Expand(root);
        FilesGrid.ItemsSource = scanned.LargestFiles;
        TypesGrid.ItemsSource = scanned.Types;
        UpdateSummary();
    }

    void UpdateSummary(string extra = "")
    {
        if (result is null) return;
        string denied = result.DeniedFolders == 0 ? ""
            : Loc.F(Elevation.IsElevated ? "Disk.DeniedAdmin" : "Disk.DeniedUser", result.DeniedFolders);
        string coverage = expectedBytes > 0 ? Loc.F("Disk.Coverage", 100.0 * result.Root.Allocated / expectedBytes) : "";
        StatusLine.Text = (result.Cancelled ? Loc.T("Disk.Stopped") : "") +
                          Loc.F("Disk.Summary", result.Root.Files, result.Root.Folders, Fmt.Bytes(result.Root.Size), result.Elapsed.TotalSeconds) +
                          coverage + denied + extra;
    }

    // ---- restart as administrator ----

    void AskToElevate(string path, ScanResult scanned)
    {
        var examples = scanned.DeniedPaths.Take(5).Select(p => "   • " + p);
        string more = scanned.DeniedFolders > 5 ? Loc.F("Disk.ElevateMore", scanned.DeniedFolders - 5) : "";
        var answer = MessageBox.Show(Window.GetWindow(this),
            Loc.F("Disk.Elevate", scanned.DeniedFolders, string.Join("\n", examples), more, path),
            Loc.T("Disk.ElevateTitle"), MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Yes) RestartElevated(path);
    }

    void RestartAdmin_Click(object sender, RoutedEventArgs e) =>
        RestartElevated(scannedPath.Length > 0 ? scannedPath : (DriveBox.SelectedItem as ComboBoxItem)?.Tag as string);

    void RestartElevated(string? scanPath)
    {
        // The new copy opens on this page and starts scanning the same place by itself.
        string args = scanPath is null ? "--open-disk" : "--open-disk " + Elevation.QuoteForArgs(scanPath);
        if (Elevation.Restart(args, out bool declined, out string error)) Application.Current.Shutdown();
        else MessageBox.Show(Window.GetWindow(this), declined ? Loc.T("Disk.ElevateCancelled") : error, Loc.T("Disk.ElevateTitle"),
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // ---- tree expand / collapse ----

    void Toggle_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: FolderRow row })
        {
            Toggle(row);
            e.Handled = true; // do not also start a row selection / double-click
        }
    }

    void TreeGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (TreeGrid.SelectedItem is FolderRow row) Toggle(row);
    }

    void Toggle(FolderRow row)
    {
        if (row.IsExpanded) Collapse(row);
        else Expand(row);
    }

    void Expand(FolderRow row)
    {
        if (!row.CanExpand || row.IsExpanded || row.Node is null) return;
        int at = rows.IndexOf(row) + 1;
        foreach (var child in FolderRow.ChildrenOf(row.Node, row.Level + 1)) rows.Insert(at++, child);
        row.SetExpanded(true);
    }

    void Collapse(FolderRow row)
    {
        int at = rows.IndexOf(row) + 1;
        while (at < rows.Count && rows[at].Level > row.Level) rows.RemoveAt(at);
        row.SetExpanded(false);
    }

    // Rows hold copies of the sizes, so after a deletion the visible tree is rebuilt from
    // the (already corrected) folder nodes, re-opening every folder that was open before.
    void RebuildRows(DirNode? select)
    {
        if (result is null) return;
        var open = rows.Where(r => r.IsExpanded && r.Node is not null).Select(r => r.Node!).ToHashSet();
        rows.Clear();
        var root = FolderRow.ForRoot(result.Root);
        rows.Add(root);
        for (int i = 0; i < rows.Count; i++)
            if (rows[i].Node is DirNode n && (open.Contains(n) || i == 0)) Expand(rows[i]);

        var target = rows.FirstOrDefault(r => r.Node == select) ?? root;
        TreeGrid.SelectedItem = target;
        TreeGrid.ScrollIntoView(target);
    }

    // Opens the biggest child, then its biggest child, and so on: the quickest way to find
    // where the space went.
    void ExpandLargest_Click(object sender, RoutedEventArgs e) => ExpandLargestPath(TreeGrid.SelectedItem as FolderRow);

    public void ExpandLargestPath(FolderRow? start = null)
    {
        var row = start ?? rows.FirstOrDefault();
        for (int i = 0; i < 12 && row is not null; i++)
        {
            Expand(row);
            row = rows.Skip(rows.IndexOf(row) + 1).TakeWhile(r => r.Level > row.Level)
                      .FirstOrDefault(r => r.Level == row.Level + 1 && r.CanExpand);
        }
        if (row is not null)
        {
            TreeGrid.SelectedItem = row;
            TreeGrid.ScrollIntoView(row);
        }
    }

    // ---- Recycle Bin ----

    void Grid_GotFocus(object sender, KeyboardFocusChangedEventArgs e) => activeGrid = sender as DataGrid;

    void Grid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete) return;
        e.Handled = true; // the DataGrid would otherwise try to delete the row itself
        if (sender == FilesGrid) RecycleSelectedFile();
        else RecycleSelectedFolder();
    }

    void Recycle_Click(object sender, RoutedEventArgs e)
    {
        if (activeGrid == FilesGrid && FilesGrid.SelectedItem is not null) RecycleSelectedFile();
        else RecycleSelectedFolder();
    }

    void RecycleFolder_Click(object sender, RoutedEventArgs e) => RecycleSelectedFolder();

    void RecycleFile_Click(object sender, RoutedEventArgs e) => RecycleSelectedFile();

    void RecycleSelectedFolder()
    {
        if (result is null || TreeGrid.SelectedItem is not FolderRow row) return;
        if (row.Node is not DirNode node)
        {
            Tell(Loc.T("Disk.FilesRow"));
            return;
        }
        if (node.Parent is null)
        {
            Tell(Loc.T("Disk.RootBlocked"));
            return;
        }
        if (node.IsLink)
        {
            Tell(Loc.T("Disk.LinkBlocked"));
            return;
        }

        string path = node.FullPath;
        if (!Confirm(path, node.Size, Loc.F("Disk.FolderContents", node.Files, node.Folders))) return;
        if (!RecycleBin.Move(path, OwnerHandle(), out string error))
        {
            Tell(error);
            return;
        }

        var parent = node.Parent;
        node.RemoveFromTree();
        string prefix = path + "\\";
        result.LargestFiles.RemoveAll(f => f.Folder.Equals(path, StringComparison.OrdinalIgnoreCase)
                                           || f.Folder.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        FilesGrid.ItemsSource = result.LargestFiles.ToList();
        RebuildRows(parent);
        UpdateSummary(Loc.F("Disk.MovedFolder", Fmt.Bytes(node.Size)));
    }

    void RecycleSelectedFile()
    {
        if (result is null || FilesGrid.SelectedItem is not FileEntry file) return;
        if (!Confirm(file.FullPath, file.Size, Loc.T("Disk.OneFile"))) return;
        if (!RecycleBin.Move(file.FullPath, OwnerHandle(), out string error))
        {
            Tell(error);
            return;
        }

        // Take the file off its folder and every folder above, then off the side lists.
        long onDisk = (file.Size + result.ClusterSize - 1) / result.ClusterSize * result.ClusterSize;
        var folder = result.Root.Find(file.Folder);
        folder?.RemoveFile(file.Size, onDisk);
        result.LargestFiles.Remove(file);
        FilesGrid.ItemsSource = result.LargestFiles.ToList();

        string ext = TypeStat.ExtensionOf(file.Name);
        long total = Math.Max(1, result.Root.Size);
        var types = result.Types
            .Select(t => t.Extension.Equals(ext, StringComparison.OrdinalIgnoreCase) ? t with { Size = t.Size - file.Size, Count = t.Count - 1 } : t)
            .Where(t => t.Count > 0)
            .Select(t => t with { Percent = 100.0 * t.Size / total })
            .OrderByDescending(t => t.Size)
            .ToList();
        result.Types.Clear();
        result.Types.AddRange(types);
        TypesGrid.ItemsSource = types;

        RebuildRows(folder);
        UpdateSummary(Loc.F("Disk.MovedFile", Fmt.Bytes(file.Size)));
    }

    bool Confirm(string path, long size, string contents)
    {
        // Extra care for places where deleting things can break Windows or installed programs.
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string[] system =
        [
            windows,
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        ];
        bool risky = system.Any(s => s.Length > 0 && (path.Equals(s, StringComparison.OrdinalIgnoreCase)
                                                      || path.StartsWith(s + "\\", StringComparison.OrdinalIgnoreCase)))
                     || Path.GetDirectoryName(path) == Path.GetPathRoot(path);
        string warning = risky ? Loc.T("Disk.Risk") : "";
        return MessageBox.Show(Window.GetWindow(this),
                   Loc.F("Disk.Confirm", path, Fmt.Bytes(size), contents, warning),
                   Loc.T("Disk.MoveToRecycle"), MessageBoxButton.YesNo, risky ? MessageBoxImage.Warning : MessageBoxImage.Question,
                   MessageBoxResult.No) == MessageBoxResult.Yes;
    }

    void Tell(string text) =>
        MessageBox.Show(Window.GetWindow(this), text, Loc.T("Disk.Title"), MessageBoxButton.OK, MessageBoxImage.Information);

    IntPtr OwnerHandle() => Window.GetWindow(this) is Window w ? new WindowInteropHelper(w).Handle : IntPtr.Zero;

    // ---- context menu actions ----

    void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (TreeGrid.SelectedItem is FolderRow row) Process.Start("explorer.exe", $"\"{row.FullPath}\"");
    }

    void CopyPath_Click(object sender, RoutedEventArgs e)
    {
        if (TreeGrid.SelectedItem is FolderRow row) Clipboard.SetText(row.FullPath);
    }

    void ScanThis_Click(object sender, RoutedEventArgs e)
    {
        if (TreeGrid.SelectedItem is not FolderRow { Node: { IsLink: false } node }) return;
        if (node.Denied && !Elevation.IsElevated)
        {
            var answer = MessageBox.Show(Window.GetWindow(this), Loc.F("Disk.ElevateFolder", node.FullPath),
                Loc.T("Disk.ElevateTitle"), MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Yes) RestartElevated(node.FullPath);
            return;
        }
        _ = StartScan(node.FullPath);
    }

    void FilesGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e) => OpenFileLocation_Click(sender, e);

    void OpenFileLocation_Click(object sender, RoutedEventArgs e)
    {
        if (FilesGrid.SelectedItem is FileEntry f) Process.Start("explorer.exe", $"/select,\"{f.FullPath}\"");
    }

    void CopyFilePath_Click(object sender, RoutedEventArgs e)
    {
        if (FilesGrid.SelectedItem is FileEntry f) Clipboard.SetText(f.FullPath);
    }
}
