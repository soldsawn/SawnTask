using System.Runtime.InteropServices;

namespace SawnTask.Native;

// The Win32 and native functions the providers call, grouped by DLL.
internal static class NativeMethods
{
    // ---- ntdll: the "native" process and CPU tables ----

    internal const int SystemProcessInformation = 5;
    internal const int SystemProcessorPerformanceInformation = 8;
    internal const uint STATUS_SUCCESS = 0;
    internal const uint STATUS_INFO_LENGTH_MISMATCH = 0xC0000004;

    [DllImport("ntdll.dll")]
    internal static extern uint NtQuerySystemInformation(int infoClass, IntPtr buffer, uint length, out uint returnLength);

    // ---- dwmapi: title bar colours (Windows 11; older Windows ignores the colour ones) ----

    internal const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    internal const int DWMWA_CAPTION_COLOR = 35;
    internal const int DWMWA_TEXT_COLOR = 36;

    [DllImport("dwmapi.dll")]
    internal static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    // ---- kernel32: whole-system CPU and memory ----

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool GetSystemTimes(out long idleTime, out long kernelTime, out long userTime);

    [StructLayout(LayoutKind.Sequential)]
    internal struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);

    [StructLayout(LayoutKind.Sequential)]
    internal struct PERFORMANCE_INFORMATION
    {
        public uint cb;
        public nuint CommitTotal;
        public nuint CommitLimit;
        public nuint CommitPeak;
        public nuint PhysicalTotal;
        public nuint PhysicalAvailable;
        public nuint SystemCache;
        public nuint KernelTotal;
        public nuint KernelPaged;
        public nuint KernelNonpaged;
        public nuint PageSize;
        public uint HandleCount;
        public uint ProcessCount;
        public uint ThreadCount;
    }

    [DllImport("kernel32.dll", EntryPoint = "K32GetPerformanceInfo", SetLastError = true)]
    internal static extern bool GetPerformanceInfo(out PERFORMANCE_INFORMATION info, uint size);

    // ---- kernel32: acting on one process (End task) ----

    internal const uint PROCESS_TERMINATE = 0x0001;
    internal const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr OpenProcess(uint access, bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool TerminateProcess(IntPtr process, uint exitCode);

    [DllImport("kernel32.dll")]
    internal static extern bool CloseHandle(IntPtr handle);

    // ---- pdh: performance counters (disk, GPU engines) ----

    internal const uint PDH_FMT_DOUBLE = 0x00000200;
    internal const uint PDH_FMT_NOCAP100 = 0x00008000;
    internal const uint PDH_MORE_DATA = 0x800007D2;
    internal const uint PDH_CSTATUS_NEW_DATA = 1;

    [StructLayout(LayoutKind.Explicit)]
    internal struct PDH_FMT_COUNTERVALUE
    {
        [FieldOffset(0)] public uint CStatus;
        [FieldOffset(8)] public double DoubleValue;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    internal static extern uint PdhOpenQueryW(string? dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    internal static extern uint PdhAddEnglishCounterW(IntPtr query, string counterPath, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    internal static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll")]
    internal static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, out uint type, out PDH_FMT_COUNTERVALUE value);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    internal static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint bufferSize, out uint itemCount, IntPtr itemBuffer);

    [DllImport("pdh.dll")]
    internal static extern uint PdhCloseQuery(IntPtr query);

    // ---- advapi32: Service Control Manager ----

    internal const uint SC_MANAGER_ENUMERATE_SERVICE = 0x0004;
    internal const int SC_ENUM_PROCESS_INFO = 0;
    internal const uint SERVICE_WIN32 = 0x00000030;
    internal const uint SERVICE_STATE_ALL = 0x00000003;
    internal const int ERROR_MORE_DATA = 234;

    [StructLayout(LayoutKind.Sequential)]
    internal struct ENUM_SERVICE_STATUS_PROCESSW
    {
        public IntPtr lpServiceName;
        public IntPtr lpDisplayName;
        public uint dwServiceType;
        public uint dwCurrentState;
        public uint dwControlsAccepted;
        public uint dwWin32ExitCode;
        public uint dwServiceSpecificExitCode;
        public uint dwCheckPoint;
        public uint dwWaitHint;
        public uint dwProcessId;
        public uint dwServiceFlags;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr OpenSCManagerW(string? machineName, string? databaseName, uint desiredAccess);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool EnumServicesStatusExW(IntPtr scManager, int infoLevel, uint serviceType, uint serviceState,
        IntPtr services, uint bufSize, out uint bytesNeeded, out uint servicesReturned, ref uint resumeHandle, string? groupName);

    [DllImport("advapi32.dll", SetLastError = true)]
    internal static extern bool CloseServiceHandle(IntPtr handle);

    internal const uint SERVICE_DRIVER = 0x0000000B;
    internal const uint SERVICE_QUERY_CONFIG = 0x0001;
    internal const uint SERVICE_START = 0x0010, SERVICE_STOP = 0x0020;

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr OpenServiceW(IntPtr scManager, string serviceName, uint desiredAccess);

    // QUERY_SERVICE_CONFIGW: dwServiceType, dwStartType, dwErrorControl, then string pointers.
    [StructLayout(LayoutKind.Sequential)]
    internal struct QUERY_SERVICE_CONFIGW
    {
        public uint dwServiceType;
        public uint dwStartType;
        public uint dwErrorControl;
        public IntPtr lpBinaryPathName;
        public IntPtr lpLoadOrderGroup;
        public uint dwTagId;
        public IntPtr lpDependencies;
        public IntPtr lpServiceStartName;
        public IntPtr lpDisplayName;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool QueryServiceConfigW(IntPtr service, IntPtr config, uint bufSize, out uint bytesNeeded);

    // ---- iphlpapi: TCP/UDP tables with the owning process (Connections page) ----

    internal const int AF_INET = 2, AF_INET6 = 23;
    internal const int TCP_TABLE_OWNER_PID_ALL = 5;
    internal const int UDP_TABLE_OWNER_PID = 1;

    [DllImport("iphlpapi.dll")]
    internal static extern uint GetExtendedTcpTable(IntPtr table, ref uint size, bool sort, int family, int tableClass, uint reserved);

    [DllImport("iphlpapi.dll")]
    internal static extern uint GetExtendedUdpTable(IntPtr table, ref uint size, bool sort, int family, int tableClass, uint reserved);

    // ---- wtsapi32: logon sessions (Users page) ----

    internal const int WTSUserName = 5, WTSDomainName = 7, WTSClientName = 10;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct WTS_SESSION_INFOW
    {
        public uint SessionId;
        public IntPtr pWinStationName;
        public int State;
    }

    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool WTSEnumerateSessionsW(IntPtr server, uint reserved, uint version, out IntPtr sessions, out uint count);

    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool WTSQuerySessionInformationW(IntPtr server, uint sessionId, int infoClass, out IntPtr buffer, out uint bytes);

    [DllImport("wtsapi32.dll")]
    internal static extern void WTSFreeMemory(IntPtr memory);

    // ---- process identity and actions ----

    internal const uint PROCESS_SET_INFORMATION = 0x0200;
    internal const uint PROCESS_SUSPEND_RESUME = 0x0800;
    internal const uint TOKEN_QUERY = 0x0008;
    internal const int TokenUser = 1;

    [DllImport("advapi32.dll", SetLastError = true)]
    internal static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    internal static extern bool GetTokenInformation(IntPtr token, int infoClass, IntPtr info, uint length, out uint returnLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, char[] name, ref uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool SetPriorityClass(IntPtr process, uint priorityClass);

    // Returns 0 on failure.
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern uint GetPriorityClass(IntPtr process);

    [DllImport("ntdll.dll")]
    internal static extern uint NtSuspendProcess(IntPtr process);

    [DllImport("ntdll.dll")]
    internal static extern uint NtResumeProcess(IntPtr process);

    // Resolves "@%SystemRoot%\system32\x.dll,-101" style names to the localized text.
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    static extern int SHLoadIndirectString(string source, System.Text.StringBuilder output, uint outputSize, IntPtr reserved);

    internal static string ResolveIndirect(string text)
    {
        if (!text.StartsWith('@')) return text;
        var output = new System.Text.StringBuilder(512);
        return SHLoadIndirectString(text, output, (uint)output.Capacity, IntPtr.Zero) == 0 ? output.ToString() : text;
    }
}
