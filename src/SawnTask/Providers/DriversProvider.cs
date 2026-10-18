using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using SawnTask.Core;
using static SawnTask.Native.NativeMethods;

namespace SawnTask.Providers;

// The Drivers page. Kernel drivers are registered as services of type
// SERVICE_DRIVER, so the same Service Control Manager calls list them; the version and
// vendor come from the .sys file's version resource (VERSION.dll).
public static class DriversProvider
{
    static readonly string[] States = ["?", "Stopped", "Starting", "Stopping", "Running", "Continuing", "Pausing", "Paused"];
    static readonly string[] StartTypes = ["Boot", "System", "Automatic", "Manual", "Disabled"];

    // Reading a file's version resource is the slow part; files rarely change.
    static readonly Dictionary<string, (string Version, string Vendor)> FileInfoCache = new(StringComparer.OrdinalIgnoreCase);

    public static List<DriverItem> Read()
    {
        IntPtr scm = OpenSCManagerW(null, null, SC_MANAGER_ENUMERATE_SERVICE);
        if (scm == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        var list = new List<DriverItem>();
        IntPtr buffer = IntPtr.Zero;
        try
        {
            uint resume = 0;
            EnumServicesStatusExW(scm, SC_ENUM_PROCESS_INFO, SERVICE_DRIVER, SERVICE_STATE_ALL, IntPtr.Zero, 0, out uint needed, out _, ref resume, null);
            buffer = Marshal.AllocHGlobal((int)needed + 4096);
            if (!EnumServicesStatusExW(scm, SC_ENUM_PROCESS_INFO, SERVICE_DRIVER, SERVICE_STATE_ALL, buffer, needed + 4096, out _, out uint count, ref resume, null))
                throw new Win32Exception(Marshal.GetLastWin32Error());

            int stride = Marshal.SizeOf<ENUM_SERVICE_STATUS_PROCESSW>();
            for (int i = 0; i < count; i++)
            {
                var e = Marshal.PtrToStructure<ENUM_SERVICE_STATUS_PROCESSW>(buffer + i * stride);
                string name = Marshal.PtrToStringUni(e.lpServiceName) ?? "";
                var (startType, path) = QueryConfig(scm, name);
                var (version, vendor) = FileInfo(path);
                list.Add(new DriverItem(
                    ResolveIndirect(Marshal.PtrToStringUni(e.lpDisplayName) ?? name),
                    name,
                    e.dwCurrentState < States.Length ? States[e.dwCurrentState] : e.dwCurrentState.ToString(),
                    startType,
                    (e.dwServiceType & 0x2) != 0 ? "File system" : "Kernel",
                    version,
                    vendor,
                    path));
            }
        }
        finally
        {
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
            CloseServiceHandle(scm);
        }
        return list.OrderBy(d => d.State == "Running" ? 0 : 1).ThenBy(d => d.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    static (string StartType, string Path) QueryConfig(IntPtr scm, string name)
    {
        IntPtr service = OpenServiceW(scm, name, SERVICE_QUERY_CONFIG);
        if (service == IntPtr.Zero) return (Fmt.Missing, "");
        IntPtr config = IntPtr.Zero;
        try
        {
            QueryServiceConfigW(service, IntPtr.Zero, 0, out uint needed);
            if (needed == 0) return (Fmt.Missing, "");
            config = Marshal.AllocHGlobal((int)needed);
            if (!QueryServiceConfigW(service, config, needed, out _)) return (Fmt.Missing, "");
            var c = Marshal.PtrToStructure<QUERY_SERVICE_CONFIGW>(config);
            string start = c.dwStartType < StartTypes.Length ? StartTypes[c.dwStartType] : c.dwStartType.ToString();
            return (start, NormalizePath(Marshal.PtrToStringUni(c.lpBinaryPathName) ?? ""));
        }
        finally
        {
            if (config != IntPtr.Zero) Marshal.FreeHGlobal(config);
            CloseServiceHandle(service);
        }
    }

    // Driver paths come in kernel forms: "\SystemRoot\System32\drivers\x.sys",
    // "System32\drivers\x.sys" or "\??\C:\...". Turn them into normal paths.
    static string NormalizePath(string raw)
    {
        string p = raw.Trim().Trim('"');
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (p.StartsWith(@"\??\", StringComparison.Ordinal)) p = p[4..];
        if (p.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase)) p = Path.Combine(windows, p[12..]);
        else if (p.StartsWith(@"System32\", StringComparison.OrdinalIgnoreCase)) p = Path.Combine(windows, p);
        return p;
    }

    static (string Version, string Vendor) FileInfo(string path)
    {
        if (path.Length == 0) return (Fmt.Missing, Fmt.Missing);
        if (FileInfoCache.TryGetValue(path, out var cached)) return cached;
        (string, string) result = (Fmt.Missing, Fmt.Missing);
        try
        {
            if (File.Exists(path))
            {
                var v = FileVersionInfo.GetVersionInfo(path);
                result = (
                    v.FileMajorPart + v.FileMinorPart + v.FileBuildPart + v.FilePrivatePart > 0
                        ? $"{v.FileMajorPart}.{v.FileMinorPart}.{v.FileBuildPart}.{v.FilePrivatePart}"
                        : Fmt.Missing,
                    string.IsNullOrWhiteSpace(v.CompanyName) ? Fmt.Missing : v.CompanyName.Trim());
            }
        }
        catch
        {
            // Unreadable file: leave the dashes.
        }
        FileInfoCache[path] = result;
        return result;
    }
}
