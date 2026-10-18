using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SawnTask.Ui;

// Small (16 px) program icons for the Processes page, read through the shell like
// Explorer does (SHGetFileInfo). One lookup per .exe path, then
// cached. Processes whose path Windows does not reveal get the generic program icon.
public static class IconCache
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SHFILEINFOW
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SHGetFileInfoW(string path, uint fileAttributes, ref SHFILEINFOW info, uint size, uint flags);

    [DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr icon);

    const uint SHGFI_ICON = 0x100, SHGFI_SMALLICON = 0x1, SHGFI_USEFILEATTRIBUTES = 0x10;
    const uint FILE_ATTRIBUTE_NORMAL = 0x80;

    static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static ImageSource? For(string imagePath)
    {
        string key = imagePath.Length == 0 ? "*.exe" : imagePath;
        if (Cache.TryGetValue(key, out var cached)) return cached;
        var icon = imagePath.Length == 0
            ? Load(".exe", FILE_ATTRIBUTE_NORMAL, SHGFI_USEFILEATTRIBUTES) // "an .exe file" in general
            : Load(imagePath, 0, 0) ?? For("");
        Cache[key] = icon;
        return icon;
    }

    static ImageSource? Load(string path, uint attributes, uint extraFlags)
    {
        var info = new SHFILEINFOW();
        SHGetFileInfoW(path, attributes, ref info, (uint)Marshal.SizeOf<SHFILEINFOW>(), SHGFI_ICON | SHGFI_SMALLICON | extraFlags);
        if (info.hIcon == IntPtr.Zero) return null;
        try
        {
            var image = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
        finally
        {
            DestroyIcon(info.hIcon);
        }
    }
}
