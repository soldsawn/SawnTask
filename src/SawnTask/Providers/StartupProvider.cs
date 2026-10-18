using System.IO;
using Microsoft.Win32;
using SawnTask.Core;

namespace SawnTask.Providers;

// HKCU/HKLM Run keys, StartupApproved state, and per-user/all-users Startup
// folders, read on the four-second slow tier. Enable/Disable writes the same StartupApproved
// value Windows Task Manager writes; the Run entry itself is never touched.
public static class StartupProvider
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string Run32Key = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";
    const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";

    public static List<StartupItem> Read()
    {
        using var hkcu = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);

        var items = new List<StartupItem>();
        ReadRunKey(items, hkcu, RunKey, "HKCU", "Run", @"HKCU\...\Run");
        ReadRunKey(items, hklm, RunKey, "HKLM", "Run", @"HKLM\...\Run");
        ReadRunKey(items, hklm, Run32Key, "HKLM", "Run32", @"HKLM\WOW6432Node\...\Run");
        ReadFolder(items, Environment.GetFolderPath(Environment.SpecialFolder.Startup), "HKCU", "Startup folder (you)");
        ReadFolder(items, Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), "HKLM", "Startup folder (all users)");
        return items.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    static RegistryKey Hive(string name) =>
        RegistryKey.OpenBaseKey(name == "HKCU" ? RegistryHive.CurrentUser : RegistryHive.LocalMachine, RegistryView.Registry64);

    static void ReadRunKey(List<StartupItem> items, RegistryKey hive, string path, string approvedHive, string approvedSub, string label)
    {
        using var key = hive.OpenSubKey(path);
        if (key is null) return;
        using var approvedRoot = Hive(approvedHive);
        using var approved = approvedRoot.OpenSubKey(ApprovedKey + approvedSub);
        foreach (var name in key.GetValueNames())
        {
            if (name.Length == 0) continue;
            items.Add(new StartupItem(IsEnabled(approved, name), name, key.GetValue(name)?.ToString() ?? "", label,
                approvedHive, ApprovedKey + approvedSub, name));
        }
    }

    static void ReadFolder(List<StartupItem> items, string folder, string approvedHive, string label)
    {
        if (!Directory.Exists(folder)) return;
        using var approvedRoot = Hive(approvedHive);
        using var approved = approvedRoot.OpenSubKey(ApprovedKey + "StartupFolder");
        foreach (var file in Directory.EnumerateFiles(folder))
        {
            string name = Path.GetFileName(file);
            if (name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
            items.Add(new StartupItem(IsEnabled(approved, name), Path.GetFileNameWithoutExtension(file), file, label,
                approvedHive, ApprovedKey + "StartupFolder", name));
        }
    }

    // StartupApproved holds a 12-byte value per entry. The first byte is even
    // (0x02, 0x06) when enabled and odd (0x03, 0x07) when the user disabled it
    // in Task Manager. No value at all means enabled.
    static bool IsEnabled(RegistryKey? approved, string name) =>
        approved?.GetValue(name) is not byte[] { Length: > 0 } data || (data[0] & 1) == 0;

    // Writes the flag the way Windows Task Manager does: 02 + 11 zero bytes for enabled,
    // 03 + 3 zero bytes + the FILETIME of the change for disabled. HKLM entries need admin.
    public static void SetEnabled(StartupItem item, bool enabled)
    {
        var value = new byte[12];
        value[0] = enabled ? (byte)0x02 : (byte)0x03;
        if (!enabled) BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()).CopyTo(value, 4);

        using var root = Hive(item.ApprovedHive);
        try
        {
            using var key = root.CreateSubKey(item.ApprovedKey, writable: true);
            key.SetValue(item.ApprovedValue, value, RegistryValueKind.Binary);
        }
        catch (UnauthorizedAccessException)
        {
            throw new UnauthorizedAccessException($"{item.Name} is a machine-wide entry ({item.ApprovedHive}); changing it needs administrator rights.");
        }
    }
}
