using System.IO;
using System.Text;
using System.Text.Json;

namespace SawnTask.Core;

// The flight recorder: everything the graphs show, saved to a file that can be replayed
// later. The format is deliberately readable:
// line 1 is a header, every following line is one SampleFrame as JSON.
public sealed class Recorder : IDisposable
{
    public const string Extension = ".minitrace";

    static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    StreamWriter? writer;

    public static string Folder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SawnTask", "recordings");

    public bool IsRecording => writer is not null;
    public string? CurrentPath { get; private set; }
    public int FrameCount { get; private set; }

    public void Start()
    {
        Directory.CreateDirectory(Folder);
        CurrentPath = Path.Combine(Folder, $"rec-{DateTime.Now:yyyyMMdd-HHmmss}{Extension}");
        writer = new StreamWriter(CurrentPath, false, new UTF8Encoding(false));
        writer.WriteLine(JsonSerializer.Serialize(new { format = "minitrace", version = 1, createdUtc = DateTime.UtcNow }));
        FrameCount = 0;
    }

    public void Write(SampleFrame frame)
    {
        if (writer is null) return;
        writer.WriteLine(JsonSerializer.Serialize(frame, Json));
        FrameCount++;
    }

    public void Stop()
    {
        writer?.Dispose();
        writer = null;
    }

    public static List<SampleFrame> Load(string path) => File.ReadLines(path)
        .Skip(1)
        .Where(line => line.Length > 0)
        .Select(line => JsonSerializer.Deserialize<SampleFrame>(line, Json)!)
        .ToList();

    public void Dispose() => Stop();
}
