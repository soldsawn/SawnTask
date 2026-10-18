using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using SawnTask.Native;

namespace SawnTask.Core;

public static class ProcessActions
{
    // Priority classes for SetPriorityClass (the same menu as Windows Task Manager).
    // The first value is the text key in Strings/*.xaml.
    public static readonly (string TextKey, uint Value)[] Priorities =
    [
        ("Prio.Realtime", 0x100), ("Prio.High", 0x80), ("Prio.AboveNormal", 0x8000), ("Prio.Normal", 0x20),
        ("Prio.BelowNormal", 0x4000), ("Prio.Low", 0x40),
    ];

    public static bool TryEndTask(ProcessKey key, out string error) =>
        WithProcess(key, NativeMethods.PROCESS_TERMINATE, h => NativeMethods.TerminateProcess(h, 1) ? null : LastError(), out error);

    // NtSuspendProcess / NtResumeProcess: undocumented but exported by ntdll on every current Windows version.
    public static bool TrySuspend(ProcessKey key, bool suspend, out string error) =>
        WithProcess(key, NativeMethods.PROCESS_SUSPEND_RESUME, h =>
        {
            uint status = suspend ? NativeMethods.NtSuspendProcess(h) : NativeMethods.NtResumeProcess(h);
            return status == 0 ? null : $"NTSTATUS 0x{status:X8}";
        }, out error);

    public static bool TrySetPriority(ProcessKey key, uint priorityClass, out string error) =>
        WithProcess(key, NativeMethods.PROCESS_SET_INFORMATION, h => NativeMethods.SetPriorityClass(h, priorityClass) ? null : LastError(), out error);

    // The priority class the process has right now, or null when Windows does not let us read it.
    public static uint? GetPriority(ProcessKey key)
    {
        uint current = 0;
        bool ok = WithProcess(key, 0, h =>
        {
            current = NativeMethods.GetPriorityClass(h);
            return current != 0 ? null : LastError();
        }, out _);
        return ok ? current : null;
    }

    public static bool TryOpenFileLocation(ProcessKey key, out string error)
    {
        string? path = null;
        bool ok = WithProcess(key, 0, h =>
        {
            var buffer = new char[32768];
            uint size = (uint)buffer.Length;
            if (!NativeMethods.QueryFullProcessImageNameW(h, 0, buffer, ref size)) return LastError();
            path = new string(buffer, 0, (int)size);
            return null;
        }, out error);
        if (!ok || path is null) return false;
        Process.Start("explorer.exe", $"/select,\"{path}\"");
        return true;
    }

    // Every action follows the same rule: open with the minimum rights it needs, check that
    // the process still has the creation time of the row the user clicked (Windows reuses
    // PIDs), and only then act. "action" returns null on success or an error message.
    static bool WithProcess(ProcessKey key, uint access, Func<IntPtr, string?> action, out string error)
    {
        IntPtr handle = NativeMethods.OpenProcess(access | NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, key.Pid);
        if (handle == IntPtr.Zero)
        {
            error = LastError();
            return false;
        }

        try
        {
            if (!NativeMethods.GetProcessTimes(handle, out long created, out _, out _, out _))
            {
                error = LastError();
                return false;
            }
            if (created != key.CreateTime)
            {
                error = "That PID now belongs to a different process (Windows reused it). Nothing was changed.";
                return false;
            }
            error = action(handle) ?? "";
            return error.Length == 0;
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }

    static string LastError() => new Win32Exception(Marshal.GetLastWin32Error()).Message;
}
