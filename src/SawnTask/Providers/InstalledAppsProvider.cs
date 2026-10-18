using System.Globalization;
using Microsoft.Win32;
using SawnTask.Core;

namespace SawnTask.Providers;

// The Installed Apps page: name, publisher, version, install date, estimated
// size, scope (machine / current user) and source (MSI / registry). These are the
// "Uninstall" registry keys that Settings > Apps also reads. No admin needed.
// (Store/MSIX apps live elsewhere and are not read here.)
public static class InstalledAppsProvider
{
    const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    const string Uninstall32Key = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall";

    public static List<InstalledApp> Read()
    {
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var hkcu = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);

        var apps = new Dictionary<string, InstalledApp>(StringComparer.OrdinalIgnoreCase);
        ReadHive(apps, hklm, UninstallKey, "Machine");
        ReadHive(apps, hklm, Uninstall32Key, "Machine (32-bit)");
        ReadHive(apps, hkcu, UninstallKey, "Current user");
        return apps.Values.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    static void ReadHive(Dictionary<string, InstalledApp> apps, RegistryKey hive, string path, string scope)
    {
        using var root = hive.OpenSubKey(path);
        if (root is null) return;
        foreach (string subName in root.GetSubKeyNames())
        {
            using var key = root.OpenSubKey(subName);
            if (key?.GetValue("DisplayName") is not string name || name.Trim().Length == 0) continue;
            // Settings > Apps hides these too: components and updates that belong to another app.
            if (key.GetValue("SystemComponent") is int sc && sc == 1) continue;
            if (key.GetValue("ParentKeyName") is string) continue;

            string version = key.GetValue("DisplayVersion")?.ToString() ?? "";
            long? size = key.GetValue("EstimatedSize") is int kb && kb > 0 ? kb * 1024L : null; // stored in KB
            bool msi = key.GetValue("WindowsInstaller") is int wi && wi == 1;
            var app = new InstalledApp(name.Trim(), key.GetValue("Publisher")?.ToString() ?? "", version,
                FormatDate(key.GetValue("InstallDate")?.ToString()), size, scope, msi ? "MSI" : "Registry");
            apps.TryAdd($"{name}|{version}", app); // the same app can be listed in two views
        }
    }

    // InstallDate is usually "yyyyMMdd".
    static string FormatDate(string? raw) =>
        raw is not null && DateTime.TryParseExact(raw, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d.ToString("yyyy-MM-dd")
            : Fmt.Missing;
}
