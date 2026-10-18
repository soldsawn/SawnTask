using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SawnTask.Core;

namespace SawnTask.Ui;

// A row of small lit squares: the utilization bar under the device name on the Performance page.
public sealed class LedBar : FrameworkElement
{
    const int Segments = 50;
    double level;

    // 0..100. Redrawn only when the number of lit squares changes.
    public double Value
    {
        get => level;
        set
        {
            double v = Math.Clamp(value, 0, 100);
            bool same = Lit(v) == Lit(level);
            level = v;
            if (!same) InvalidateVisual();
        }
    }

    static int Lit(double v) => (int)Math.Round(v / 100 * Segments);

    protected override void OnRender(DrawingContext dc)
    {
        var on = (Brush)(TryFindResource("Brand") ?? Brushes.DeepSkyBlue);
        var off = (Brush)(TryFindResource("BarTrack") ?? Brushes.DimGray);
        double step = ActualWidth / Segments, gap = Math.Min(2, step / 4);
        int lit = Lit(level);
        for (int i = 0; i < Segments; i++)
            dc.DrawRectangle(i < lit ? on : off, null, new Rect(i * step, 0, Math.Max(1, step - gap), ActualHeight));
    }
}

// The Performance page: a list of devices with live values on the left, the selected
// device on the right. The CPU view has the per-logical-processor graphs (Expanded) or one
// graph (Combined) and a table of statistics; the other devices show a single graph for now.
public partial class PerformancePage : UserControl
{
    static readonly Color CpuColor = Color.FromRgb(0x66, 0xBB, 0x6A);
    static readonly Color KernelColor = Color.FromRgb(0xEF, 0x53, 0x50);
    static readonly Color TempColor = Color.FromRgb(0xFF, 0xA7, 0x26);

    // One entry per device on the left: its title key, how its value reads, and its graphs.
    sealed record Device(string Key, Func<SampleFrame, string> Sub, Func<SampleFrame, string> Value,
        Action<LineGraph, History> Lines, Action<LineGraph> Setup, TextBlock SubText, LineGraph TileGraph);

    // Statistics of the CPU view, in display order.
    static readonly string[] Stats =
    [
        "Perf.Utilization", "Perf.Speed", "Perf.Processes", "Perf.Threads",
        "Perf.Handles", "Perf.Uptime", "Perf.Shutdown", "Perf.BaseSpeed",
        "Perf.Sockets", "Perf.Cores", "Perf.Logical", "Perf.Virtualization",
        "Perf.Vm", "Perf.L1", "Perf.L2", "Perf.L3",
    ];

    readonly List<Device> devices = [];
    readonly Dictionary<string, TextBlock> statValues = [];
    History? history;
    SystemInfoData? info;
    SampleFrame? last;
    Snapshot? lastSnapshot;
    bool loadingView;
    static readonly TimeSpan VisibleSpan = TimeSpan.FromSeconds(60);
    int window = 120;

    public PerformancePage()
    {
        InitializeComponent();

        foreach (var (key, sub, value, lines, setup) in new (string, Func<SampleFrame, string>, Func<SampleFrame, string>, Action<LineGraph, History>, Action<LineGraph>)[]
        {
            ("Perf.Cpu", f => $"{Fmt.Pct(f.Cpu)}" + (f.CpuSpeed is double mhz ? $"  {mhz / 1000:0.00} GHz" : ""), f => Fmt.Pct(f.Cpu),
                (g, h) => { g.AddLine(h.Cpu, CpuColor); }, _ => { }),
            ("Perf.Memory", f => $"{Fmt.Bytes(f.MemUsed)} ({Fmt.Pct(f.Mem)})", f => Fmt.Pct(f.Mem),
                (g, h) => g.AddLine(h.Mem, Color.FromRgb(0xCE, 0x93, 0xD8)), _ => { }),
            ("Perf.Gpu", f => Fmt.Pct(f.Gpu), f => Fmt.Pct(f.Gpu),
                (g, h) => g.AddLine(h.Gpu, Color.FromRgb(0x42, 0xA5, 0xF5)), _ => { }),
            ("Perf.Disks", f => $"{Fmt.Pct(f.DiskActive)}  R {Fmt.Rate(f.DiskRead)}", f => Fmt.Pct(f.DiskActive),
                (g, h) => g.AddLine(h.DiskActive, Color.FromRgb(0x9C, 0xCC, 0x65)), _ => { }),
            ("Perf.Network", f => $"↓ {Fmt.Rate(f.NetRx)}  ↑ {Fmt.Rate(f.NetTx)}", f => Fmt.Rate(f.NetRx),
                (g, h) => { g.AddLine(h.NetRx, Color.FromRgb(0x29, 0xB6, 0xF6)); g.AddLine(h.NetTx, Color.FromRgb(0xFF, 0xA7, 0x26), 1); }, g => g.AutoScale = true),
            ("Perf.CpuPower", f => f.CpuPower is double w ? $"{w:0.0} W" : Fmt.Missing, f => f.CpuPower is double w ? $"{w:0.0} W" : Fmt.Missing,
                (g, h) => g.AddLine(h.CpuPower, Color.FromRgb(0xEF, 0x53, 0x50)), g => { g.AutoScale = true; g.MinAutoMax = 20; }),
            ("Perf.Thermals", f => f.CpuTemp is double t ? $"{t:0} °C" : Fmt.Missing, f => f.CpuTemp is double t ? $"{t:0} °C" : Fmt.Missing,
                (g, h) => g.AddLine(h.CpuTemp, TempColor), g => g.Max = 110),
        })
        {
            var name = new TextBlock { FontSize = 15 };
            name.SetResourceReference(TextBlock.TextProperty, key);
            var subText = new TextBlock { Text = Fmt.Missing, Margin = new Thickness(0, 2, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
            subText.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
            var graph = new LineGraph { Width = 78, Height = 40, GridColumns = 4, GridRows = 2, VisibleSamples = window };
            setup(graph);
            var tile = new DockPanel();
            DockPanel.SetDock(graph, Dock.Right);
            tile.Children.Add(graph);
            tile.Children.Add(new StackPanel { Children = { name, subText }, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
            DeviceList.Items.Add(new ListBoxItem { Content = tile });
            devices.Add(new Device(key, sub, value, lines, setup, subText, graph));
        }

        for (int i = 0; i < Stats.Length; i++)
        {
            if (i % 4 == 0) StatsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var label = new TextBlock { Style = (Style)FindResource("StatLabel") };
            label.SetResourceReference(TextBlock.TextProperty, Stats[i]);
            var value = new TextBlock { Text = Fmt.Missing, Style = (Style)FindResource("StatValue"), TextTrimming = TextTrimming.CharacterEllipsis };
            statValues[Stats[i]] = value;
            var pair = new StackPanel { Children = { label, value } };
            Grid.SetRow(pair, i / 4);
            Grid.SetColumn(pair, i % 4);
            StatsGrid.Children.Add(pair);
        }

        loadingView = true;
        foreach (string key in new[] { "Perf.Expanded", "Perf.Combined" })
        {
            var item = new ComboBoxItem { Tag = key };
            item.SetResourceReference(ContentControl.ContentProperty, key);
            ViewBox.Items.Add(item);
        }
        ViewBox.SelectedIndex = 0;
        loadingView = false;
    }

    // Called once by the window: the graphs read the same history as the Summary page.
    public void Bind(History h)
    {
        history = h;
        for (int i = 0; i < devices.Count; i++) devices[i].Lines(devices[i].TileGraph, h);
        // For --snapshot: SAWNTASK_PERF=<device index>[,combined] opens that view.
        if (Environment.GetEnvironmentVariable("SAWNTASK_PERF")?.Split(',') is [var index, ..] and var parts && int.TryParse(index, out int device))
        {
            loadingView = true;
            DeviceList.SelectedIndex = Math.Clamp(device, 0, devices.Count - 1);
            if (parts.Contains("combined")) ViewBox.SelectedIndex = 1;
            loadingView = false;
        }
        ShowDevice();
    }

    bool IsCpu => DeviceList.SelectedIndex <= 0;
    bool Expanded => ViewBox.SelectedIndex == 0;

    // The page became visible: read the machine facts (cached) and redraw.
    public async void Show()
    {
        foreach (var d in devices) d.TileGraph.InvalidateVisual();
        BigGraph.InvalidateVisual();
        foreach (var g in CoreGrid.Children.OfType<Grid>().Select(c => c.Children[0]).OfType<LineGraph>()) g.InvalidateVisual();
        if (info is null)
        {
            info = await SystemInfo.Load();
            ShowDevice(); // the CPU's name is known now
            if (last is not null) Update(lastSnapshot!, last, false);
        }
    }

    public void Relocalize()
    {
        ShowDevice();
        if (last is not null && lastSnapshot is not null) Update(lastSnapshot, last, false);
    }

    void DeviceList_SelectionChanged(object sender, SelectionChangedEventArgs e) => ShowDevice();
    void ViewBox_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (!loadingView) ShowDevice(); }

    // Rebuild the right-hand panel for the selected device and view.
    void ShowDevice()
    {
        if (history is null || DeviceList.SelectedIndex < 0) return;
        var device = devices[DeviceList.SelectedIndex];
        bool cpu = IsCpu;

        DeviceTitle.Text = cpu ? info?.CpuName is { Length: > 0 } n ? n : "CPU" : Loc.T(device.Key);
        Led.Visibility = cpu ? Visibility.Visible : Visibility.Collapsed;
        ViewBox.Visibility = cpu ? Visibility.Visible : Visibility.Collapsed;
        StatsGrid.Visibility = cpu ? Visibility.Visible : Visibility.Collapsed;
        GraphNote.Text = cpu ? Loc.T("Perf.ByCore") : "";

        bool cores = cpu && Expanded;
        CoreGrid.Visibility = cores ? Visibility.Visible : Visibility.Collapsed;
        BigGraph.Visibility = cores ? Visibility.Collapsed : Visibility.Visible;
        if (cores) BuildCoreGraphs();
        else BuildBigGraph(device);
        if (cpu && !Expanded) GraphNote.Text = Loc.T("Perf.Over60");
    }

    void BuildBigGraph(Device device)
    {
        // A graph cannot drop lines, so a fresh one replaces it when the device changes.
        var parent = (Grid)BigGraph.Parent;
        int index = parent.Children.IndexOf(BigGraph);
        parent.Children.Remove(BigGraph);
        BigGraph = new LineGraph { GridColumns = 10, GridRows = 6, VisibleSamples = window };
        parent.Children.Insert(index, BigGraph);
        device.Setup(BigGraph);
        device.Lines(BigGraph, history!);
        if (IsCpu) BigGraph.AddLine(history!.CpuKernel, KernelColor, 1);
    }

    void BuildCoreGraphs()
    {
        int count = history!.Cores.Count;
        CoreGrid.Children.Clear();
        CoreGrid.Columns = count > 8 ? (int)Math.Ceiling(count / 3.0) : 4;
        for (int i = 0; i < count; i++)
        {
            var label = new TextBlock { Text = $"CPU {i}", FontSize = 11, Margin = new Thickness(4, 2, 0, 0) };
            label.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
            var graph = new LineGraph { GridColumns = 6, GridRows = 4, MinHeight = 60, VisibleSamples = window };
            graph.AddLine(history.Cores[i], CpuColor);
            var cell = new Grid { Margin = new Thickness(3) };
            cell.Children.Add(graph);
            cell.Children.Add(label);
            CoreGrid.Children.Add(cell);
        }
    }

    // A new sample. newSample: the graphs start their slide; false: just redraw the numbers.
    public void Update(Snapshot s, SampleFrame f, bool newSample)
    {
        last = f;
        lastSnapshot = s;
        if (history is null) return;

        // Every graph on this page spans the last minute, like Task Manager (no compressed history).
        int samples = (int)Math.Round(VisibleSpan.TotalMilliseconds / Math.Max(100, f.IntervalMs));
        if (samples != window)
        {
            window = samples;
            BigGraph.VisibleSamples = samples;
            foreach (var cell in CoreGrid.Children.OfType<Grid>()) ((LineGraph)cell.Children[0]).VisibleSamples = samples;
        }
        foreach (var d in devices)
        {
            d.TileGraph.VisibleSamples = samples;
            d.SubText.Text = d.Sub(f);
            if (newSample) d.TileGraph.Advance(TimeSpan.FromMilliseconds(f.IntervalMs));
        }

        var device = devices[Math.Max(0, DeviceList.SelectedIndex)];
        DeviceValue.Text = device.Value(f);
        Led.Value = f.Cpu ?? 0;

        if (IsCpu)
        {
            if (Expanded && CoreGrid.Children.Count != history.Cores.Count) BuildCoreGraphs();
            UpdateStats(s, f);
        }

        if (!newSample) return;
        var span = TimeSpan.FromMilliseconds(f.IntervalMs);
        if (IsCpu && Expanded)
            foreach (var cell in CoreGrid.Children.OfType<Grid>())
                ((LineGraph)cell.Children[0]).Advance(span);
        else
            BigGraph.Advance(span);
    }

    void UpdateStats(Snapshot s, SampleFrame f)
    {
        void Set(string key, string text) => statValues[key].Text = text;
        Set("Perf.Utilization", Fmt.Pct(f.Cpu));
        Set("Perf.Speed", f.CpuSpeed is double mhz ? SystemInfoPage.Ghz(mhz) : Fmt.Missing);
        Set("Perf.Processes", s.ProcessCount.ToString("N0"));
        Set("Perf.Threads", s.Threads.ToString("N0"));
        Set("Perf.Handles", s.Handles.ToString("N0"));
        if (info is null) return;
        Set("Perf.Uptime", SystemInfoPage.Uptime(info.Uptime));
        Set("Perf.Shutdown", SystemInfoPage.Shutdown(info.Shutdown));
        Set("Perf.BaseSpeed", SystemInfoPage.Ghz(info.BaseMhz));
        Set("Perf.Sockets", info.Topology.Sockets.ToString());
        Set("Perf.Cores", info.Topology.PhysicalCores.ToString());
        Set("Perf.Logical", info.Topology.LogicalProcessors.ToString());
        Set("Perf.Virtualization", Loc.T(info.VirtualizationEnabled == true ? "SI.Enabled" : "SI.Disabled"));
        Set("Perf.Vm", Loc.T(info.VirtualMachine ? "SI.Yes" : "SI.No"));
        Set("Perf.L1", Fmt.Bytes(info.Topology.L1Bytes));
        Set("Perf.L2", Fmt.Bytes(info.Topology.L2Bytes));
        Set("Perf.L3", Fmt.Bytes(info.Topology.L3Bytes));
    }
}
