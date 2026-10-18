using System.Runtime.InteropServices;

namespace SawnTask.Native;

// Which processes count as "Apps" (the first group on the Processes page, as in
// Windows Task Manager): those that own a real, visible top-level window.
public static class AppWindows
{
    delegate bool EnumWindowsProc(IntPtr window, IntPtr state);

    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc callback, IntPtr state);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr window, uint command);
    [DllImport("user32.dll")] static extern int GetWindowTextLengthW(IntPtr window);
    [DllImport("user32.dll")] static extern IntPtr GetWindowLongPtrW(IntPtr window, int index);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);

    const uint GW_OWNER = 4;
    const int GWL_EXSTYLE = -20;
    const long WS_EX_TOOLWINDOW = 0x80;
    const int DWMWA_CLOAKED = 14; // hidden Store-app frames and windows on other virtual desktops

    public static HashSet<uint> ProcessIds()
    {
        var pids = new HashSet<uint>();
        EnumWindows((window, _) =>
        {
            if (!IsWindowVisible(window) || GetWindow(window, GW_OWNER) != IntPtr.Zero) return true; // owned = dialogs, popups
            if (GetWindowTextLengthW(window) == 0) return true;                                      // untitled helper windows
            if ((GetWindowLongPtrW(window, GWL_EXSTYLE).ToInt64() & WS_EX_TOOLWINDOW) != 0) return true;
            if (DwmGetWindowAttribute(window, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
            GetWindowThreadProcessId(window, out uint pid);
            pids.Add(pid);
            return true;
        }, IntPtr.Zero);
        return pids;
    }
}
