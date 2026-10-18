using System.Runtime.InteropServices;
using System.Security.Principal;
using SawnTask.Core;
using static SawnTask.Native.NativeMethods;

namespace SawnTask.Providers;

// The "User name" column and each process's executable path (for its icon). The process
// table has neither, so for each NEW process we open it with minimum rights, read its image
// path and its token's user SID, and translate the SID to a name (OpenProcessToken,
// GetTokenInformation and LookupAccountSidW). Protected processes refuse: they show
// "Access denied". Results are cached per
// ProcessKey, so each process costs this work only once.
public sealed class ProcessOwners
{
    public sealed record Identity(string User, string ImagePath);

    readonly Dictionary<ProcessKey, Identity> byProcess = [];
    readonly Dictionary<string, string> bySid = [];

    public Identity Get(ProcessKey key)
    {
        if (byProcess.TryGetValue(key, out var cached)) return cached;
        var identity = key.Pid is 0 or 4 ? new Identity("SYSTEM", "") : Lookup(key.Pid);
        byProcess[key] = identity;
        return identity;
    }

    public void Forget(IEnumerable<ProcessKey> alive)
    {
        var keep = alive.ToHashSet();
        foreach (var key in byProcess.Keys.Where(k => !keep.Contains(k)).ToList()) byProcess.Remove(key);
    }

    Identity Lookup(uint pid)
    {
        IntPtr process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == IntPtr.Zero) return new Identity("Access denied", "");
        try
        {
            return new Identity(User(process), ImagePath(process));
        }
        finally
        {
            CloseHandle(process);
        }
    }

    // Many system processes allow reading the path even when their token is off limits.
    static string ImagePath(IntPtr process)
    {
        var buffer = new char[1024];
        uint size = (uint)buffer.Length;
        return QueryFullProcessImageNameW(process, 0, buffer, ref size) ? new string(buffer, 0, (int)size) : "";
    }

    string User(IntPtr process)
    {
        IntPtr token = IntPtr.Zero, info = IntPtr.Zero;
        try
        {
            if (!OpenProcessToken(process, TOKEN_QUERY, out token)) return "Access denied";
            GetTokenInformation(token, TokenUser, IntPtr.Zero, 0, out uint size);
            if (size == 0) return "Access denied";
            info = Marshal.AllocHGlobal((int)size);
            if (!GetTokenInformation(token, TokenUser, info, size, out _)) return "Access denied";

            // TOKEN_USER starts with SID_AND_ATTRIBUTES, whose first field is the SID pointer.
            var sid = new SecurityIdentifier(Marshal.ReadIntPtr(info));
            string text = sid.Value;
            if (!bySid.TryGetValue(text, out var name))
            {
                try
                {
                    string account = sid.Translate(typeof(NTAccount)).Value; // "NT AUTHORITY\SYSTEM", "PC\alice"
                    name = account[(account.LastIndexOf('\\') + 1)..];
                }
                catch (IdentityNotMappedException)
                {
                    name = text;
                }
                bySid[text] = name;
            }
            return name;
        }
        catch
        {
            return "Access denied";
        }
        finally
        {
            if (info != IntPtr.Zero) Marshal.FreeHGlobal(info);
            if (token != IntPtr.Zero) CloseHandle(token);
        }
    }
}
