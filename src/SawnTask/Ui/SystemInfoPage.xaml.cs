using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using SawnTask.Core;
using SawnTask.Native;

namespace SawnTask.Ui;

// A row of the table. The value can change while the page is open (the time since boot, a
// source's health), so a row announces it and the grid updates in place without losing the
// selection or the scroll position.
public sealed class InfoRow(string property, string value, string hint = "") : INotifyPropertyChanged
{
    string valueText = value;

    public string Property { get; } = property;
    public string Hint { get; } = hint;

    public string Value
    {
        get => valueText;
        set
        {
            if (valueText == value) return;
            valueText = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public partial class SystemInfoPage : UserControl
{
    // The category list: a group heading, then its categories.
    static readonly (string Group, string[] Categories)[] Layout =
    [
        ("SI.G.Hardware", ["SI.C.Overview", "SI.C.Processor", "SI.C.Memory", "SI.C.Graphics", "SI.C.Storage", "SI.C.Power"]),
        ("SI.G.Network", ["SI.C.Network"]),
        ("SI.G.Software", ["SI.C.Os"]),
        ("SI.G.Diagnostics", ["SI.C.Providers", "SI.C.Caps", "SI.C.Snapshot"]),
    ];

    readonly ObservableCollection<InfoRow> rows = [];
    SystemInfoData? data;
    Snapshot? snapshot;
    string category = "SI.C.Overview";
    bool loading;

    public SystemInfoPage()
    {
        InitializeComponent();
        Table.ItemsSource = rows;

        foreach (var (group, categories) in Layout)
        {
            var heading = new ListBoxItem { Style = (Style)FindResource("SubNavHeader") };
            heading.SetResourceReference(ContentProperty, group);
            Categories.Items.Add(heading);
            foreach (string key in categories)
            {
                var item = new ListBoxItem { Tag = key, Style = (Style)FindResource("SubNavItem") };
                item.SetResourceReference(ContentProperty, key);
                Categories.Items.Add(item);
            }
        }
        Categories.SelectedIndex = 1;

        // SAWNTASK_SYSINFO=SI.C.Memory opens that category (used for screenshots, like SAWNTASK_THEME).
        if (Environment.GetEnvironmentVariable("SAWNTASK_SYSINFO") is { Length: > 0 } wanted)
            foreach (ListBoxItem item in Categories.Items)
                if (item.Tag as string == wanted) Categories.SelectedItem = item;
    }

    // Called when the page comes on screen: the facts are read the first time it is opened.
    public void Show()
    {
        if (data is null && !loading) Load(refresh: false);
    }

    // Called with every new snapshot while the page is open (the diagnostics pages and the
    // time since boot move).
    public void Update(Snapshot s)
    {
        snapshot = s;
        Rebuild();
    }

    public void Relocalize() => Rebuild();

    void Refresh_Click(object sender, RoutedEventArgs e) => Load(refresh: true);

    async void Load(bool refresh)
    {
        loading = true;
        Rebuild();
        data = await SystemInfo.Load(refresh);
        loading = false;
        Rebuild();
    }

    void Categories_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Categories.SelectedItem is not ListBoxItem { Tag: string key }) return;
        category = key;
        rows.Clear(); // a different table: start from the top
        Rebuild();
    }

    // Recomputes the rows of the open category and applies them: in place when the same
    // properties are listed, otherwise by replacing the list.
    void Rebuild()
    {
        List<InfoRow> fresh = loading || data is null ? [new InfoRow("", Loc.T("SI.Loading"))] : Rows(category, data, snapshot);
        if (rows.Count == fresh.Count && rows.Zip(fresh).All(p => p.First.Property == p.Second.Property))
        {
            foreach (var (old, row) in rows.Zip(fresh)) old.Value = row.Value;
            return;
        }
        rows.Clear();
        foreach (var row in fresh) rows.Add(row);
    }

    // ---- what each category lists ----

    static string T(string key) => Loc.T(key);
    static string YesNo(bool value) => T(value ? "SI.Yes" : "SI.No");
    static string Or(string text) => text.Length > 0 ? text : Fmt.Missing;
    internal static string Ghz(double mhz) => mhz > 0 ? $"{mhz / 1000:0.00} GHz" : Fmt.Missing;

    internal static string Uptime(TimeSpan t) => $"{(int)t.TotalDays}:{t.Hours:00}:{t.Minutes:00}:{t.Seconds:00}";

    internal static string Shutdown(ShutdownLog.Reason reason) => reason.Kind switch
    {
        ShutdownLog.Kind.Planned => T(reason.Type == "restart" ? "SI.ShutRestart" : "SI.ShutPlanned"),
        ShutdownLog.Kind.Unexpected => T("SI.ShutUnexpected"),
        _ => T("SI.Unknown"),
    };

    static string Date(string text) =>
        DateTime.TryParseExact(text, ["M-d-yyyy", "MM-dd-yyyy", "M/d/yyyy"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date.ToShortDateString()
            : text;

    static string Speed(long bitsPerSecond) =>
        bitsPerSecond <= 0 ? Fmt.Missing : bitsPerSecond >= 1_000_000_000 ? $"{bitsPerSecond / 1e9:0.#} Gb/s" : $"{bitsPerSecond / 1_000_000} Mb/s";

    static List<InfoRow> Rows(string category, SystemInfoData d, Snapshot? s)
    {
        var r = new List<InfoRow>();
        void Add(string property, string value, string hint = "") => r.Add(new InfoRow(property, value, hint));
        var cpu = d.Topology;

        switch (category)
        {
            case "SI.C.Overview":
                Add(T("SI.Computer"), Or(d.Os?.ComputerName ?? ""));
                Add(T("SI.System"), Or((d.SystemManufacturer + " " + d.SystemProduct).Trim()));
                Add(T("SI.C.Processor"), Or(d.CpuName));
                Add(T("Perf.Logical"), cpu.LogicalProcessors.ToString());
                Add(T("Perf.Cores"), cpu.PhysicalCores.ToString());
                Add(T("Perf.Sockets"), cpu.Sockets.ToString());
                Add(T("SI.C.Memory"), d.TotalMemory > 0 ? Fmt.Bytes(d.TotalMemory) : Fmt.Missing);
                Add(T("SI.C.Graphics"), d.Gpus.Count > 0 ? string.Join(", ", d.Gpus.Select(g => g.Name)) : Fmt.Missing);
                Add(T("SI.C.Storage"), d.Disks.Count > 0 ? string.Join(", ", d.Disks.Select(k => $"{k.Model} ({Fmt.Bytes(k.SizeBytes)})")) : Fmt.Missing);
                Add(T("SI.Vm"), YesNo(d.VirtualMachine));
                Add(T("SI.Uptime"), Uptime(d.Uptime));
                break;

            case "SI.C.Processor":
                Add(T("SI.C.Processor"), Or(d.CpuName));
                Add(T("SI.Vendor"), Or(d.CpuVendor));
                Add(T("Perf.BaseSpeed"), Ghz(d.BaseMhz), "CallNtPowerInformation(ProcessorInformation).MaxMhz");
                Add(T("Perf.Sockets"), cpu.Sockets.ToString());
                Add(T("Perf.Cores"), cpu.PhysicalCores.ToString());
                Add(T("Perf.Logical"), cpu.LogicalProcessors.ToString());
                Add(T("Perf.L1"), cpu.L1Bytes > 0 ? Fmt.Bytes(cpu.L1Bytes) : Fmt.Missing, "GetLogicalProcessorInformationEx, all L1 caches added up");
                Add(T("Perf.L2"), cpu.L2Bytes > 0 ? Fmt.Bytes(cpu.L2Bytes) : Fmt.Missing);
                Add(T("Perf.L3"), cpu.L3Bytes > 0 ? Fmt.Bytes(cpu.L3Bytes) : Fmt.Missing);
                Add(T("Perf.Virtualization"), d.VirtualizationEnabled is bool virt ? T(virt ? "SI.Enabled" : "SI.Disabled") : Fmt.Missing);
                Add(T("SI.Vm"), YesNo(d.VirtualMachine));
                break;

            case "SI.C.Memory":
                Add(T("SI.Installed"), d.TotalMemory > 0 ? Fmt.Bytes(d.TotalMemory) : Fmt.Missing);
                Add(T("SI.Modules"), d.Modules.Count.ToString(), "SMBIOS type 17");
                for (int i = 0; i < d.Modules.Count; i++)
                {
                    var m = d.Modules[i];
                    string kind = string.Join(" ", new[] { m.Type, m.Form }.Where(x => x.Length > 0));
                    int speed = m.ConfiguredMTs > 0 ? m.ConfiguredMTs : m.SpeedMTs;
                    Add(Or(m.Slot.Length > 0 ? m.Slot : $"{T("SI.Module")} {i + 1}"),
                        string.Join(" · ", new[] { Fmt.Bytes(m.SizeBytes), kind, speed > 0 ? $"{speed} MT/s" : "", m.Manufacturer, m.PartNumber }.Where(x => x.Length > 0)));
                }
                break;

            case "SI.C.Graphics":
                for (int i = 0; i < d.Gpus.Count; i++)
                {
                    var g = d.Gpus[i];
                    string n = d.Gpus.Count > 1 ? $" {i + 1}" : "";
                    Add($"{T("SI.Adapter")}{n}", g.Name);
                    Add($"{T("SI.Driver")}{n}", Or(string.Join(" · ", new[] { g.DriverVersion, Date(g.DriverDate), g.Provider }.Where(x => x.Length > 0))));
                    Add($"{T("SI.VideoMemory")}{n}", g.VramBytes > 0 ? Fmt.Bytes(g.VramBytes) : Fmt.Missing);
                }
                if (d.Gpus.Count == 0) Add(T("SI.C.Graphics"), Fmt.Missing);
                break;

            case "SI.C.Storage":
                foreach (var k in d.Disks)
                {
                    Add($"{T("SI.Disk")} {k.Number}", Or(k.Model));
                    Add("", Or(string.Join(" · ", new[] { Fmt.Bytes(k.SizeBytes), k.Bus, k.Kind }.Where(x => x.Length > 0))));
                    if (k.Revision.Length > 0) Add(T("SI.Firmware"), k.Revision);
                    if (k.Serial.Length > 0) Add(T("SI.Serial"), k.Serial);
                }
                foreach (var v in d.Volumes)
                    Add($"{T("SI.Volume")} {v.Name}", $"{v.Format} · {Loc.F("SI.FreeOf", Fmt.Bytes(v.FreeBytes), Fmt.Bytes(v.TotalBytes))}");
                break;

            case "SI.C.Power":
                if (d.Power is { } p)
                {
                    Add(T("SI.PowerSource"), p.OnAc is bool ac ? T(ac ? "SI.Mains" : "SI.OnBattery") : T("SI.Unknown"));
                    Add(T("SI.Battery"), p.HasBattery ? (p.BatteryPercent is int pct ? $"{pct}%" : T("SI.Unknown")) : T("SI.None"));
                    Add(T("SI.Plan"), Or(p.Scheme));
                }
                break;

            case "SI.C.Network":
                foreach (var a in d.Adapters)
                {
                    Add(a.Name, a.Description);
                    Add(T("SI.LinkSpeed"), Speed(a.SpeedBps));
                    Add(T("SI.Mac"), Or(a.Mac));
                    Add(T("SI.Address"), a.Addresses.Count > 0 ? string.Join(", ", a.Addresses) : Fmt.Missing);
                    if (a.Gateway.Length > 0) Add(T("SI.Gateway"), a.Gateway);
                    if (a.Dns.Count > 0) Add(T("SI.Dns"), string.Join(", ", a.Dns));
                }
                break;

            case "SI.C.Os":
                if (d.Os is { } os)
                {
                    Add(T("SI.OsName"), os.Name);
                    Add(T("SI.Version"), Or(os.Version));
                    Add(T("SI.Build"), os.Build);
                    Add(T("SI.InstalledOn"), os.Installed is DateTime when ? when.ToString("d") : Fmt.Missing);
                    Add(T("SI.Computer"), os.ComputerName);
                    Add(T("SI.User"), os.UserName);
                    Add(T("SI.Arch"), os.Architecture);
                    Add(T("SI.TimeZone"), os.TimeZone);
                }
                Add(T("SI.Uptime"), Uptime(d.Uptime));
                Add(T("SI.LastShutdown"), Shutdown(d.Shutdown), "System event log: 1074 planned; 41 and 6008 unexpected");
                Add(T("SI.FastStartup"), d.FastStartup is bool fast ? T(fast ? "SI.Enabled" : "SI.Disabled") : Fmt.Missing);
                Add(T("SI.Bios"), Or(string.Join(" ", new[] { d.BiosVendor, d.BiosVersion, d.BiosDate }.Where(x => x.Length > 0))));
                Add(T("SI.Board"), Or((d.BoardManufacturer + " " + d.BoardProduct).Trim()));
                break;

            case "SI.C.Providers":
                if (s is not null)
                    foreach (var src in s.Sources)
                        Add(src.Id, $"{src.Health} · {src.Detail}", src.Description);
                break;

            case "SI.C.Caps":
                Add(T("SI.Admin"), YesNo(Elevation.IsElevated));
                Add(T("SI.Smbios"), T(d.Modules.Count > 0 || d.SystemManufacturer.Length > 0 ? "SI.Available" : "SI.Unavailable"));
                Add(T("SI.EventLog"), T(d.Shutdown.Kind != ShutdownLog.Kind.Unknown ? "SI.Available" : "SI.Unavailable"));
                if (s is not null)
                    foreach (var src in s.Sources.Where(x => x.Health is SourceHealth.Unavailable or SourceHealth.Failed))
                        Add(src.Id, $"{T("SI.Unavailable")} · {src.Detail}", src.Description);
                foreach (string problem in d.Problems) Add(T("SI.NotRead"), problem);
                break;

            case "SI.C.Snapshot":
                if (s is not null)
                {
                    Add(T("SI.Generation"), s.Generation.ToString());
                    Add(T("SI.Time"), s.TimestampUtc.ToLocalTime().ToString("T"));
                    Add(T("SI.Interval"), $"{s.IntervalMs:0} ms");
                    Add(T("SI.Actual"), $"{s.ActualMs:0} ms");
                    Add(T("Perf.Logical"), s.LogicalProcessors.ToString());
                    Add(T("Perf.Processes"), s.Processes.Count.ToString());
                    Add(T("Perf.Threads"), s.Threads.ToString());
                    Add(T("Perf.Handles"), s.Handles.ToString());
                    Add(T("SI.Healthy"), $"{s.Sources.Count(x => x.Health == SourceHealth.Ok)} / {s.Sources.Count}");
                }
                break;
        }
        return r;
    }
}
