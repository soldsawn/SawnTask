using System.IO;
using System.Runtime.InteropServices;

namespace SawnTask.Core;

// Moves a file or folder to the Recycle Bin with the shell's own delete operation (the
// same one Explorer uses), so it can be restored from the Recycle Bin.
public static class RecycleBin
{
    const uint FO_DELETE = 3;
    const ushort FOF_NOCONFIRMATION = 0x0010;  // we already asked the user ourselves
    const ushort FOF_ALLOWUNDO = 0x0040;       // Recycle Bin instead of permanent delete
    const ushort FOF_WANTNUKEWARNING = 0x4000; // warn if it is too big for the Recycle Bin and would be deleted for good

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SHFILEOPSTRUCTW
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;   // list of paths, each ending in \0, the list ending in an extra \0
        public string? pTo;
        public ushort fFlags;
        public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHFileOperationW(ref SHFILEOPSTRUCTW operation);

    // Returns true when the item is gone. "owner" is the window the shell's own dialogs
    // (progress, too-big-for-Recycle-Bin warning, errors) belong to.
    public static bool Move(string path, IntPtr owner, out string error)
    {
        var op = new SHFILEOPSTRUCTW
        {
            hwnd = owner,
            wFunc = FO_DELETE,
            pFrom = path + "\0", // the marshaller adds the final \0
            fFlags = FOF_NOCONFIRMATION | FOF_ALLOWUNDO | FOF_WANTNUKEWARNING,
        };
        int rc = SHFileOperationW(ref op);
        if (op.fAnyOperationsAborted)
        {
            error = "Cancelled.";
            return false;
        }
        if (rc != 0 || File.Exists(path) || Directory.Exists(path))
        {
            error = rc == 0 ? "Some items could not be moved (in use or no permission)." : $"Windows refused the operation (code 0x{rc:X}). The file may be in use or need administrator rights.";
            return false;
        }
        error = "";
        return true;
    }
}
