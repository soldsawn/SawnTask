using System.IO;
using System.Text.Json;

namespace SawnTask.Core;

// Settings live in %LOCALAPPDATA%\SawnTask\settings.json.
public sealed class Settings
{
    public int UpdateSpeedMs { get; set; } = 1000;
    public string Theme { get; set; } = "Dark";
    public string? Language { get; set; } // null: follow the Windows display language
    public int GraphFps { get; set; } = 60;       // 0 = redraw graphs only when new data arrives
    public bool SmoothCurves { get; set; } = true;
    public int StartPage { get; set; }            // page index (TabItem order in MainWindow.xaml)
    public bool NavMoreOpen { get; set; }         // the "More" group in the left navigation is unfolded
    public int HistoryMinutes { get; set; } = 5;  // how much history the Summary graphs keep (at the current update speed)
    public bool CompressHistory { get; set; } = true; // old history squeezed to the left (see LineGraph.Compress)

    static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SawnTask", "settings.json");

    public static Settings Load()
    {
        try
        {
            return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch
        {
            return new Settings();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Settings are a convenience; failing to save them must not crash the monitor.
        }
    }
}
