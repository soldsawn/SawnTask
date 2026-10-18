using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace SawnTask.Native;

// Client for Intel Dynamic Tuning (DTT/DPTF) through its IPF service: this is how the CPU
// package temperature is read without a kernel driver. Every layout below comes from Intel's
// published headers (github.com/intel/dptf, IPF/Sources/Common/ipf_core_iface.h,
// Common/esif_sdk_iface_app.h, esif_sdk_iface_esif.h, esif_sdk_participant.h).
//
// The protocol in one picture:
//   ipfcore.dll!GetIpfInterface -> IpfIface (function table)
//   IpfCore_Init, IpfCore_SetAppIface(our AppInterfaceSet), SessionCreate, SessionConnect
//   <- the service calls our AppCreate (gives us the "execute primitive" function),
//      then ParticipantCreate / DomainCreate once per sensor device
//   we call fPrimitive(..., GET_TEMPERATURE) per domain -> tenths of a kelvin
public sealed unsafe class IntelDttClient : IDisposable
{
    public sealed record Participant(ulong Handle, string Name, string Description, string AcpiScope, uint Bus);
    public sealed record Domain(ulong Participant, ulong Handle, string Name, uint Type);
    public sealed record Reading(Participant Participant, Domain Domain, double Celsius);

    // ---- constants from the ESIF headers ----
    const int ESIF_OK = 0;
    const int ESIF_E_NOT_SUPPORTED = 1002;
    const int ESIF_E_NEED_LARGER_BUFFER = 1300;
    const uint ESIF_DATA_STRING = 8;
    const uint ESIF_DATA_TEMPERATURE = 6;
    const uint ESIF_DATA_VOID = 24;
    const uint GET_TEMPERATURE = 14;
    const uint GET_RAPL_POWER = 35;
    const uint ESIF_DATA_POWER = 26;
    const byte ESIF_INSTANCE_INVALID = 255;
    public const uint DomainTypeProcessor = 0;

    // IpfIface (#pragma pack 1): hdr(8) + 3 version strings of 20 bytes, then 11 function pointers.
    const int IpfIfaceSize = 8 + 3 * 20 + 11 * 8; // 156
    const int OffCoreVersion = 8, OffServerSdkVersion = 28, OffClientSdkVersion = 48; // char[20] each
    const string ClientSdkVersion = "1.0.11402"; // sent first; the service answers with its own if it differs
    const int OffInit = 68, OffExit = 76, OffSetAppIface = 84, OffSessionCreate = 92,
              OffSessionDestroy = 100, OffSessionConnect = 108, OffSessionDisconnect = 116;

    // AppInterfaceSet (#pragma pack 1): hdr(8) + AppInterface (15 pointers) + EsifInterface (8 pointers).
    const int AppSetSize = 8 + 15 * 8 + 8 * 8; // 192 (0x00C0)
    const int OffEsifPrimitive = 8 + 15 * 8 + 2 * 8; // 144: EsifInterface.fPrimitiveFuncPtr

    // AppParticipantData (#pragma pack 1) field offsets.
    const int PartName = 0x2C, PartDesc = 0x40, PartBus = 0x94, PartAcpiScope = 0xAC;
    // AppDomainData (#pragma pack 1)
    const int DomName = 0x04, DomType = 0x40;

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    struct EsifData // 20 bytes, packed
    {
        public uint Type;
        public IntPtr Buffer;
        public uint BufferLength;
        public uint DataLength;
    }

    // Callbacks are static (native code cannot call an instance), so one client per process.
    static IntelDttClient? instance;

    readonly object gate = new();
    readonly Dictionary<ulong, Participant> participants = [];
    readonly List<Domain> domains = [];
    readonly byte* iface;
    readonly byte* appSet;
    ulong session = ulong.MaxValue;
    ulong esifHandle;
    delegate* unmanaged<ulong, ulong, ulong, EsifData*, EsifData*, uint, byte, int> primitive;

    IntelDttClient()
    {
        // Native memory, never freed: the service may still hold these pointers
        // after we disconnect, and freeing them would be a use-after-free.
        iface = (byte*)NativeMemory.AllocZeroed(IpfIfaceSize);
        appSet = (byte*)NativeMemory.AllocZeroed(AppSetSize);
    }

    public string LibraryPath { get; private set; } = "";
    public string CoreVersion { get; private set; } = "";
    public string CoreSdkVersion { get; private set; } = "";
    public bool HasPrimitive => primitive != null;

    public IReadOnlyList<Participant> Participants
    {
        get { lock (gate) return participants.Values.ToList(); }
    }

    public IReadOnlyList<Domain> Domains
    {
        get { lock (gate) return domains.ToList(); }
    }

    // No ipfsvc service registered means Intel Dynamic Tuning is not installed.
    // The client DLL lives next to the service executable in the DriverStore.
    static string? FindIpfCore()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\ipfsvc");
        if (key?.GetValue("ImagePath") is not string imagePath) return null;
        string exe = Environment.ExpandEnvironmentVariables(imagePath.Trim().Trim('"'));
        string dll = Path.Combine(Path.GetDirectoryName(exe) ?? "", "ipfcore.dll");
        return File.Exists(dll) ? dll : null;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr LoadLibraryExW(string path, IntPtr reserved, uint flags);

    const uint LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR = 0x100, LOAD_LIBRARY_SEARCH_DEFAULT_DIRS = 0x1000;

    public static IntelDttClient Connect()
    {
        if (instance is not null) throw new InvalidOperationException("Intel Dynamic Tuning client already connected");

        string dll = FindIpfCore() ?? throw new InvalidOperationException("Intel Dynamic Tuning is not installed (no ipfsvc service)");
        // Flags 0x1100: resolve ipfcore's own dependencies from its folder.
        IntPtr module = LoadLibraryExW(dll, IntPtr.Zero, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
        if (module == IntPtr.Zero)
            throw new InvalidOperationException($"LoadLibraryExW(ipfcore.dll) failed with Win32 error {Marshal.GetLastWin32Error()}");
        if (!NativeLibrary.TryGetExport(module, "GetIpfInterface", out IntPtr getIface))
            throw new InvalidOperationException("ipfcore.dll does not export GetIpfInterface");

        var client = new IntelDttClient { LibraryPath = dll };
        instance = client;
        try
        {
            client.Open((delegate* unmanaged<byte*, int>)getIface);
            return client;
        }
        catch
        {
            // Late callbacks for this half-open client now find no instance and do nothing;
            // its native tables stay allocated, so a retry can start a fresh client safely.
            instance = null;
            throw;
        }
    }

    void Open(delegate* unmanaged<byte*, int> getIpfInterface)
    {
        // The caller fills the header first: type eIfaceTypeIpfClient (7), version 1, size,
        // plus its own SDK version string. An empty version is refused (rc 1002).
        *(int*)(iface + 0) = 7;
        *(ushort*)(iface + 4) = 1;
        *(ushort*)(iface + 6) = IpfIfaceSize;
        WriteFixed(iface + OffClientSdkVersion, ClientSdkVersion);
        int rc = getIpfInterface(iface);

        // Version handshake: on refusal the DLL writes its own SDK
        // version into the header; copy that into the client field and ask again.
        string serverSdk = ReadFixed(iface + OffServerSdkVersion);
        if (rc != ESIF_OK && serverSdk.Length > 0)
        {
            WriteFixed(iface + OffClientSdkVersion, serverSdk);
            rc = getIpfInterface(iface);
        }
        Check(rc, "GetIpfInterface");
        CoreVersion = ReadFixed(iface + OffCoreVersion);
        CoreSdkVersion = ReadFixed(iface + OffServerSdkVersion);

        Check(((delegate* unmanaged<int>)Fn(iface, OffInit))(), "IpfCore_Init");

        // Our application interface: type eIfaceTypeApplication (0), APP_INTERFACE_VERSION 4, size 192.
        *(int*)(appSet + 0) = 0;
        *(ushort*)(appSet + 4) = 4;
        *(ushort*)(appSet + 6) = AppSetSize;
        IntPtr* app = (IntPtr*)(appSet + 8);
        app[0] = (IntPtr)(delegate* unmanaged<byte*, ulong, ulong*, IntPtr, int, int>)&OnAppCreate;
        app[1] = (IntPtr)(delegate* unmanaged<ulong, int>)&OnOk1;              // destroy
        app[2] = (IntPtr)(delegate* unmanaged<ulong, int>)&OnOk1;              // suspend
        app[3] = (IntPtr)(delegate* unmanaged<ulong, int>)&OnOk1;              // resume
        app[4] = (IntPtr)(delegate* unmanaged<EsifData*, int>)&OnGetVersion;
        app[5] = (IntPtr)(delegate* unmanaged<ulong, int>)&OnNotSupported;     // command
        app[6] = (IntPtr)(delegate* unmanaged<ulong, EsifData*, int>)&OnGetIntro;
        app[7] = (IntPtr)(delegate* unmanaged<EsifData*, int>)&OnGetDescription;
        app[8] = (IntPtr)(delegate* unmanaged<EsifData*, int>)&OnGetName;
        app[9] = (IntPtr)(delegate* unmanaged<ulong, int>)&OnNotSupported;     // status
        app[10] = (IntPtr)(delegate* unmanaged<ulong, ulong, byte*, int, int>)&OnParticipantCreate;
        app[11] = (IntPtr)(delegate* unmanaged<ulong, ulong, int>)&OnParticipantDestroy;
        app[12] = (IntPtr)(delegate* unmanaged<ulong, ulong, ulong, byte*, int, int>)&OnDomainCreate;
        app[13] = (IntPtr)(delegate* unmanaged<ulong, ulong, ulong, int>)&OnDomainDestroy;
        app[14] = (IntPtr)(delegate* unmanaged<ulong, int>)&OnOk1;             // event
        Check(((delegate* unmanaged<byte*, int>)Fn(iface, OffSetAppIface))(appSet), "IpfCore_SetAppIface");

        // NULL session info = defaults: the public pipe "pipe://ipfsrv.public", no admin needed.
        session = ((delegate* unmanaged<IntPtr, ulong>)Fn(iface, OffSessionCreate))(IntPtr.Zero);
        if (session == ulong.MaxValue || session == 0)
            throw new InvalidOperationException("IpfCore_SessionCreate returned no session (is the ipfsvc service running?)");
        Check(((delegate* unmanaged<ulong, int>)Fn(iface, OffSessionConnect))(session), "IpfCore_SessionConnect");
    }

    // Executes one GET_* primitive that answers with a single 32-bit value.
    uint? ReadUInt32(Domain domain, uint primitiveId, uint dataType, out int rc)
    {
        rc = -1;
        if (primitive == null) return null;
        uint raw = 0;
        var request = new EsifData { Type = ESIF_DATA_VOID };
        var response = new EsifData { Type = dataType, Buffer = (IntPtr)(&raw), BufferLength = 4 };
        rc = primitive(esifHandle, domain.Participant, domain.Handle, &request, &response, primitiveId, ESIF_INSTANCE_INVALID);
        return rc == ESIF_OK && response.DataLength >= 4 ? raw : null;
    }

    // GET_TEMPERATURE (14) on one domain. ESIF temperatures are tenths of a kelvin.
    public double? ReadCelsius(Domain domain, out int rc)
    {
        if (ReadUInt32(domain, GET_TEMPERATURE, ESIF_DATA_TEMPERATURE, out rc) is not uint raw) return null;
        double celsius = raw / 10.0 - 273.15;
        return celsius is > -40 and < 150 ? Math.Round(celsius, 1) : null; // reject nonsense, never invent
    }

    // GET_RAPL_POWER (35): the running average power of a RAPL domain (the CPU
    // package on TCPU.PKG), in milliwatts (DPTF's Power type; see EsifDataPower.cpp).
    public double? ReadWatts(Domain domain, out int rc)
    {
        if (ReadUInt32(domain, GET_RAPL_POWER, ESIF_DATA_POWER, out rc) is not uint mw) return null;
        double watts = mw / 1000.0;
        return watts is >= 0 and < 1000 ? Math.Round(watts, 1) : null;
    }

    public List<Reading> ReadAll()
    {
        var result = new List<Reading>();
        Dictionary<ulong, Participant> parts;
        List<Domain> doms;
        lock (gate)
        {
            parts = new Dictionary<ulong, Participant>(participants);
            doms = domains.ToList();
        }
        foreach (var d in doms)
        {
            if (!parts.TryGetValue(d.Participant, out var p)) continue;
            if (ReadCelsius(d, out _) is double c) result.Add(new Reading(p, d, c));
        }
        return result;
    }

    public const uint DomainTypeGraphics = 1, DomainTypeMultifunction = 9;

    // The CPU participant is Intel's ACPI device TCPU. On this kind of laptop it has
    // three domains: PKG (multifunction = whole package), CPU (processor cores) and
    // GFX (integrated graphics). The package domain is preferred, with the processor
    // domain as the fallback.
    public static bool IsCpuParticipant(Participant p) =>
        p.Name.Equals("TCPU", StringComparison.OrdinalIgnoreCase)
        || p.AcpiScope.EndsWith(".TCPU", StringComparison.OrdinalIgnoreCase);

    public static bool IsPackage(Participant p, Domain d) =>
        IsCpuParticipant(p) && (d.Type == DomainTypeMultifunction || d.Name.Equals("PKG", StringComparison.OrdinalIgnoreCase));

    public static bool IsProcessor(Participant p, Domain d) => IsCpuParticipant(p) && d.Type == DomainTypeProcessor;

    public static string Label(Participant p, Domain d)
    {
        if (!IsCpuParticipant(p)) return p.Description.Length > 0 ? p.Description : p.Name;
        if (IsPackage(p, d)) return "CPU package";
        if (IsProcessor(p, d)) return "CPU cores";
        if (d.Type == DomainTypeGraphics) return "Integrated graphics";
        return "CPU " + d.Name;
    }

    public void Dispose()
    {
        if (session != ulong.MaxValue)
        {
            ((delegate* unmanaged<ulong, void>)Fn(iface, OffSessionDisconnect))(session);
            ((delegate* unmanaged<ulong, void>)Fn(iface, OffSessionDestroy))(session);
            session = ulong.MaxValue;
        }
        ((delegate* unmanaged<void>)Fn(iface, OffExit))();
        primitive = null;
        if (instance == this) instance = null;
        // ipfcore.dll stays loaded and our tables stay allocated: a late callback
        // into freed code or memory would crash the whole app.
    }

    static IntPtr Fn(byte* table, int offset)
    {
        IntPtr fn = *(IntPtr*)(table + offset);
        if (fn == IntPtr.Zero) throw new InvalidOperationException($"ipfcore.dll left function slot {offset} empty");
        return fn;
    }

    static void Check(int rc, string what)
    {
        if (rc != ESIF_OK) throw new InvalidOperationException($"{what} failed: rc {rc}");
    }

    static void WriteFixed(byte* field, string text)
    {
        var span = new Span<byte>(field, 20);
        span.Clear();
        Encoding.ASCII.GetBytes(text.AsSpan(0, Math.Min(text.Length, 19)), span);
    }

    static string ReadFixed(byte* field)
    {
        var span = new ReadOnlySpan<byte>(field, 20);
        int nul = span.IndexOf((byte)0);
        return Encoding.ASCII.GetString(nul >= 0 ? span[..nul] : span);
    }

    static string ReadString(byte* esifData)
    {
        var d = (EsifData*)esifData;
        if (d->Buffer == IntPtr.Zero || d->DataLength == 0) return "";
        var span = new ReadOnlySpan<byte>((void*)d->Buffer, (int)Math.Min(d->DataLength, 4096));
        int nul = span.IndexOf((byte)0);
        return Encoding.UTF8.GetString(nul >= 0 ? span[..nul] : span).Trim();
    }

    static int WriteString(EsifData* d, string text)
    {
        if (d == null) return ESIF_E_NOT_SUPPORTED;
        byte[] bytes = Encoding.UTF8.GetBytes(text + "\0");
        d->DataLength = (uint)bytes.Length;
        if (d->Buffer == IntPtr.Zero || d->BufferLength < bytes.Length) return ESIF_E_NEED_LARGER_BUFFER;
        Marshal.Copy(bytes, 0, d->Buffer, bytes.Length);
        d->Type = ESIF_DATA_STRING;
        return ESIF_OK;
    }

    // ---- callbacks the Intel service calls (on its own threads) ----
    // [UnmanagedCallersOnly] makes a C# method callable from native code without a
    // delegate the garbage collector could move or free. Exceptions must never escape.

    [UnmanagedCallersOnly]
    static int OnAppCreate(byte* ifaceSet, ulong esif, ulong* appHandle, IntPtr appData, int initialState)
    {
        try
        {
            var self = instance;
            if (self is null || ifaceSet == null || appHandle == null) return ESIF_E_NOT_SUPPORTED;
            *appHandle = 0x4D494E49; // any value other than 0 / -1 ("MINI")
            self.esifHandle = esif;
            self.primitive = (delegate* unmanaged<ulong, ulong, ulong, EsifData*, EsifData*, uint, byte, int>)
                *(IntPtr*)(ifaceSet + OffEsifPrimitive);
            return self.primitive == null ? ESIF_E_NOT_SUPPORTED : ESIF_OK;
        }
        catch
        {
            return ESIF_E_NOT_SUPPORTED;
        }
    }

    [UnmanagedCallersOnly]
    static int OnParticipantCreate(ulong app, ulong participant, byte* data, int state)
    {
        try
        {
            var self = instance;
            if (self is null || data == null) return ESIF_E_NOT_SUPPORTED;
            var p = new Participant(participant, ReadString(data + PartName), ReadString(data + PartDesc),
                ReadString(data + PartAcpiScope), *(uint*)(data + PartBus));
            lock (self.gate) self.participants[participant] = p;
            return ESIF_OK;
        }
        catch
        {
            return ESIF_E_NOT_SUPPORTED;
        }
    }

    [UnmanagedCallersOnly]
    static int OnParticipantDestroy(ulong app, ulong participant)
    {
        var self = instance;
        if (self is null) return ESIF_OK;
        lock (self.gate)
        {
            self.participants.Remove(participant);
            self.domains.RemoveAll(d => d.Participant == participant);
        }
        return ESIF_OK;
    }

    [UnmanagedCallersOnly]
    static int OnDomainCreate(ulong app, ulong participant, ulong domain, byte* data, int state)
    {
        try
        {
            var self = instance;
            if (self is null || data == null) return ESIF_E_NOT_SUPPORTED;
            var d = new Domain(participant, domain, ReadString(data + DomName), *(uint*)(data + DomType));
            lock (self.gate)
            {
                self.domains.RemoveAll(x => x.Participant == participant && x.Handle == domain);
                self.domains.Add(d);
            }
            return ESIF_OK;
        }
        catch
        {
            return ESIF_E_NOT_SUPPORTED;
        }
    }

    [UnmanagedCallersOnly]
    static int OnDomainDestroy(ulong app, ulong participant, ulong domain)
    {
        var self = instance;
        if (self is not null)
            lock (self.gate) self.domains.RemoveAll(x => x.Participant == participant && x.Handle == domain);
        return ESIF_OK;
    }

    [UnmanagedCallersOnly]
    static int OnGetName(EsifData* d) => WriteString(d, "SAWNTASK");

    [UnmanagedCallersOnly]
    static int OnGetVersion(EsifData* d) => WriteString(d, "1.0.0");

    [UnmanagedCallersOnly]
    static int OnGetDescription(EsifData* d) => WriteString(d, "SawnTask temperature reader");

    [UnmanagedCallersOnly]
    static int OnGetIntro(ulong app, EsifData* d) => WriteString(d, "SawnTask");

    [UnmanagedCallersOnly]
    static int OnOk1(ulong a) => ESIF_OK;

    [UnmanagedCallersOnly]
    static int OnNotSupported(ulong a) => ESIF_E_NOT_SUPPORTED;
}
