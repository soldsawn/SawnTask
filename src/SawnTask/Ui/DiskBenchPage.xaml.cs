using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
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

    // The live graph has one line per test and direction (plus the preparing write), so each
    // stretch is drawn in its test's colour: a line holds NaN (a gap) while another test runs.
    // Read is the strong shade of a colour, write the light one.
    static readonly Dictionary<string, (Color Read, Color Write)> TestColors = new()
    {
        ["SEQ1M Q8T1"] = (Color.FromRgb(0x1E, 0x88, 0xE5), Color.FromRgb(0x90, 0xCA, 0xF9)),
        ["SEQ1M Q1T1"] = (Color.FromRgb(0x8E, 0x24, 0xAA), Color.FromRgb(0xCE, 0x93, 0xD8)),
        ["RND4K Q32T1"] = (Color.FromRgb(0x43, 0xA0, 0x47), Color.FromRgb(0xA5, 0xD6, 0xA7)),
        ["RND4K Q1T1"] = (Color.FromRgb(0xFB, 0x8C, 0x00), Color.FromRgb(0xFF, 0xCC, 0x80)),
    };
    static readonly Color PrepareColor = Color.FromRgb(0x9E, 0x9E, 0x9E);
    const int GraphSamples = 240;

    readonly Series prepareLine = new(GraphSamples);
    readonly Dictionary<(string Test, bool Write), Series> testLines = [];
    readonly Dictionary<(string Test, bool Write), BenchResult> results = [];
    List<StorageDevices.Disk> disks = [];
    CancellationTokenSource? cancel;
    bool running, loading;
    DiskVerdict? verdict;
    DiskHealth? health;
    string? healthRoot;
    bool healthTried; // the report was asked for (health stays null when the disk would not give it)
    int healthVersion;
    string statusKey = "DB.Idle";
    object?[] statusArgs = [];

    public DiskBenchPage()
    {
        InitializeComponent();
        Graph.AddLine(prepareLine, PrepareColor, 1.6);
        foreach (var test in DiskBench.Tests)
            foreach (bool write in new[] { false, true })
            {
                var line = new Series(GraphSamples);
                testLines[(test.Name, write)] = line;
                var (read, written) = TestColors[test.Name];
                Graph.AddLine(line, write ? written : read, 1.6);
            }
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
        ShowHealth();
        BuildHistory();
        BuildLegend();
    }

    // "Sequential, many requests (SEQ1M Q8T1)": a plain name first, the usual benchmark name after it.
    static string TestKey(string test, string prefix) => prefix + test.Replace(' ', '.');
    static string TestLabel(string test) => Loc.F("DB.TestName", Loc.T(TestKey(test, "DB.T.")), test);

    void BuildLegend()
    {
        GraphScale.Text = Loc.T("DB.GraphScale");
        GraphLegend.Children.Clear();
        void Swatch(Panel row, Color color, string text)
        {
            row.Children.Add(new Border { Width = 14, Height = 3, Background = new SolidColorBrush(color), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) });
            row.Children.Add(new TextBlock { Text = text, Margin = new Thickness(0, 0, 10, 0) });
        }
        StackPanel Group(string? title)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 14, 4) };
            if (title is not null)
            {
                var name = new TextBlock { Text = title, Margin = new Thickness(0, 0, 8, 0) };
                name.SetResourceReference(TextBlock.ForegroundProperty, "Dim"); // follows a theme switch
                row.Children.Add(name);
            }
            GraphLegend.Children.Add(row);
            return row;
        }
        foreach (var test in DiskBench.Tests)
        {
            var row = Group(TestLabel(test.Name));
            row.ToolTip = Loc.T(TestKey(test.Name, "DB.TT."));
            Swatch(row, TestColors[test.Name].Read, Loc.T("DB.Read"));
            Swatch(row, TestColors[test.Name].Write, Loc.T("DB.Write"));
        }
        Swatch(Group(null), PrepareColor, Loc.T("DB.PrepareLine"));
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
        return new BenchOptions(size, passes, passes switch { 1 => 2, 2 or 3 => 3, _ => 5 }, passes >= 5 ? 2 : 1);
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
        prepareLine.Clear();
        foreach (var line in testLines.Values) line.Clear();
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
                if (Get(SeqTest, false) is { } seqRead && Get(SeqTest, true) is { } seqWrite
                    && Get(RndTest, false) is { } rndRead && Get(RndTest, true) is { } rndWrite)
                {
                    BenchHistory.Add(new BenchRecord(DateTime.Now, drive.Root.TrimEnd('\\'), drive.Model, options.FileBytes, options.Passes,
                        seqRead.MBps, seqWrite.MBps, rndRead.MBps, rndWrite.MBps));
                    BuildHistory();
                }
                // For --snapshot: SAWNTASK_BENCH_SCROLL=1 shows the bottom of the page (the history).
                if (Environment.GetEnvironmentVariable("SAWNTASK_BENCH_SCROLL") == "1")
                {
                    PageScroll.UpdateLayout();
                    PageScroll.ScrollToEnd();
                }
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
            _ = LoadHealth(); // the temperature and written total have moved on
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
            var active = p.Phase == "prepare" ? prepareLine : testLines.GetValueOrDefault((p.Test, p.Write)) ?? prepareLine;
            prepareLine.Add(active == prepareLine ? p.LiveMBps : double.NaN);
            foreach (var line in testLines.Values) line.Add(line == active ? p.LiveMBps : double.NaN);
            Graph.Advance(TimeSpan.FromMilliseconds(250));
        }
        if (p.Phase == "prepare")
        {
            SetStatus("DB.Preparing", p.LiveMBps);
            return;
        }
        Progress.Value = ((p.Step - 1) * passes + (p.Pass - 1)) / (double)(p.Steps * passes);
        SetStatus("DB.Running", p.Step, p.Steps, TestLabel(p.Test), Loc.T(p.Write ? "DB.Write" : "DB.Read"), p.Pass, p.Passes, p.LiveMBps);
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

    // ---- earlier tests ----

    void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        BenchHistory.Clear();
        BuildHistory();
    }

    // The last ten tests, newest first. Each number carries an arrow when it differs by 3% or more
    // from the test before it on the same disk.
    void BuildHistory()
    {
        var all = BenchHistory.Load();
        HistoryCard.Visibility = all.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        HistoryGrid.Children.Clear();
        HistoryGrid.RowDefinitions.Clear();
        HistoryGrid.ColumnDefinitions.Clear();
        if (all.Count == 0) return;

        double[] widths = [1.5, 2.6, 1.2, 1.4, 1.4, 1.4, 1.4];
        foreach (double w in widths) HistoryGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(w, GridUnitType.Star) });
        var culture = Appearance.Language == "tr" ? new CultureInfo("tr-TR") : CultureInfo.InvariantCulture;

        TextBlock Cell(int row, int col, bool header = false)
        {
            var block = new TextBlock { Margin = new Thickness(0, 3, 8, 3), TextTrimming = TextTrimming.CharacterEllipsis };
            if (header) block.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
            if (col >= 3) block.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetRow(block, row);
            Grid.SetColumn(block, col);
            HistoryGrid.Children.Add(block);
            return block;
        }

        HistoryGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        string[] headers = ["DB.HCol.Date", "DB.HCol.Disk", "DB.HCol.Size", "DB.SeqRead", "DB.SeqWrite", "DB.RndRead", "DB.RndWrite"];
        for (int c = 0; c < headers.Length; c++) Cell(0, c, header: true).Text = Loc.T(headers[c]);

        for (int i = 0; i < Math.Min(10, all.Count); i++)
        {
            var r = all[i];
            var before = all.Skip(i + 1).FirstOrDefault(x => x.Model == r.Model && x.Drive == r.Drive);
            HistoryGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Cell(i + 1, 0).Text = r.Time.ToString("g", culture);
            Cell(i + 1, 1).Text = $"{r.Drive}  {r.Model}";
            Cell(i + 1, 2).Text = $"{Fmt.Bytes(r.FileBytes)} × {r.Passes}";
            double[] now = [r.SeqRead, r.SeqWrite, r.RndRead, r.RndWrite];
            double[]? old = before is null ? null : [before.SeqRead, before.SeqWrite, before.RndRead, before.RndWrite];
            for (int c = 0; c < 4; c++)
            {
                var cell = Cell(i + 1, 3 + c);
                cell.FontFamily = new FontFamily("Consolas");
                cell.Inlines.Add(new Run(now[c] >= 100 ? $"{now[c]:0}" : $"{now[c]:0.0}"));
                if (old is null || old[c] <= 0) continue;
                double change = (now[c] - old[c]) / old[c] * 100;
                if (Math.Abs(change) < 3) continue;
                cell.Inlines.Add(new Run($"  {(change > 0 ? "▲" : "▼")}{Math.Abs(change):0}%")
                {
                    Foreground = new SolidColorBrush(change > 0 ? Color.FromRgb(0x66, 0xBB, 0x6A) : Color.FromRgb(0xEF, 0x53, 0x50)),
                });
            }
        }
    }

    // ---- disk health ----

    void Drive_Changed(object sender, SelectionChangedEventArgs e) => _ = LoadHealth();

    async Task LoadHealth()
    {
        int version = ++healthVersion;
        string? root = (DriveBox.SelectedItem as ComboBoxItem)?.Tag is DriveEntry d ? d.Root : null;
        if (root != healthRoot) (health, healthTried, healthRoot) = (null, false, root); // another drive: forget the old report
        ShowHealth();
        if ((DriveBox.SelectedItem as ComboBoxItem)?.Tag is not DriveEntry { Disk: { } disk }) return;
        var report = await Task.Run(() => DiskHealthReader.Read(disk.Number, disk.Bus));
        if (version != healthVersion) return; // the selection moved on while reading
        (health, healthTried) = (report, true);
        ShowHealth();
    }

    void ShowHealth()
    {
        bool known = (DriveBox.SelectedItem as ComboBoxItem)?.Tag is DriveEntry { Disk: not null };
        HealthCard.Visibility = known && healthTried ? Visibility.Visible : Visibility.Collapsed;
        HealthGrid.Children.Clear();
        if (!known || !healthTried) return;
        if (health is null)
        {
            HealthTitle.Text = Loc.T("DB.Health");
            HealthTitle.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
            HealthSummary.Text = Loc.T("DB.H.Unavailable");
            return;
        }

        var (level, reasons) = health.Assess();
        HealthTitle.Text = $"{Loc.T("DB.Health")}: {Loc.T("DB.Health." + level)}";
        HealthTitle.Foreground = new SolidColorBrush(level switch
        {
            HealthLevel.Good => Color.FromRgb(0x66, 0xBB, 0x6A),
            HealthLevel.Caution => Color.FromRgb(0xFF, 0xA7, 0x26),
            _ => Color.FromRgb(0xEF, 0x53, 0x50),
        });
        HealthSummary.Text = reasons.Count == 0
            ? Loc.T("DB.H.Ok")
            : string.Join("\n", reasons.Select(r => "• " + Loc.F(r.Key, r.Args)));

        void Item(string labelKey, string? value)
        {
            if (value is null) return;
            var label = new TextBlock { Text = Loc.T(labelKey), FontSize = 12 };
            label.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
            HealthGrid.Children.Add(new StackPanel
            {
                Margin = new Thickness(0, 0, 12, 8),
                Children = { label, new TextBlock { Text = value, FontSize = 16, FontFamily = new FontFamily("Consolas") } },
            });
        }
        static string Amount(double tb) => tb >= 1 ? $"{tb:0.00} TB" : $"{tb * 1000:0} GB";
        var h = health;
        Item("DB.HL.Temp", h.TemperatureC is int t ? $"{t} °C" : null);
        Item("DB.HL.Used", h.PercentUsed is int used ? $"{used}%" : null);
        Item("DB.HL.Spare", h.SpareLeftPercent is int spare ? $"{spare}%" : null);
        Item("DB.HL.Written", h.TerabytesWritten is double w ? Amount(w) : null);
        Item("DB.HL.Read", h.TerabytesRead is double r ? Amount(r) : null);
        Item("DB.HL.Hours", h.PowerOnHours is long hours ? Loc.F("DB.HL.Days", hours, hours / 24.0) : null);
        Item("DB.HL.Cycles", h.PowerCycles is long cycles ? $"{cycles:N0}" : null);
        Item("DB.HL.Unsafe", h.UnsafeShutdowns is long unsafeShutdowns ? $"{unsafeShutdowns:N0}" : null);
        Item("DB.HL.Media", h.MediaErrors is long media ? $"{media:N0}" : null);
        Item("DB.HL.Realloc", h.ReallocatedSectors is long realloc ? $"{realloc:N0}" : null);
        Item("DB.HL.Pending", h.PendingSectors is long pending ? $"{pending:N0}" : null);
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
            Table.ColumnDefinitions.Add(new ColumnDefinition { Width = c == 0 ? new GridLength(2.2, GridUnitType.Star) : new GridLength(1, GridUnitType.Star) });

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
            Cell(i + 1, 0, TestLabel(name));
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
