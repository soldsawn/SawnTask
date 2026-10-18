using System.ComponentModel;
using System.Runtime.InteropServices;
using SawnTask.Core;
using static SawnTask.Native.NativeMethods;

namespace SawnTask.Providers;

// Logon sessions from WTSEnumerateSessionsW and WTSQuerySessionInformationW, read on
// the four-second slow tier; the Users page then adds up the
// CPU and memory of every process in each session.
public static class SessionsProvider
{
    static readonly string[] States =
        ["Active", "Connected", "Connect query", "Shadow", "Disconnected", "Idle", "Listen", "Reset", "Down", "Init"];

    public static List<UserSession> Read()
    {
        if (!WTSEnumerateSessionsW(IntPtr.Zero, 0, 1, out IntPtr sessions, out uint count))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        var list = new List<UserSession>();
        try
        {
            int size = Marshal.SizeOf<WTS_SESSION_INFOW>();
            for (int i = 0; i < count; i++)
            {
                var info = Marshal.PtrToStructure<WTS_SESSION_INFOW>(sessions + i * size);
                string user = Query(info.SessionId, WTSUserName);
                if (user.Length == 0) continue; // services session 0, listeners: no user
                string domain = Query(info.SessionId, WTSDomainName);
                list.Add(new UserSession(
                    info.SessionId,
                    domain.Length > 0 ? $@"{domain}\{user}" : user,
                    info.State >= 0 && info.State < States.Length ? States[info.State] : info.State.ToString(),
                    Marshal.PtrToStringUni(info.pWinStationName) ?? "",
                    Query(info.SessionId, WTSClientName)));
            }
        }
        finally
        {
            WTSFreeMemory(sessions);
        }
        return list;
    }

    static string Query(uint session, int infoClass)
    {
        if (!WTSQuerySessionInformationW(IntPtr.Zero, session, infoClass, out IntPtr buffer, out _)) return "";
        try
        {
            return Marshal.PtrToStringUni(buffer) ?? "";
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }
}
