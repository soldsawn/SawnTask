using System.IO;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Microsoft.Win32;
using SawnTask.Core;
using SawnTask.Native;
using SawnTask.Providers;
using SawnTask.Ui;

namespace SawnTask;

public partial class MainWindow : Window
{
    const int HistoryLength = 600; // samples kept by the main graphs (about 5 minutes at 0.5 s)
    const int CoreHistoryLength = 120; // the per-core graphs stay linear and short
    static readonly TimeSpan ExitedRowLifetime = TimeSpan.FromSeconds(10); // how long an exited process stays listed

    // Page indexes (TabItem order in MainWindow.xaml; the menu items carry them as Tag).
    const int PagePerformance = 1, PageUsers = 3, PageConnections = 4, PageSources = 10, PageSettings = 11, PageSystemInfo = 12, PagePower = 13;
    public const int PageDisk = 9;

    // Text keys of the pages that can be the start page, in page order.
    static readonly string[] StartPageKeys =
    [
        "Nav.Summary", "Nav.Performance", "Nav.Processes", "Nav.Users", "Nav.Connections", "Nav.Startup",
        "Nav.Services", "Nav.Installed", "Nav.Drivers", "Nav.Disk", "Nav.Sources",
    ];

    static readonly Color CpuColor = Color.FromRgb(0x66, 0xBB, 0x6A);
    static readonly Color TempColor = Color.FromRgb(0xFF, 0xA7, 0x26);
    static readonly Color KernelColor = Color.FromRgb(0xEF, 0x53, 0x50);

    readonly Settings settings = Settings.Load();
    readonly History history = new(HistoryLength, CoreHistoryLength);
    readonly ProcessHistory processHistory = new(HistoryLength);
    readonly Recorder recorder = new();
    readonly Collector collector = new();
    readonly ObservableCollection<ProcessRow> processes = [];
    readonly Dictionary<ProcessKey, ProcessRow> processIndex = [];
    readonly ListCollectionView processView;
    readonly ObservableCollection<ConnectionItem> connections = [];
    readonly ObservableCollection<SourceRow> sources = [];
    readonly ListCollectionView connectionView;
    LineGraph[] SummaryGraphs => [CpuGraph, MemGraph, NetGraph, DiskGraph, GpuGraph, TempGraph, PowerGraph];

    IReadOnlyList<StartupItem>? shownStartup;
    IReadOnlyList<ServiceItem>? shownServices;
    IReadOnlyList<InstalledApp>? shownApps;
    IReadOnlyList<DriverItem>? shownDrivers;
    Snapshot? lastSnapshot;
    SampleFrame? lastFrame;
    DateTime recordStartedUtc;
    bool replaying;
    bool loadingSettings;

    public MainWindow()
    {
        InitializeComponent();

        foreach (var g in SummaryGraphs) g.Compress = true;
        Perf.Bind(history);
        TopScroll.SizeChanged += (_, e) =>
        {
            if (e.HeightChanged && lastFrame is not null && !HistoryActive) TopList.ItemsSource = lastFrame.Top.Take(TopRowsFit()).ToList();
        };
        // Text that changes twice a second is kept as a bitmap, so the 60 fps graphs beside it do not make the renderer redraw it.
        foreach (UIElement still in new UIElement[] { Nav, TopList, SensorList }) still.CacheMode = new BitmapCache();
        CpuGraph.GridRows = 10;
        MemGraph.GridRows = DiskGraph.GridRows = GpuGraph.GridRows = NetGraph.GridRows = 6;
        CpuGraph.Selectable = true;
        CpuGraph.Cursor = System.Windows.Input.Cursors.Cross;
        CpuGraph.SelectionChanged += CpuGraph_SelectionChanged;
        CpuGraph.HoverChanged += CpuGraph_SelectionChanged;
        historyTimer.Tick += (_, _) => { historyTimer.Stop(); RefreshHistoryList(); };
        CpuGraph.AddLine(history.Cpu, CpuColor);
        CpuGraph.AddLine(history.CpuKernel, KernelColor, 1);
        CpuGraph.AddLine(history.CpuTemp, TempColor, 1.5, ownMax: 110); // the temperature scale tops out at 110 °C
        CpuGraph.LeftAxis = (CpuColor, ["100%", "50%", "0%"]);
        CpuGraph.RightAxis = (TempColor, ["110°", "55°", "0°"]);
        MemGraph.AddLine(history.Mem, Color.FromRgb(0xCE, 0x93, 0xD8));
        PowerGraph.AutoScale = true;
        PowerGraph.MinAutoMax = 20; // watts
        PowerGraph.GridRows = 6;
        PowerGraph.AddLine(history.CpuPower, Color.FromRgb(0xEF, 0x53, 0x50));
        NetGraph.AutoScale = true;
        NetGraph.AddLine(history.NetRx, Color.FromRgb(0x29, 0xB6, 0xF6));
        NetGraph.AddLine(history.NetTx, Color.FromRgb(0xFF, 0xA7, 0x26), 1);
        DiskGraph.AddLine(history.DiskActive, Color.FromRgb(0x9C, 0xCC, 0x65));
        GpuGraph.AddLine(history.Gpu, Color.FromRgb(0x42, 0xA5, 0xF5));
        TempGraph.Max = 110; // the same 110 °C temperature scale
        TempGraph.AddLine(history.CpuTemp, TempColor);

        processView = (ListCollectionView)CollectionViewSource.GetDefaultView(processes);
        // "Apps" and "Background processes" groups, regrouped live when a process opens or
        // closes its window. The first sort key is the group, so Apps always come first;
        // ProcGrid_Sorting keeps it first when the user sorts by a column.
        SortProcesses(nameof(ProcessRow.CpuSort), ListSortDirection.Descending);
        processView.IsLiveSorting = true;
        processView.Filter = FilterProcess;
        processView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ProcessRow.Group)));
        processView.IsLiveGrouping = true;
        processView.LiveGroupingProperties.Add(nameof(ProcessRow.Group));
        ProcGrid.ItemsSource = processView;

        connectionView = (ListCollectionView)CollectionViewSource.GetDefaultView(connections);
        connectionView.Filter = FilterConnection;
        ConnGrid.ItemsSource = connectionView;

        foreach (var (textKey, value) in ProcessActions.Priorities)
        {
            var item = new MenuItem { Tag = value };
            item.SetResourceReference(HeaderedItemsControl.HeaderProperty, textKey); // follows the language
            item.Click += Priority_Click;
            PriorityMenu.Items.Add(item);
        }

        // Settings page: show the current choices (the handlers ignore changes while loading).
        loadingSettings = true;
        LanguageBox.SelectedItem = LanguageBox.Items.Cast<ComboBoxItem>().First(i => (string)i.Tag == Appearance.Language);
        ThemeBox.SelectedItem = ThemeBox.Items.Cast<ComboBoxItem>().First(i => (string)i.Tag == Appearance.Theme);
        int speedIndex = Math.Max(0, SpeedBox.Items.Cast<ComboBoxItem>().ToList().FindIndex(i => int.Parse((string)i.Tag) == settings.UpdateSpeedMs));
        SettingsSpeedBox.SelectedIndex = speedIndex;
        FpsBox.SelectedItem = FpsBox.Items.Cast<ComboBoxItem>().FirstOrDefault(i => int.Parse((string)i.Tag) == settings.GraphFps) ?? FpsBox.Items[2];
        SmoothBox.IsChecked = settings.SmoothCurves;
        LineGraph.Fps = settings.GraphFps;
        LineGraph.SmoothCurves = settings.SmoothCurves;
        // Start page choices: every page except Settings itself, named like the menu.
        foreach (string key in StartPageKeys)
        {
            var choice = new ComboBoxItem();
            choice.SetResourceReference(ContentProperty, key);
            StartPageBox.Items.Add(choice);
        }
        StartPageBox.SelectedIndex = Math.Clamp(settings.StartPage, 0, StartPageKeys.Length - 1);
        SetMoreOpen(settings.NavMoreOpen);
        Page = StartPageBox.SelectedIndex;
        loadingSettings = false;
        SpeedBox.SelectedIndex = speedIndex;
        SettingsAdminBtn.Visibility = Elevation.IsElevated ? Visibility.Collapsed : Visibility.Visible;
        Appearance.Changed += Relocalize;
        _ = SystemInfo.Load(); // read the machine's facts in the background, ready for the System Info page
        Appearance.Changed += ApplyTitleBar;
        SourceInitialized += (_, _) => ApplyTitleBar();
        Relocalize();

        // The collector runs on its own thread; WPF controls may only be touched
        // on the UI thread, so each snapshot is handed over with BeginInvoke.
        collector.SnapshotReady += s => Dispatcher.BeginInvoke(() => OnSnapshot(s));
        collector.Start();
    }

    void OnSnapshot(Snapshot s)
    {
        lastSnapshot = s;
        var frame = SampleFrame.From(s);
        if (recorder.IsRecording)
        {
            recorder.Write(frame);
            RecordTime.Text = (DateTime.UtcNow - recordStartedUtc).ToString(@"hh\:mm\:ss");
        }

        UpdateProcesses(s);
        UpdateSlowLists(s);
        UpdateStatusBar(s);

        // Pages that are not visible are not rebuilt: no work for nobody to see.
        switch (Page)
        {
            case PageUsers: UpdateUsers(s); break;
            case PageConnections: UpdateConnections(s); break;
            case PageSources: UpdateSources(s); break;
            case PageSystemInfo: SysInfo.Update(s); break;
        }

        if (replaying) return; // the graphs are showing a recording
        history.Append(frame);
        processHistory.Add(history.Cpu.Total - 1, s);
        ShowFrame(frame, newSample: true);
        if (Page == PagePerformance) Perf.Update(s, frame, true);
        if (HistoryActive) RefreshHistoryList();
    }

    // The Top list shows as many processes as fit in its card: a taller window shows more.
    const double TopRowHeight = 19.5;
    int TopRowsFit() => Math.Max(3, (int)(TopScroll.ActualHeight / TopRowHeight));

    // newSample: a live sample just arrived, so the graphs start their slide (60 fps motion).
    void ShowFrame(SampleFrame f, bool newSample = false)
    {
        lastFrame = f;
        CpuValue.Text = Fmt.Pct(f.Cpu);
        if (!HistoryActive) CpuDetail.Text = Loc.F("Sum.Kernel", Fmt.Pct(f.CpuKernel)) + "   ·   " + Loc.F("Sum.LogicalCount", f.Cores?.Length ?? 0)
                         + (f.CpuSpeed is double mhz0 ? "   ·   " + Loc.F("Sum.Speed", mhz0 / 1000) : "")
                         + (f.CpuPower is double w ? "   ·   " + Loc.F("Sum.Package", w) : "")
                         + (f.CpuTemp is double t ? $"   ·   {t:0} °C" : "")
                         + "   ·   " + Loc.F("Sum.Breaks", history.GraphBreaks);
        PowerValue.Text = f.CpuPower is double pw ? $"{pw:0.0} W" : Fmt.Missing;
        PowerDetail.Text = Loc.T("Sum.CpuPackage");
        MemValue.Text = Fmt.Pct(f.Mem);
        MemDetail.Text = Loc.F("Sum.MemDetail", Fmt.Bytes(f.MemUsed), Fmt.Bytes(f.MemTotal));
        NetValue.Text = Fmt.Rate(f.NetRx);
        NetDetail.Text = Loc.F("Sum.NetDetail", Fmt.Rate(f.NetRx), Fmt.Rate(f.NetTx), Fmt.Rate(NetGraph.CurrentMax));
        DiskValue.Text = Fmt.Pct(f.DiskActive);
        DiskDetail.Text = Loc.F("Sum.DiskDetail", Fmt.Rate(f.DiskRead), Fmt.Rate(f.DiskWrite));
        GpuValue.Text = Fmt.Pct(f.Gpu);
        GpuDetail.Text = f.Gpus.FirstOrDefault() is GpuDevice g
            ? string.Join(" · ", new[]
            {
                g.Name.Replace("NVIDIA GeForce ", ""),
                g.TempC is double gt ? $"{gt:0} °C" : null,
                g.PowerW is double gw ? $"{gw:0.0} W" : null,
                g.CoreMHz is double mhz ? $"{mhz:0} MHz" : null,
                g.VramUsed is ulong used && g.VramTotal is ulong total ? $"{Fmt.Bytes(used)} / {Fmt.Bytes(total)}" : null,
            }.Where(x => x is not null))
            : Loc.T("Sum.GpuFallback");
        if (!HistoryActive)
        {
            SmoothTopHeat(f.Top);
            TopList.ItemsSource = f.Top.Take(TopRowsFit()).ToList();
        }

        TempValue.Text = f.CpuTemp is double ct ? $"{ct:0} °C" : Fmt.Missing;
        TempValue.ToolTip = Loc.T("Sum.TempTip");
        SensorList.ItemsSource = f.Thermals.Select(x => new SensorRow(x.Label, $"{x.Celsius:0.0} °C", x.Source))
            .Concat(f.Gpus.Where(x => x.TempC is not null).Select(x => new SensorRow(x.Name.Replace("NVIDIA GeForce ", ""), $"{x.TempC:0} °C", "NVML")))
            .Concat(f.Power.Select(p => new SensorRow(Loc.F("Sum.Power", p.Label.Replace("NVIDIA GeForce ", "")), $"{p.Watts:0.0} W", p.Source)))
            .ToList();

        foreach (var graph in SummaryGraphs)
        {
            if (newSample) graph.Advance(TimeSpan.FromMilliseconds(f.IntervalMs));
            else graph.InvalidateVisual();
        }
    }

    void UpdateProcesses(Snapshot s)
    {
        var seen = new HashSet<ProcessKey>();
        foreach (var p in s.Processes)
        {
            seen.Add(p.Key);
            if (!processIndex.TryGetValue(p.Key, out var row))
            {
                row = new ProcessRow(p.Key);
                processIndex[p.Key] = row;
                processes.Add(row);
            }
            row.Update(p);
        }

        // Gone processes first turn red ("Exited") and disappear 10 seconds later.
        var now = DateTime.UtcNow;
        for (int i = processes.Count - 1; i >= 0; i--)
        {
            var row = processes[i];
            if (seen.Contains(row.Key)) continue;
            row.MarkExited();
            if (now - row.ExitedUtc > ExitedRowLifetime)
            {
                processIndex.Remove(row.Key);
                processes.RemoveAt(i);
            }
        }
    }

    // Slow-tier lists only change every few seconds. Rebinding only on real change
    // keeps the selection and scroll position stable.
    void UpdateSlowLists(Snapshot s)
    {
        Rebind(StartupGrid, s.Startup, ref shownStartup);
        Rebind(ServicesGrid, s.Services, ref shownServices);
        Rebind(AppsGrid, s.InstalledApps, ref shownApps);
        Rebind(DriversGrid, s.Drivers, ref shownDrivers);
    }

    static void Rebind<T>(DataGrid grid, IReadOnlyList<T> items, ref IReadOnlyList<T>? shown)
    {
        if (ReferenceEquals(items, shown)) return;
        if (shown is null || !items.SequenceEqual(shown)) grid.ItemsSource = items;
        shown = items;
    }

    // The rows stay; only the cells that changed are redrawn.
    void UpdateSources(Snapshot s)
    {
        if (SourcesGrid.ItemsSource is null) SourcesGrid.ItemsSource = sources;
        for (int i = 0; i < s.Sources.Count; i++)
        {
            if (i == sources.Count || sources[i].Id != s.Sources[i].Id)
            {
                if (i < sources.Count) sources[i] = new SourceRow(s.Sources[i].Id);
                else sources.Add(new SourceRow(s.Sources[i].Id));
            }
            sources[i].Update(s.Sources[i]);
        }
        while (sources.Count > s.Sources.Count) sources.RemoveAt(sources.Count - 1);
    }

    void UpdateUsers(Snapshot s)
    {
        var rows = s.Sessions.Select(u =>
        {
            var mine = s.Processes.Where(p => p.SessionId == u.SessionId).ToList();
            return new UserRow(u.SessionId, u.User, u.State, u.Station, u.Client,
                mine.Sum(p => p.CpuPercent ?? 0), mine.Sum(p => p.PrivateBytes), mine.Count);
        }).ToList();
        uint? selected = (UsersGrid.SelectedItem as UserRow)?.SessionId;
        UsersGrid.ItemsSource = rows;
        UsersGrid.SelectedItem = rows.FirstOrDefault(r => r.SessionId == selected) ?? rows.FirstOrDefault();
    }

    void UsersGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (UsersGrid.SelectedItem is not UserRow user) return;
        var list = processes.Where(r => r.Session == user.SessionId).OrderByDescending(r => r.CpuSort).ToList();
        if (UserProcGrid.ItemsSource is not List<ProcessRow> old || !old.SequenceEqual(list))
            UserProcGrid.ItemsSource = list;
    }

    void UpdateConnections(Snapshot s)
    {
        // Records compare by value: unchanged sockets stay, so the selection survives.
        var fresh = s.Connections.ToHashSet();
        for (int i = connections.Count - 1; i >= 0; i--)
            if (!fresh.Contains(connections[i])) connections.RemoveAt(i);
        var existing = connections.ToHashSet();
        foreach (var c in s.Connections)
            if (!existing.Contains(c)) connections.Add(c);

        int established = s.Connections.Count(c => c.State == "Established");
        int listening = s.Connections.Count(c => c.State == "Listening");
        int internet = s.Connections.Count(c => c.Exposure == "Internet");
        int apps = s.Connections.Select(c => c.Pid).Distinct().Count();
        ConnSummary.Text = Loc.F("Conn.Summary", established, listening, internet, apps, s.Connections.Count);
    }

    bool FilterConnection(object item)
    {
        var c = (ConnectionItem)item;
        return ConnFilter.SelectedIndex switch
        {
            1 => c.State == "Established",
            2 => c.State == "Listening",
            3 => c.Exposure == "Internet",
            4 => c.Protocol.StartsWith("UDP", StringComparison.Ordinal),
            _ => true,
        };
    }

    void ConnFilter_SelectionChanged(object sender, SelectionChangedEventArgs e) => connectionView?.Refresh();

    void UpdateStatusBar(Snapshot s)
    {
        int ok = s.Sources.Count(x => x.Health == SourceHealth.Ok);
        int expected = s.Sources.Count(x => x.Health != SourceHealth.Unavailable);
        bool anyFailed = s.Sources.Any(x => x.Health == SourceHealth.Failed);
        StatusDot.Fill = new SolidColorBrush(anyFailed ? Colors.IndianRed : ok == expected ? Color.FromRgb(0x4C, 0xAF, 0x50) : Colors.Orange);
        StatusText.Text = Loc.F("Bar.Status", ok, expected, s.Processes.Count, s.Threads, s.Generation, s.ActualMs);
    }

    bool FilterProcess(object item)
    {
        string text = FilterBox.Text.Trim();
        if (text.Length == 0) return true;
        var row = (ProcessRow)item;
        return row.Name.Contains(text, StringComparison.OrdinalIgnoreCase)
               || row.User.Contains(text, StringComparison.OrdinalIgnoreCase)
               || row.Pid.ToString() == text;
    }

    // Row tint of the top list: CPU use relative to the busiest row (at least 5 %, so a quiet
    // system is not all bright). Each process eases towards its target over a few updates,
    // so rows fade instead of flashing; processes that leave the list are forgotten.
    readonly Dictionary<uint, double> topHeat = [];

    void SmoothTopHeat(List<TopProcess> top)
    {
        double max = Math.Max(5, top.Count == 0 ? 0 : top.Max(t => t.Cpu));
        var next = new Dictionary<uint, double>();
        foreach (var t in top)
        {
            double prev = topHeat.GetValueOrDefault(t.Pid);
            t.Heat = next[t.Pid] = prev + 0.4 * (t.Cpu / max - prev);
        }
        topHeat.Clear();
        foreach (var (pid, heat) in next) topHeat[pid] = heat;
    }

    // A row of the Summary page's top list: open the Processes page with that process selected.
    // An active search that hides the process is cleared first.
    void TopRow_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not TopProcess top) return;
        var row = processes.FirstOrDefault(r => r.Pid == top.Pid && !r.IsExited);
        if (row is null) return;
        Page = 2;
        if (!processView.Contains(row)) FilterBox.Clear(); // TextChanged refreshes the view
        ProcGrid.SelectedItem = row;
        ProcGrid.UpdateLayout();
        ProcGrid.ScrollIntoView(row);
        ProcGrid.Focus();
    }

    // ---- CPU history (on the Summary page) ----
    //
    // There is no separate mode: drag on the CPU graph to pick a period and the top list beside
    // it turns into "Average CPU use" for that period. Clicking the graph clears the period and
    // the list is the live top list again.

    // Hovering shows one moment, a click pins it, a drag picks a period (then the list is averaged).
    bool HistoryActive => CpuGraph.Selection is not null || CpuGraph.Hover is not null;
    bool PeriodPicked => CpuGraph.Selection is { } s && s.To > s.From;

    readonly System.Windows.Threading.DispatcherTimer historyTimer = new() { Interval = TimeSpan.FromMilliseconds(120) };

    // Dragging fires many selection changes per second; rebuilding the list for each one makes
    // the drag stutter, so the list is refreshed at most every 120 ms while the graph itself
    // follows the mouse immediately.
    void CpuGraph_SelectionChanged()
    {
        if (!HistoryActive)
        {
            historyTimer.Stop();
            ApplyHistoryTitles();
            if (lastFrame is not null) ShowFrame(lastFrame);
            TopScroll.ScrollToTop();
            return;
        }
        ApplyHistoryTitles();
        if (!historyTimer.IsEnabled) historyTimer.Start();
    }

    void ApplyHistoryTitles()
    {
        CpuTitle.SetResourceReference(TextBlock.TextProperty, HistoryActive ? "Hist.Title" : "Sum.Cpu");
        TopTitle.SetResourceReference(TextBlock.TextProperty, PeriodPicked ? "Hist.Avg" : "Sum.TopProcesses");
        TopHeader.Margin = new Thickness(0, 0, HistoryActive ? 14 : 0, 4); // room for the list's scroll bar
    }

    // Runs every update while a period is picked. The scroll position is kept.
    void RefreshHistoryList()
    {
        long total = history.Cpu.Total;
        var range = CpuGraph.Selection ?? (CpuGraph.Hover is long h ? (h, h) : ((long, long)?)null);
        if (range is not { } sel || lastFrame is null) return;
        if (sel.To < total - history.Cpu.Count) // the whole period has scrolled out of the graph
        {
            CpuGraph.Selection = null;
            CpuGraph_SelectionChanged();
            return;
        }

        var rows = processHistory.Average(sel.From, sel.To, lastSnapshot?.Processes ?? [], 60, out int samples);
        double max = Math.Max(1, rows.Count == 0 ? 0 : rows.Max(r => r.Cpu));
        foreach (var r in rows) r.Heat = r.Cpu / max;
        double offset = TopScroll.VerticalOffset;
        TopList.ItemsSource = rows;
        if (offset > 0)
        {
            TopScroll.UpdateLayout();
            TopScroll.ScrollToVerticalOffset(offset);
        }

        string span = Duration(samples * lastFrame.IntervalMs / 1000.0);
        string moment = sel.To > sel.From
            ? Loc.F("Hist.Selected", span, samples)
            : Loc.F("Hist.Moment", Duration((total - 1 - sel.From) * lastFrame.IntervalMs / 1000.0));
        CpuDetail.Text = Loc.F("Sum.LogicalCount", lastFrame.Cores?.Length ?? 0) + "   ·   " + moment;
    }

    static string Duration(double seconds) =>
        seconds >= 60 ? $"{(int)seconds / 60}:{(int)seconds % 60:00}" : $"{seconds:0} s";

    void FilterBox_TextChanged(object sender, TextChangedEventArgs e) => processView.Refresh();

    // Column clicks are handled here instead of by the DataGrid, so the group stays the first
    // sort key (Apps above Background processes). First click sorts big-to-small, like Task Manager.
    void ProcGrid_Sorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        var direction = e.Column.SortDirection == ListSortDirection.Descending ? ListSortDirection.Ascending : ListSortDirection.Descending;
        foreach (var column in ProcGrid.Columns) column.SortDirection = null;
        e.Column.SortDirection = direction;
        SortProcesses(e.Column.SortMemberPath, direction);
    }

    void SortProcesses(string property, ListSortDirection direction)
    {
        using (processView.DeferRefresh())
        {
            processView.SortDescriptions.Clear();
            processView.SortDescriptions.Add(new SortDescription(nameof(ProcessRow.GroupOrder), ListSortDirection.Ascending));
            processView.SortDescriptions.Add(new SortDescription(property, direction));
        }
        // Live sorting re-sorts rows when these properties change on later ticks.
        processView.LiveSortingProperties.Clear();
        processView.LiveSortingProperties.Add(nameof(ProcessRow.GroupOrder));
        processView.LiveSortingProperties.Add(property);
    }

    // ---- process actions ----

    ProcessRow? SelectedProcess() => ProcGrid.SelectedItem as ProcessRow is { IsExited: false } row ? row : null;

    void ProcGrid_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (SelectedProcess() is not ProcessRow row)
        {
            e.Handled = true; // nothing (or an exited row) selected
            return;
        }
        MarkCurrentPriority(row);
    }

    // Ticks the priority the process has right now (none when Windows hides it from us).
    public void MarkCurrentPriority(ProcessRow row)
    {
        uint? current = ProcessActions.GetPriority(row.Key);
        foreach (MenuItem item in PriorityMenu.Items) item.IsChecked = current is uint c && (uint)item.Tag == c;
    }

    // Error texts come from Windows itself (already in the Windows display language).
    void Report(bool ok, string error)
    {
        if (!ok) MessageBox.Show(this, error, Loc.T("Msg.ActionFailed"), MessageBoxButton.OK, MessageBoxImage.Error);
    }

    void EndTask_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedProcess() is not ProcessRow row) return;
        var answer = MessageBox.Show(this, Loc.F("Msg.EndTask", row.Name, row.Pid), Loc.T("Proc.EndTask"),
            MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.OK) return;
        Report(ProcessActions.TryEndTask(row.Key, out string error), error);
    }

    void Suspend_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedProcess() is ProcessRow row) Report(ProcessActions.TrySuspend(row.Key, true, out string error), error);
    }

    void Resume_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedProcess() is ProcessRow row) Report(ProcessActions.TrySuspend(row.Key, false, out string error), error);
    }

    void Priority_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedProcess() is not ProcessRow row || sender is not MenuItem { Tag: uint priority }) return;
        Report(ProcessActions.TrySetPriority(row.Key, priority, out string error), error);
    }

    void OpenLocation_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedProcess() is ProcessRow row) Report(ProcessActions.TryOpenFileLocation(row.Key, out string error), error);
    }

    void ToggleStartup_Click(object sender, RoutedEventArgs e)
    {
        if (StartupGrid.SelectedItem is not StartupItem item) return;
        try
        {
            StartupProvider.SetEnabled(item, !item.Enabled);
            collector.InvalidateStartup(); // re-read on the next tick
        }
        catch (UnauthorizedAccessException)
        {
            MessageBox.Show(this, Loc.F("Startup.NeedsAdmin", item.Name), Loc.T("Startup.Failed"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Loc.T("Startup.Failed"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ---- navigation ----

    // The page on screen. Setting it selects the matching menu item, unfolding "More" if
    // the page is inside it.
    public int Page
    {
        get => Pages.SelectedIndex;
        set
        {
            if (value == SettingsPage)
            {
                Nav.SelectedItem = null;
                Pages.SelectedIndex = SettingsPage;
                UpdateNavButtons();
                return;
            }
            var item = Nav.Items.Cast<ListBoxItem>().FirstOrDefault(i => i != NavMore && int.Parse((string)i.Tag) == value);
            if (item is null)
            return;
            if (item.Visibility != Visibility.Visible) SetMoreOpen(true);
            Nav.SelectedItem = item;
        }
    }

    // Settings is a button at the bottom of the menu, not a list item.
    const int SettingsPage = 11;

    void Nav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Nav.SelectedItem is ListBoxItem item && item != NavMore && item.Tag is string tag) Pages.SelectedIndex = int.Parse(tag);
        if (Pages.SelectedIndex == PageSystemInfo) SysInfo.Show();
        if (Pages.SelectedIndex == PagePower) PowerPage.Show();
        if (Pages.SelectedIndex == PagePerformance)
        {
            Perf.Show();
            if (lastSnapshot is not null && lastFrame is not null) Perf.Update(lastSnapshot, lastFrame, false);
        }
        UpdateNavButtons();
    }

    void NavSettings_Click(object sender, RoutedEventArgs e) => Page = SettingsPage;

    void NavTheme_Click(object sender, RoutedEventArgs e)
    {
        ThemeMenu.PlacementTarget = NavTheme;
        ThemeMenu.IsOpen = true;
    }

    // The menu goes through the Settings page's combo box, so both always agree and the
    // choice is saved by the same handler.
    void ThemeMenu_Click(object sender, RoutedEventArgs e) =>
        ThemeBox.SelectedItem = ThemeBox.Items.Cast<ComboBoxItem>().First(i => (string)i.Tag == (string)((MenuItem)sender).Tag);

    void UpdateNavButtons() => NavSettings.Tag = Pages.SelectedIndex == SettingsPage ? "sel" : null;

    void NavMore_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        e.Handled = true; // the divider itself is never selected
        SetMoreOpen(!settings.NavMoreOpen);
    }

    // Shows or hides the menu items below the "More" divider.
    void SetMoreOpen(bool open)
    {
        settings.NavMoreOpen = open;
        NavMore.Tag = open ? "open" : null; // turns the chevron (Styles.xaml, NavMore)
        int first = Nav.Items.IndexOf(NavMore) + 1, end = Nav.Items.Count;
        for (int i = first; i < end; i++)
            ((ListBoxItem)Nav.Items[i]).Visibility = open ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---- settings ----

    // Title bar: dark/light frame plus the side menu's colour, so the window reads as one piece.
    // Windows 10 only honours the dark flag; the colour calls fail harmlessly there.
    void ApplyTitleBar()
    {
        IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        static int Colorref(string key) =>
            Application.Current.TryFindResource(key) is SolidColorBrush { Color: var c } ? c.R | c.G << 8 | c.B << 16 : 0;
        int dark = Appearance.Theme == "Dark" ? 1 : 0;
        int caption = Colorref("Side"), text = Colorref("Fg");
        NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
        NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_CAPTION_COLOR, ref caption, sizeof(int));
        NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_TEXT_COLOR, ref text, sizeof(int));
    }

    // Runs at start-up and after every theme or language switch: everything built in code
    // (rather than bound with {DynamicResource}) is redrawn with the new text and colours.
    void Relocalize()
    {
        Title = Loc.T(Elevation.IsElevated ? "App.TitleAdmin" : "App.Title");
        AdminText.Text = Loc.T(Elevation.IsElevated ? "Set.AdminYes" : "Set.AdminNo");
        RecordBtn.SetResourceReference(ContentProperty, recorder.IsRecording ? "Bar.Stop" : "Bar.Record");
        foreach (var row in processes) row.Relocalize();
        DiskPage.Relocalize();
        Perf.Relocalize();
        SysInfo.Relocalize();
        PowerPage.Relocalize();
        CpuGraph.ToolTip = Loc.T("Hist.Hint");
        if (HistoryActive) RefreshHistoryList();
        if (lastFrame is not null && !replaying) ShowFrame(lastFrame);
        if (lastSnapshot is not null) UpdateStatusBar(lastSnapshot);
        foreach (var graph in SummaryGraphs)
            graph.InvalidateVisual();
    }

    void Appearance_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (loadingSettings || LanguageBox.SelectedItem is not ComboBoxItem lang || ThemeBox.SelectedItem is not ComboBoxItem theme) return;
        settings.Language = (string)lang.Tag;
        settings.Theme = (string)theme.Tag;
        settings.Save();
        Appearance.Apply(settings.Theme, settings.Language); // raises Changed -> Relocalize
    }

    void Graphs_Changed(object sender, RoutedEventArgs e)
    {
        if (loadingSettings || FpsBox.SelectedItem is not ComboBoxItem fps) return;
        settings.GraphFps = int.Parse((string)fps.Tag);
        settings.SmoothCurves = SmoothBox.IsChecked == true;
        settings.Save();
        LineGraph.Fps = settings.GraphFps;
        LineGraph.SmoothCurves = settings.SmoothCurves;
        Relocalize(); // redraws every graph with the new style
    }

    void StartPage_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (loadingSettings || StartPageBox.SelectedIndex < 0) return;
        settings.StartPage = StartPageBox.SelectedIndex;
        settings.Save();
    }

    // The status bar and the Settings page both have an update-speed box; keep them in step.
    void SettingsSpeed_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!loadingSettings && SpeedBox.SelectedIndex != SettingsSpeedBox.SelectedIndex) SpeedBox.SelectedIndex = SettingsSpeedBox.SelectedIndex;
    }

    void SettingsAdmin_Click(object sender, RoutedEventArgs e)
    {
        if (Elevation.Restart("", out bool declined, out string error)) Application.Current.Shutdown();
        else MessageBox.Show(this, declined ? Loc.T("Disk.ElevateCancelled") : error, Loc.T("Set.Admin"), MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // ---- update speed, recording, replay ----

    void SpeedBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SpeedBox.SelectedItem is not ComboBoxItem item) return;
        int ms = int.Parse((string)item.Tag);
        collector.Paused = ms == 0;
        if (ms > 0)
        {
            collector.IntervalMs = ms;
            settings.UpdateSpeedMs = ms;
        }
        if (SettingsSpeedBox.SelectedIndex != SpeedBox.SelectedIndex) SettingsSpeedBox.SelectedIndex = SpeedBox.SelectedIndex;
    }

    void Record_Click(object sender, RoutedEventArgs e)
    {
        if (recorder.IsRecording)
        {
            recorder.Stop();
            RecordBtn.SetResourceReference(ContentProperty, "Bar.Record");
            MessageBox.Show(this, Loc.F("Rec.Saved", recorder.FrameCount, recorder.CurrentPath), Loc.T("Rec.SavedTitle"));
            return;
        }
        recorder.Start();
        recordStartedUtc = DateTime.UtcNow;
        RecordBtn.SetResourceReference(ContentProperty, "Bar.Stop");
    }

    void OpenRecording_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(Recorder.Folder);
        var dialog = new OpenFileDialog
        {
            InitialDirectory = Recorder.Folder,
            Filter = $"{Loc.T("Rec.Filter")} (*{Recorder.Extension})|*{Recorder.Extension}",
        };
        if (dialog.ShowDialog(this) != true) return;

        List<SampleFrame> frames;
        try
        {
            frames = Recorder.Load(dialog.FileName);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Loc.T("Rec.OpenFailed"), MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        if (frames.Count == 0) return;

        replaying = true;
        history.Clear();
        processHistory.Clear();
        foreach (var frame in frames) history.Append(frame);
        ShowFrame(frames[^1]);
        ReplayText.Text = Loc.F("Sum.Replay", Path.GetFileName(dialog.FileName), frames.Count, history.GraphBreaks, HistoryLength);
        ReplayBanner.Visibility = Visibility.Visible;
        LiveBtn.Visibility = Visibility.Visible;
        Page = 0;
    }

    void BackToLive_Click(object sender, RoutedEventArgs e)
    {
        replaying = false;
        history.Clear();
        processHistory.Clear();
        ReplayBanner.Visibility = Visibility.Collapsed;
        LiveBtn.Visibility = Visibility.Collapsed;
    }

    protected override void OnClosed(EventArgs e)
    {
        Appearance.Changed -= Relocalize;
        Appearance.Changed -= ApplyTitleBar;
        collector.Dispose();
        recorder.Dispose();
        settings.Save();
        base.OnClosed(e);
    }
}
