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
// graph (Combined); every device has a table of facts and figures under its graph, and the disk
// and the GPU a second graph (transfer rate, graphics memory).
public partial class PerformancePage : UserControl
{
    static readonly Color CpuColor = Color.FromRgb(0x66, 0xBB, 0x6A);
    static readonly Color KernelColor = Color.FromRgb(0xEF, 0x53, 0x50);
    static readonly Color TempColor = Color.FromRgb(0xFF, 0xA7, 0x26);

    // Positions in the device list.
    const int CpuIndex = 0, MemoryIndex = 1, GpuIndex = 2, DiskIndex = 3, NetworkIndex = 4, PowerIndex = 5, ThermalIndex = 6;

    // One entry per device on the left: its title key, how its value reads, and its graphs.
    sealed record Device(string Key, Func<SampleFrame, string> Sub, Func<SampleFrame, string> Value,
        Action<LineGraph, History> Lines, Action<LineGraph> Setup, TextBlock SubText, LineGraph TileGraph);

    // One figure of the table under the graph; Span is how many of the four columns it takes.
    sealed record Stat(string Label, string Value, int Span = 1);

    readonly List<Device> devices = [];
    readonly List<TextBlock> statCells = [];
    string statsSignature = "";
    string gpuNames = "";
    History? history;
    SystemInfoData? info;
    SampleFrame? last;
    Snapshot? lastSnapshot;
    bool loadingView;
    bool cpuExpanded = true;
    int gpuAdapter;
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
    }

    // Called once by the window: the graphs read the same history as the Summary page.
    public void Bind(History h)
    {
        history = h;
        for (int i = 0; i < devices.Count; i++) devices[i].Lines(devices[i].TileGraph, h);
        // For --snapshot: SAWNTASK_PERF=<device index>[,combined] opens that view.
        if (Environment.GetEnvironmentVariable("SAWNTASK_PERF")?.Split(',') is [var index, ..] and var parts && int.TryParse(index, out int device))
        {
            DeviceList.SelectedIndex = Math.Clamp(device, 0, devices.Count - 1);
            if (parts.Contains("combined")) cpuExpanded = false;
        }
        ShowDevice();
    }

    int Selected => Math.Max(0, DeviceList.SelectedIndex);
    bool IsCpu => Selected == CpuIndex;
    bool Expanded => cpuExpanded;

    // The page became visible: read the machine facts (cached) and redraw.
    public async void Show()
    {
        foreach (var d in devices) d.TileGraph.InvalidateVisual();
        BigGraph.InvalidateVisual();
        SecondGraph.InvalidateVisual();
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

    // The box on the right: Expanded / Combined for the CPU, the adapters for the GPU.
    void ViewBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (loadingView || ViewBox.SelectedIndex < 0) return;
        if (IsCpu) cpuExpanded = ViewBox.SelectedIndex == 0;
        else if (Selected == GpuIndex) gpuAdapter = ViewBox.SelectedIndex;
        ShowDevice(refillViewBox: false);
    }

    void FillViewBox()
    {
        loadingView = true;
        ViewBox.Items.Clear();
        if (IsCpu)
        {
            foreach (string key in new[] { "Perf.Expanded", "Perf.Combined" })
            {
                var item = new ComboBoxItem();
                item.SetResourceReference(ContentControl.ContentProperty, key);
                ViewBox.Items.Add(item);
            }
            ViewBox.SelectedIndex = cpuExpanded ? 0 : 1;
            ViewBox.Visibility = Visibility.Visible;
        }
        else if (Selected == GpuIndex)
        {
            var adapters = last?.Gpus ?? [];
            foreach (var g in adapters) ViewBox.Items.Add(new ComboBoxItem { Content = ShortGpu(g.Name) });
            gpuAdapter = Math.Clamp(gpuAdapter, 0, Math.Max(0, adapters.Count - 1));
            if (adapters.Count > 0) ViewBox.SelectedIndex = gpuAdapter;
            ViewBox.Visibility = adapters.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        }
        else ViewBox.Visibility = Visibility.Collapsed;
        loadingView = false;
    }

    static string ShortGpu(string name) => name.Replace("NVIDIA GeForce ", "");

    GpuDevice? Adapter => last?.Gpus.ElementAtOrDefault(gpuAdapter);

    // Rebuild the right-hand panel for the selected device and view.
    void ShowDevice(bool refillViewBox = true)
    {
        if (history is null || DeviceList.SelectedIndex < 0) return;
        var device = devices[Selected];
        bool cpu = IsCpu;
        if (refillViewBox) FillViewBox();

        DeviceTitle.Text = cpu ? info?.CpuName is { Length: > 0 } n ? n : "CPU"
            : Selected == GpuIndex && Adapter is { } gpu ? gpu.Name
            : Loc.T(device.Key);
        Led.Visibility = Selected is CpuIndex or MemoryIndex or GpuIndex or DiskIndex ? Visibility.Visible : Visibility.Collapsed;
        GraphNote.Text = Selected switch
        {
            CpuIndex => Loc.T("Perf.ByCore"),
            ThermalIndex => Loc.T("Perf.TempNote"),
            NetworkIndex or PowerIndex => "", // these two show the scale of the graph (see ShowScales)
            _ => Loc.T("Perf.Over60"),
        };

        bool cores = cpu && Expanded;
        CoreGrid.Visibility = cores ? Visibility.Visible : Visibility.Collapsed;
        BigGraph.Visibility = cores ? Visibility.Collapsed : Visibility.Visible;
        if (cores) BuildCoreGraphs();
        else BuildBigGraph(device);
        if (cpu && !Expanded) GraphNote.Text = Loc.T("Perf.Over60");
        BuildSecondGraph();
        statsSignature = ""; // another device: the table is built again by the next update
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

    // The disk's transfer rate (read and write) and the GPU's dedicated memory.
    void BuildSecondGraph()
    {
        bool has = Selected is DiskIndex or GpuIndex;
        SecondPanel.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        var parent = (DockPanel)SecondGraph.Parent;
        parent.Children.Remove(SecondGraph);
        SecondGraph = new LineGraph { GridColumns = 10, GridRows = 4, VisibleSamples = window };
        parent.Children.Add(SecondGraph);
        if (!has) return;
        if (Selected == DiskIndex)
        {
            SecondGraph.AutoScale = true;
            SecondGraph.AddLine(history!.DiskRead, Color.FromRgb(0x9C, 0xCC, 0x65));
            SecondGraph.AddLine(history.DiskWrite, Color.FromRgb(0xFF, 0xA7, 0x26), 1);
        }
        else
        {
            SecondGraph.Max = 100;
            SecondGraph.AddLine(history!.GpuMem, Color.FromRgb(0x42, 0xA5, 0xF5));
        }
        ShowScales();
    }

    // Text beside the graphs that names their scale; it follows the data, so it is set on every update.
    void ShowScales()
    {
        switch (Selected)
        {
            case NetworkIndex: GraphNote.Text = Loc.F("Perf.NetNote", Fmt.Rate(BigGraph.CurrentMax)); break;
            case PowerIndex: GraphNote.Text = Loc.F("Perf.PowerNote", BigGraph.CurrentMax); break;
            case DiskIndex: SecondTitle.Text = Loc.F("Perf.DiskTransfer", Fmt.Rate(SecondGraph.CurrentMax)); break;
            case GpuIndex: SecondTitle.Text = Loc.T("Perf.GpuMemGraph"); break;
        }
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
            SecondGraph.VisibleSamples = samples;
            foreach (var cell in CoreGrid.Children.OfType<Grid>()) ((LineGraph)cell.Children[0]).VisibleSamples = samples;
        }
        foreach (var d in devices)
        {
            d.TileGraph.VisibleSamples = samples;
            d.SubText.Text = d.Sub(f);
            if (newSample) d.TileGraph.Advance(TimeSpan.FromMilliseconds(f.IntervalMs));
        }

        // A graphics card appeared or went away: the adapter box has to be filled again.
        string names = string.Join("|", f.Gpus.Select(g => g.Name));
        if (names != gpuNames)
        {
            gpuNames = names;
            if (Selected == GpuIndex) ShowDevice();
        }

        var device = devices[Selected];
        DeviceValue.Text = device.Value(f);
        Led.Value = Selected switch
        {
            MemoryIndex => f.Mem ?? 0,
            GpuIndex => f.Gpu ?? 0,
            DiskIndex => f.DiskActive ?? 0,
            _ => f.Cpu ?? 0,
        };

        if (IsCpu && Expanded && CoreGrid.Children.Count != history.Cores.Count) BuildCoreGraphs();
        RenderStats(StatsFor(s, f));
        ShowScales();

        if (!newSample) return;
        var span = TimeSpan.FromMilliseconds(f.IntervalMs);
        if (IsCpu && Expanded)
            foreach (var cell in CoreGrid.Children.OfType<Grid>())
                ((LineGraph)cell.Children[0]).Advance(span);
        else
            BigGraph.Advance(span);
        if (SecondPanel.Visibility == Visibility.Visible) SecondGraph.Advance(span);
    }

    // ---- the table under the graph ----

    // Writes the figures into the table. The cells are rebuilt only when the labels change (another
    // device, another language, a disk that appeared); otherwise just their text is replaced.
    void RenderStats(List<Stat> stats)
    {
        string signature = string.Join("\u0001", stats.Select(x => $"{x.Label}\u0002{x.Span}"));
        if (signature != statsSignature)
        {
            statsSignature = signature;
            statCells.Clear();
            StatsGrid.Children.Clear();
            StatsGrid.RowDefinitions.Clear();
            StatsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            int row = 0, column = 0;
            foreach (var stat in stats)
            {
                if (column + stat.Span > 4)
                {
                    row++;
                    column = 0;
                    StatsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                }
                var label = new TextBlock { Text = stat.Label, Style = (Style)FindResource("StatLabel"), TextTrimming = TextTrimming.CharacterEllipsis };
                var value = new TextBlock { Text = stat.Value, Style = (Style)FindResource("StatValue"), TextTrimming = TextTrimming.CharacterEllipsis };
                var pair = new StackPanel { Children = { label, value } };
                Grid.SetRow(pair, row);
                Grid.SetColumn(pair, column);
                Grid.SetColumnSpan(pair, stat.Span);
                StatsGrid.Children.Add(pair);
                statCells.Add(value);
                column += stat.Span;
            }
        }
        for (int i = 0; i < stats.Count && i < statCells.Count; i++)
            if (statCells[i].Text != stats[i].Value)
            {
                statCells[i].Text = stats[i].Value;
                statCells[i].ToolTip = stats[i].Span > 1 ? stats[i].Value : null; // wide values may be cut off
            }
    }

    List<Stat> StatsFor(Snapshot s, SampleFrame f) => Selected switch
    {
        MemoryIndex => MemoryStats(s, f),
        GpuIndex => GpuStats(f),
        DiskIndex => DiskStats(f),
        NetworkIndex => NetworkStats(f),
        PowerIndex => PowerStats(f),
        ThermalIndex => ThermalStats(f),
        _ => CpuStats(s, f),
    };

    static string L(string key) => Loc.T(key);

    List<Stat> CpuStats(Snapshot s, SampleFrame f)
    {
        string FromInfo(Func<SystemInfoData, string> read) => info is null ? Fmt.Missing : read(info);
        return
        [
            new(L("Perf.Utilization"), Fmt.Pct(f.Cpu)),
            new(L("Perf.Speed"), f.CpuSpeed is double mhz ? SystemInfoPage.Ghz(mhz) : Fmt.Missing),
            new(L("Perf.Processes"), s.ProcessCount.ToString("N0")),
            new(L("Perf.Threads"), s.Threads.ToString("N0")),
            new(L("Perf.Handles"), s.Handles.ToString("N0")),
            new(L("Perf.Uptime"), FromInfo(i => SystemInfoPage.Uptime(i.Uptime))),
            new(L("Perf.Shutdown"), FromInfo(i => SystemInfoPage.Shutdown(i.Shutdown))),
            new(L("Perf.BaseSpeed"), FromInfo(i => SystemInfoPage.Ghz(i.BaseMhz))),
            new(L("Perf.Sockets"), FromInfo(i => i.Topology.Sockets.ToString())),
            new(L("Perf.Cores"), FromInfo(i => i.Topology.PhysicalCores.ToString())),
            new(L("Perf.Logical"), FromInfo(i => i.Topology.LogicalProcessors.ToString())),
            new(L("Perf.Virtualization"), FromInfo(i => Loc.T(i.VirtualizationEnabled == true ? "SI.Enabled" : "SI.Disabled"))),
            new(L("Perf.Vm"), FromInfo(i => Loc.T(i.VirtualMachine ? "SI.Yes" : "SI.No"))),
            new(L("Perf.L1"), FromInfo(i => Fmt.Bytes(i.Topology.L1Bytes))),
            new(L("Perf.L2"), FromInfo(i => Fmt.Bytes(i.Topology.L2Bytes))),
            new(L("Perf.L3"), FromInfo(i => Fmt.Bytes(i.Topology.L3Bytes))),
        ];
    }

    List<Stat> MemoryStats(Snapshot s, SampleFrame f)
    {
        var modules = info?.Modules ?? [];
        int speed = modules.Select(m => m.ConfiguredMTs > 0 ? m.ConfiguredMTs : m.SpeedMTs).DefaultIfEmpty(0).Max();
        string type = modules.Select(m => m.Type).FirstOrDefault(t => t.Length > 0) ?? "";
        string form = modules.Select(m => m.Form).FirstOrDefault(t => t.Length > 0) ?? "";
        return
        [
            new(L("Perf.InUse"), Fmt.Bytes(f.MemUsed)),
            new(L("Perf.Available"), Fmt.Bytes(f.MemTotal >= f.MemUsed ? f.MemTotal - f.MemUsed : 0)),
            new(L("Perf.Committed"), $"{Fmt.Bytes(s.MemCommitted)} / {Fmt.Bytes(s.MemCommitLimit)}"),
            new(L("Perf.Cached"), Fmt.Bytes(s.MemCached)),
            new(L("Perf.PagedPool"), Fmt.Bytes(s.MemPagedPool)),
            new(L("Perf.NonPagedPool"), Fmt.Bytes(s.MemNonPagedPool)),
            new(L("Perf.MemTotal"), Fmt.Bytes(f.MemTotal)),
            new(L("Perf.MemModules"), info is null ? Fmt.Missing : modules.Count.ToString()),
            new(L("Perf.MemSpeed"), speed > 0 ? $"{speed} MT/s" : Fmt.Missing),
            new(L("Perf.MemType"), type.Length > 0 ? type : Fmt.Missing),
            new(L("Perf.MemForm"), form.Length > 0 ? form : Fmt.Missing),
        ];
    }

    List<Stat> GpuStats(SampleFrame f)
    {
        var g = Adapter;
        // The driver facts come from Windows; match the card by name.
        var facts = g is null ? null : info?.Gpus.FirstOrDefault(x => x.Name.Equals(g.Name, StringComparison.OrdinalIgnoreCase))
                                       ?? info?.Gpus.FirstOrDefault(x => g.Name.Contains(x.Name, StringComparison.OrdinalIgnoreCase));
        return
        [
            new(L("Perf.Utilization"), Fmt.Pct(g?.Utilization ?? f.Gpu)),
            new(L("Perf.GpuMem"), g is { VramUsed: ulong used, VramTotal: ulong total } ? $"{Fmt.Bytes(used)} / {Fmt.Bytes(total)}" : Fmt.Missing),
            new(L("Perf.GpuCore"), g?.CoreMHz is double core ? $"{core:0} MHz" : Fmt.Missing),
            new(L("Perf.GpuMemClock"), g?.MemMHz is double mem ? $"{mem:0} MHz" : Fmt.Missing),
            new(L("Perf.GpuTemp"), g?.TempC is double t ? $"{t:0} °C" : Fmt.Missing),
            new(L("Perf.GpuPower"), g?.PowerW is double w ? $"{w:0.0} W" : Fmt.Missing),
            new(L("Perf.GpuDriver"), facts?.DriverVersion is { Length: > 0 } v ? v : Fmt.Missing),
            new(L("Perf.GpuDriverDate"), facts?.DriverDate is { Length: > 0 } d ? d : Fmt.Missing),
        ];
    }

    List<Stat> DiskStats(SampleFrame f)
    {
        var stats = new List<Stat>
        {
            new(L("Perf.DiskActive"), Fmt.Pct(f.DiskActive)),
            new(L("Perf.DiskRead"), Fmt.Rate(f.DiskRead)),
            new(L("Perf.DiskWrite"), Fmt.Rate(f.DiskWrite)),
            new(L("Perf.DiskCount"), info is null ? Fmt.Missing : info.Disks.Count.ToString()),
        };
        if (info is null) return stats;
        foreach (var d in info.Disks)
            stats.Add(new Stat($"{L("Perf.Disk")} {d.Number}  ·  {$"{d.Bus} {d.Kind}".Trim()}  ·  {Fmt.Bytes(d.SizeBytes)}", d.Model, 2));
        foreach (var v in info.Volumes.Where(v => v.TotalBytes > 0 && (v.Kind is "Fixed" or "Removable")))
            stats.Add(new Stat(Loc.F("Perf.Volume", v.Name.TrimEnd('\\')), Loc.F("Perf.VolumeValue", v.Format, Fmt.Bytes(v.FreeBytes), Fmt.Bytes(v.TotalBytes)), 2));
        return stats;
    }

    List<Stat> NetworkStats(SampleFrame f)
    {
        var stats = new List<Stat>
        {
            new(L("Perf.NetRecv"), Fmt.Rate(f.NetRx)),
            new(L("Perf.NetSend"), Fmt.Rate(f.NetTx)),
        };
        if (info is null) return stats;
        foreach (var a in info.Adapters.Where(a => a.Addresses.Count > 0))
            stats.Add(new Stat(a.Name, $"{LinkSpeed(a.SpeedBps)}  ·  {a.Addresses[0]}", 2));
        return stats;
    }

    static string LinkSpeed(long bitsPerSecond) =>
        bitsPerSecond <= 0 ? Fmt.Missing
        : bitsPerSecond >= 1_000_000_000 ? $"{bitsPerSecond / 1e9:0.#} Gbps" : $"{bitsPerSecond / 1e6:0} Mbps";

    List<Stat> PowerStats(SampleFrame f)
    {
        // The average of the minute shown in the graph.
        var series = history!.CpuPower;
        double sum = 0;
        int n = 0;
        for (int i = Math.Max(0, series.Count - window); i < series.Count; i++)
            if (!double.IsNaN(series[i])) { sum += series[i]; n++; }
        double peak = series.MaxValue();
        var stats = new List<Stat>
        {
            new(L("Perf.PowerNow"), f.CpuPower is double w ? $"{w:0.0} W" : Fmt.Missing),
            new(L("Perf.PowerAvg"), n > 0 ? $"{sum / n:0.0} W" : Fmt.Missing),
            new(L("Perf.PowerPeak"), peak > 0 ? $"{peak:0.0} W" : Fmt.Missing),
        };
        foreach (var p in f.Power) stats.Add(new Stat(ShortGpu(p.Label), $"{p.Watts:0.0} W"));
        return stats;
    }

    List<Stat> ThermalStats(SampleFrame f)
    {
        var sensors = f.Thermals.Select(t => (Label: t.Label, Celsius: t.Celsius))
            .Concat(f.Gpus.Where(g => g.TempC is not null).Select(g => (Label: ShortGpu(g.Name), Celsius: g.TempC!.Value)))
            .ToList();
        var stats = new List<Stat>
        {
            new(L("Perf.TempNow"), f.CpuTemp is double t ? $"{t:0} °C" : Fmt.Missing),
            new(L("Perf.TempHottest"), sensors.Count > 0 ? $"{sensors.Max(x => x.Celsius):0} °C" : Fmt.Missing),
        };
        foreach (var (label, celsius) in sensors) stats.Add(new Stat(label, $"{celsius:0.0} °C"));
        return stats;
    }
}
