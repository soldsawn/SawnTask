using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SawnTask.Core;
using SawnTask.Native;

namespace SawnTask.Ui;

public partial class DiskBenchPage : UserControl
{
    sealed record DriveEntry(string Root, string Model, string Kind, long Free, StorageDevices.Disk? Disk);

    static readonly (string Text, long Bytes)[] Sizes =
        [("256 MB", 256L << 20), ("512 MB", 512L << 20), ("1 GB", 1L << 30), ("2 GB", 2L << 30), ("4 GB", 4L << 30), ("8 GB", 8L << 30)];
    static readonly int[] PassChoices = [1, 2, 3, 5];

    const string SeqTest = "SEQ1M Q8T1", RndTest = "RND4K Q1T1";
    const long SpareBytes = 256L << 20; // free space kept beyond the test file

    readonly Series live = new(240);
    readonly Dictionary<(string Test, bool Write), BenchResult> results = [];
    List<StorageDevices.Disk> disks = [];
    CancellationTokenSource? cancel;
    bool running, loading;
    DiskVerdict? verdict;
    string statusKey = "DB.Idle";
    object?[] statusArgs = [];

    public DiskBenchPage()
    {
        InitializeComponent();
        Graph.AddLine(live, Color.FromRgb(0x4F, 0xC3, 0xF7), 1.6);
        loading = true;
        foreach (var (text, bytes) in Sizes) SizeBox.Items.Add(new ComboBoxItem { Content = text, Tag = bytes });
        foreach (int n in PassChoices) PassBox.Items.Add(new ComboBoxItem { Content = n.ToString(), Tag = n });
        foreach (var profile in Enum.GetValues<BenchProfile>()) ProfileBox.Items.Add(new ComboBoxItem { Tag = profile });
        ProfileBox.SelectedIndex = (int)BenchProfile.Normal;
        loading = false;
        ApplyProfile();
        Relocalize();
    }

    // The page was opened: look at the drives again (a USB stick may have come or gone).
    public async void Show()
    {
        if (running) return;
        disks = await Task.Run(StorageDevices.List);
        PopulateDrives();
        // For --snapshot: SAWNTASK_BENCH=quick|normal|thorough starts a test as soon as the page opens.
        if (Environment.GetEnvironmentVariable("SAWNTASK_BENCH") is { Length: > 0 } auto && Enum.TryParse(auto, true, out BenchProfile profile))
        {
            ProfileBox.SelectedIndex = (int)profile;
            Start_Click(this, new RoutedEventArgs());
        }
    }

    public void Relocalize()
    {
        foreach (ComboBoxItem item in ProfileBox.Items) item.Content = Loc.T("DB.Profile." + (BenchProfile)item.Tag);
        PopulateDrives();
        ShowStatus();
        UpdateCards();
        BuildTable();
        ShowVerdict();
    }

    // Stops a running test (the window is closing); the test file goes with it.
    public void Cancel() => cancel?.Cancel();

    // ---- drives ----

    void PopulateDrives()
    {
        string? keep = (DriveBox.SelectedItem as ComboBoxItem)?.Tag is DriveEntry d ? d.Root : null;
        DriveBox.Items.Clear();
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable) || !drive.IsReady) continue;
            var disk = StorageDevices.DiskNumberOf(drive.Name) is int n ? disks.FirstOrDefault(x => x.Number == n) : null;
            string model = disk?.Model ?? drive.VolumeLabel;
            string kind = disk is null ? "" : $"{disk.Bus} {disk.Kind}".Trim();
            var entry = new DriveEntry(drive.Name, model, kind, drive.AvailableFreeSpace, disk);
            DriveBox.Items.Add(new ComboBoxItem
            {
                Content = Loc.F("DB.DriveItem", drive.Name.TrimEnd('\\'), model, kind, Fmt.Bytes(drive.AvailableFreeSpace)),
                Tag = entry,
            });
        }
        var chosen = DriveBox.Items.Cast<ComboBoxItem>().FirstOrDefault(i => ((DriveEntry)i.Tag).Root == keep)
                     ?? DriveBox.Items.Cast<ComboBoxItem>().FirstOrDefault();
        DriveBox.SelectedItem = chosen;
        StartBtn.IsEnabled = !running && chosen is not null;
    }

    // ---- settings ----

    void Profile_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!loading) ApplyProfile();
    }

    // A profile just fills in the two advanced boxes; the test always reads those.
    void ApplyProfile()
    {
        if (ProfileBox.SelectedItem is not ComboBoxItem { Tag: BenchProfile profile }) return;
        var o = BenchOptions.For(profile);
        loading = true;
        SizeBox.SelectedItem = SizeBox.Items.Cast<ComboBoxItem>().First(i => (long)i.Tag == o.FileBytes);
        PassBox.SelectedItem = PassBox.Items.Cast<ComboBoxItem>().First(i => (int)i.Tag == o.Passes);
        loading = false;
    }

    // Hand-picked size or passes no longer match a profile.
    void Manual_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (loading) return;
        loading = true;
        ProfileBox.SelectedItem = null;
        loading = false;
    }

    void Advanced_Changed(object sender, RoutedEventArgs e) =>
        AdvancedPanel.Visibility = AdvancedToggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

    BenchOptions CurrentOptions()
    {
        long size = SizeBox.SelectedItem is ComboBoxItem { Tag: long s } ? s : 1L << 30;
        int passes = PassBox.SelectedItem is ComboBoxItem { Tag: int p } ? p : 3;
        return new BenchOptions(size, passes, passes switch { 1 => 2, 2 or 3 => 3, _ => 5 });
    }

    // ---- running ----

    async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (running || (DriveBox.SelectedItem as ComboBoxItem)?.Tag is not DriveEntry drive) return;
        var options = CurrentOptions();
        long needed = options.FileBytes + SpareBytes;
        long free = 0;
        try { free = new DriveInfo(drive.Root).AvailableFreeSpace; } catch { /* the drive went away: the run reports it */ }
        if (free < needed)
        {
            ThemedMessageBox.Show(Window.GetWindow(this), Loc.F("DB.NoSpace", Fmt.Bytes(needed)), Loc.T("DB.Title"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        results.Clear();
        verdict = null;
        ShowVerdict();
        live.Clear();
        Graph.InvalidateVisual();
        UpdateCards();
        BuildTable();
        SetRunning(true);
        Progress.Value = 0;
        cancel = new CancellationTokenSource();
        var token = cancel.Token;
        int passes = options.Passes;
        try
        {
            await Task.Run(() => DiskBench.Run(drive.Root, options,
                p => Dispatcher.BeginInvoke(() => OnProgress(p, passes)),
                r => Dispatcher.BeginInvoke(() => OnResult(r)),
                token));
            SetStatus(token.IsCancellationRequested ? "DB.Stopped" : "DB.Done");
            if (!token.IsCancellationRequested)
            {
                Progress.Value = 1;
                if (Get(SeqTest, false) is { } seq) verdict = DiskVerdict.Evaluate(drive.Disk?.Bus ?? "", drive.Disk?.Kind ?? "", seq.MBps);
                ShowVerdict();
            }
        }
        catch (BenchException ex)
        {
            SetStatus("DB.Failed", ex.Message);
        }
        catch (Exception ex)
        {
            SetStatus("DB.Failed", ex.Message);
        }
        finally
        {
            SetRunning(false);
            cancel.Dispose();
            cancel = null;
        }
    }

    void Stop_Click(object sender, RoutedEventArgs e) => cancel?.Cancel();

    void SetRunning(bool value)
    {
        running = value;
        StartBtn.IsEnabled = !value && DriveBox.Items.Count > 0;
        StopBtn.IsEnabled = value;
        DriveBox.IsEnabled = ProfileBox.IsEnabled = SizeBox.IsEnabled = PassBox.IsEnabled = !value;
    }

    void OnProgress(BenchProgress p, int passes)
    {
        if (!running) return;
        if (p.LiveMBps > 0)
        {
            live.Add(p.LiveMBps);
            Graph.Advance(TimeSpan.FromMilliseconds(250));
            GraphScale.Text = Loc.F("DB.GraphScale", Graph.CurrentMax);
        }
        if (p.Phase == "prepare")
        {
            SetStatus("DB.Preparing", p.LiveMBps);
            return;
        }
        Progress.Value = ((p.Step - 1) * passes + (p.Pass - 1)) / (double)(p.Steps * passes);
        SetStatus("DB.Running", p.Step, p.Steps, p.Test, Loc.T(p.Write ? "DB.Write" : "DB.Read"), p.Pass, p.Passes, p.LiveMBps);
    }

    void OnResult(BenchResult r)
    {
        results[(r.Test, r.Write)] = r;
        UpdateCards();
        BuildTable();
    }

    // ---- showing things ----

    void SetStatus(string key, params object?[] args)
    {
        statusKey = key;
        statusArgs = args;
        ShowStatus();
    }

    void ShowStatus()
    {
        StatusLine.Text = Loc.F(statusKey, statusArgs);
    }

    static string Speed(BenchResult? r) => r is null ? "—" : r.MBps >= 100 ? $"{r.MBps:0} MB/s" : $"{r.MBps:0.0} MB/s";

    void UpdateCards()
    {
        SeqReadValue.Text = Speed(Get(SeqTest, false));
        SeqWriteValue.Text = Speed(Get(SeqTest, true));
        RndReadValue.Text = Speed(Get(RndTest, false));
        RndWriteValue.Text = Speed(Get(RndTest, true));
    }

    void ShowVerdict()
    {
        VerdictCard.Visibility = verdict is null ? Visibility.Collapsed : Visibility.Visible;
        if (verdict is null) return;
        var color = verdict.Level switch
        {
            VerdictLevel.Fast => Color.FromRgb(0x66, 0xBB, 0x6A),
            VerdictLevel.Normal => Color.FromRgb(0x4F, 0xC3, 0xF7),
            _ => Color.FromRgb(0xFF, 0xA7, 0x26),
        };
        VerdictTitle.Text = $"{Loc.T("DB.Verdict")}: {Loc.T("DB.Level." + verdict.Level)}";
        VerdictTitle.Foreground = new SolidColorBrush(color);
        string kind = Loc.T(verdict.ClassKey);
        VerdictText.Text = verdict.Level switch
        {
            VerdictLevel.Fast => Loc.F("DB.V.Fast", kind),
            VerdictLevel.Normal => Loc.F("DB.V.Normal", kind),
            _ => Loc.F("DB.V.Low", kind, Loc.T(verdict.HintKey!)),
        };
    }

    BenchResult? Get(string test, bool write) => results.GetValueOrDefault((test, write));

    // All eight measurements: one row per test, read columns then write columns.
    void BuildTable()
    {
        Table.Children.Clear();
        Table.RowDefinitions.Clear();
        Table.ColumnDefinitions.Clear();
        string[] headers = ["DB.Col.Test", "DB.Col.Read", "DB.Col.ReadIops", "DB.Col.ReadLat", "DB.Col.Write", "DB.Col.WriteIops", "DB.Col.WriteLat"];
        for (int c = 0; c < headers.Length; c++)
            Table.ColumnDefinitions.Add(new ColumnDefinition { Width = c == 0 ? new GridLength(1.4, GridUnitType.Star) : new GridLength(1, GridUnitType.Star) });

        void Cell(int row, int col, string text, bool header = false)
        {
            var block = new TextBlock
            {
                Text = text,
                Margin = new Thickness(0, 3, 8, 3),
                HorizontalAlignment = col == 0 ? HorizontalAlignment.Left : HorizontalAlignment.Right,
                FontFamily = header || col == 0 ? new FontFamily("Segoe UI") : new FontFamily("Consolas"),
            };
            if (header) block.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
            Grid.SetRow(block, row);
            Grid.SetColumn(block, col);
            Table.Children.Add(block);
        }

        Table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (int c = 0; c < headers.Length; c++) Cell(0, c, Loc.T(headers[c]), header: true);
        for (int i = 0; i < DiskBench.Tests.Length; i++)
        {
            Table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            string name = DiskBench.Tests[i].Name;
            Cell(i + 1, 0, name);
            for (int w = 0; w < 2; w++)
            {
                var r = Get(name, w == 1);
                Cell(i + 1, 1 + w * 3, r is null ? "—" : $"{r.MBps:0.0} MB/s");
                Cell(i + 1, 2 + w * 3, r is null ? "—" : $"{r.Iops:0}");
                Cell(i + 1, 3 + w * 3, r is null ? "—" : $"{r.LatencyUs:0.0} µs");
            }
        }
    }
}
