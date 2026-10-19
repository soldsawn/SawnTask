using System.ComponentModel;
using System.Runtime.CompilerServices;
using SawnTask.Core;

namespace SawnTask.Ui;

// One row in the Processes grid. Rows are updated in place (keyed by
// ProcessKey) so selection and scroll position survive each tick.
// Each column has a display text and a numeric "Sort" value.
public sealed class ProcessRow(ProcessKey key) : INotifyPropertyChanged
{
    string name = "", user = "";
    string cpuText = "", memText = "", wsText = "", readText = "", writeText = "", cpuTimeText = "", peakText = "";
    double cpuSort = -1, readSort, writeSort;
    long memSort, wsSort, cpuTimeSort, peakSort;
    uint threads, handles, session, pageFaults;

    public event PropertyChangedEventHandler? PropertyChanged;

    public ProcessKey Key { get; } = key;
    public uint Pid => Key.Pid;
    public DateTime? ExitedUtc { get; private set; }
    public string Name { get => name; private set => Set(ref name, value); }
    // The data layer says "Access denied" for protected processes; show it in the UI language.
    public string User { get => user == "Access denied" ? Loc.T("Proc.AccessDenied") : user; private set => Set(ref user, value); }
    public bool IsExited => ExitedUtc is not null;

    // Program icon (loaded once per .exe path on the UI thread) and the group this row is
    // shown in: "Apps" (has a window) or "Background processes".
    string imagePath = "";
    bool isApp;
    System.Windows.Media.ImageSource? icon;
    public System.Windows.Media.ImageSource? Icon => icon ??= IconCache.For(imagePath);
    public bool IsApp
    {
        get => isApp;
        private set
        {
            if (!Set(ref isApp, value)) return;
            Raise(nameof(Group));
            Raise(nameof(GroupOrder));
        }
    }
    public string Group => Loc.T(IsApp ? "Proc.GroupApps" : "Proc.GroupBackground");
    public int GroupOrder => IsApp ? 0 : 1; // first sort key: Apps above Background processes
    public string Status => Loc.T(IsExited ? "Status.Exited" : "Status.Running");
    public string CpuText { get => cpuText; private set => Set(ref cpuText, value); }
    public double CpuSort { get => cpuSort; private set => Set(ref cpuSort, value); }
    public string MemText { get => memText; private set => Set(ref memText, value); }
    public long MemSort { get => memSort; private set => Set(ref memSort, value); }
    public string WsText { get => wsText; private set => Set(ref wsText, value); }
    public long WsSort { get => wsSort; private set => Set(ref wsSort, value); }
    public string ReadText { get => readText; private set => Set(ref readText, value); }
    public double ReadSort { get => readSort; private set => Set(ref readSort, value); }
    public string WriteText { get => writeText; private set => Set(ref writeText, value); }
    public double WriteSort { get => writeSort; private set => Set(ref writeSort, value); }
    public string CpuTimeText { get => cpuTimeText; private set => Set(ref cpuTimeText, value); }
    public long CpuTimeSort { get => cpuTimeSort; private set => Set(ref cpuTimeSort, value); }
    public string PeakText { get => peakText; private set => Set(ref peakText, value); }
    public long PeakSort { get => peakSort; private set => Set(ref peakSort, value); }
    public uint PageFaults { get => pageFaults; private set => Set(ref pageFaults, value); }
    public uint Threads { get => threads; private set => Set(ref threads, value); }
    public uint Handles { get => handles; private set => Set(ref handles, value); }
    public uint Session { get => session; private set => Set(ref session, value); }

    public void Update(ProcessSample p)
    {
        Name = p.Name;
        if (imagePath.Length == 0 && p.ImagePath.Length > 0)
        {
            imagePath = p.ImagePath;
            icon = null;
            Raise(nameof(Icon));
        }
        if (!IsExited) IsApp = p.IsApp;
        User = p.User;
        CpuText = Fmt.Pct(p.CpuPercent);
        CpuSort = p.CpuPercent ?? -1;
        MemText = Fmt.Bytes(p.PrivateBytes);
        MemSort = p.PrivateBytes;
        WsText = Fmt.Bytes(p.WorkingSetBytes);
        WsSort = p.WorkingSetBytes;
        ReadText = Fmt.Rate(p.IoReadPerSec);
        ReadSort = p.IoReadPerSec ?? -1;
        WriteText = Fmt.Rate(p.IoWritePerSec);
        WriteSort = p.IoWritePerSec ?? -1;
        CpuTimeText = $"{(int)p.CpuTime.TotalHours}:{p.CpuTime:mm\\:ss\\.ff}";
        CpuTimeSort = p.CpuTime.Ticks;
        PeakText = Fmt.Bytes(p.PeakWorkingSetBytes);
        PeakSort = p.PeakWorkingSetBytes;
        PageFaults = p.PageFaults;
        Threads = p.Threads;
        Handles = p.Handles;
        Session = p.SessionId;
    }

    // An exited process stays visible (red, "Exited") for up to 10 seconds with its
    // last values, so a short-lived process does not just vanish. Live rates make no
    // sense after exit, so they become dashes.
    public void MarkExited()
    {
        if (ExitedUtc is not null) return;
        ExitedUtc = DateTime.UtcNow;
        CpuText = ReadText = WriteText = Fmt.Missing;
        CpuSort = ReadSort = WriteSort = -1;
        Raise(nameof(IsExited));
        Raise(nameof(Status));
    }

    // After a language switch: the status text comes from the new string table.
    public void Relocalize()
    {
        Raise(nameof(Status));
        Raise(nameof(User));
        Raise(nameof(Group));
    }

    void Raise(string property) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));

    bool Set<T>(ref T field, T value, [CallerMemberName] string property = "")
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
        return true;
    }
}

// One row on the Users page: a logon session with the totals of its processes. Updated in place
// every tick (a new list each tick rebuilt every row and lost the scroll position).
public sealed class UserRow(uint sessionId) : INotifyPropertyChanged
{
    string user = "", state = "", station = "", client = "";
    double cpu;
    long memory;
    int processes;

    public uint SessionId { get; } = sessionId;
    public string User => user;
    public string State => state;
    public string Station => station;
    public string Client => client;
    public double Cpu => cpu;
    public long Memory => memory;
    public int Processes => processes;
    public string CpuText => $"{cpu:0.0}%";
    public string MemoryText => Fmt.Bytes(memory);

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Update(string user, string state, string station, string client, double cpu, long memory, int processes)
    {
        Set(ref this.user, user, nameof(User));
        Set(ref this.state, state, nameof(State));
        Set(ref this.station, station, nameof(Station));
        Set(ref this.client, client, nameof(Client));
        if (Set(ref this.cpu, cpu, nameof(Cpu))) Raise(nameof(CpuText));
        if (Set(ref this.memory, memory, nameof(Memory))) Raise(nameof(MemoryText));
        Set(ref this.processes, processes, nameof(Processes));
    }

    bool Set<T>(ref T field, T value, string property)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(property);
        return true;
    }

    void Raise(string property) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}

// One line in the Summary's Sensors card (a temperature or a power reading).
public sealed record SensorRow(string Label, string Value, string Source);
