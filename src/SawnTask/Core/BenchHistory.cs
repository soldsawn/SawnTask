using System.IO;
using System.Text.Json;

namespace SawnTask.Core;

// One finished disk speed test: the four numbers shown on the cards.
public sealed record BenchRecord(DateTime Time, string Drive, string Model, long FileBytes, int Passes,
    double SeqRead, double SeqWrite, double RndRead, double RndWrite);

// Earlier tests, newest first, in %LOCALAPPDATA%\SawnTask\bench-history.json.
public static class BenchHistory
{
    const int Keep = 100;

    static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SawnTask", "bench-history.json");

    public static List<BenchRecord> Load()
    {
        try { return JsonSerializer.Deserialize<List<BenchRecord>>(File.ReadAllText(FilePath)) ?? []; }
        catch { return []; }
    }

    public static void Add(BenchRecord record)
    {
        var all = Load();
        all.Insert(0, record);
        Save(all.Take(Keep).ToList());
    }

    public static void Clear() => Save([]);

    static void Save(List<BenchRecord> records)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // The history is a convenience; failing to save it must not break the test.
        }
    }
}
