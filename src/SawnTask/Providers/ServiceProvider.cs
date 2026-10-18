using System.ComponentModel;
using System.Runtime.InteropServices;
using SawnTask.Core;
using static SawnTask.Native.NativeMethods;

namespace SawnTask.Providers;

// OpenSCManagerW + EnumServicesStatusExW: the Service Control Manager inventory,
// read on the four-second slow tier.
public static class ServiceProvider
{
    static readonly string[] States = ["?", "Stopped", "Starting", "Stopping", "Running", "Continuing", "Pausing", "Paused"];

    public static List<ServiceItem> Read()
    {
        IntPtr scm = OpenSCManagerW(null, null, SC_MANAGER_ENUMERATE_SERVICE);
        if (scm == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());

        var items = new List<ServiceItem>();
        IntPtr buffer = IntPtr.Zero;
        try
        {
            uint resume = 0, size = 0;
            // First call with no buffer tells us how many bytes are needed.
            EnumServicesStatusExW(scm, SC_ENUM_PROCESS_INFO, SERVICE_WIN32, SERVICE_STATE_ALL, IntPtr.Zero, 0, out uint needed, out _, ref resume, null);
            while (true)
            {
                if (needed > size)
                {
                    if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
                    size = needed + 4096;
                    buffer = Marshal.AllocHGlobal((int)size);
                }
                bool ok = EnumServicesStatusExW(scm, SC_ENUM_PROCESS_INFO, SERVICE_WIN32, SERVICE_STATE_ALL, buffer, size, out needed, out uint count, ref resume, null);
                int error = Marshal.GetLastWin32Error();
                if (!ok && error != ERROR_MORE_DATA) throw new Win32Exception(error);

                int stride = Marshal.SizeOf<ENUM_SERVICE_STATUS_PROCESSW>();
                for (int i = 0; i < count; i++)
                {
                    var entry = Marshal.PtrToStructure<ENUM_SERVICE_STATUS_PROCESSW>(buffer + i * stride);
                    items.Add(new ServiceItem(
                        Marshal.PtrToStringUni(entry.lpServiceName) ?? "",
                        ResolveIndirect(Marshal.PtrToStringUni(entry.lpDisplayName) ?? ""),
                        entry.dwCurrentState < States.Length ? States[entry.dwCurrentState] : entry.dwCurrentState.ToString(),
                        entry.dwProcessId,
                        (entry.dwServiceType & 0x20) != 0 ? "Shared process" : "Own process"));
                }
                if (ok) break;
            }
        }
        finally
        {
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
            CloseServiceHandle(scm);
        }
        return items.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
