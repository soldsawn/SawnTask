using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace SawnTask.Native;

// Why did the computer last shut down? The System event log says: event 1074 is a planned
// shutdown or restart (with a type), events 41 (Kernel-Power) and 6008 mean it went down
// unexpectedly. The Performance page shows this next to the up time. The log is read with
// the Windows Event Log API (wevtapi), newest events first.
public static class ShutdownLog
{
    [DllImport("wevtapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr EvtQuery(IntPtr session, string path, string query, uint flags);

    [DllImport("wevtapi.dll", SetLastError = true)]
    static extern bool EvtNext(IntPtr resultSet, uint count, IntPtr[] events, uint timeout, uint flags, out uint returned);

    [DllImport("wevtapi.dll", SetLastError = true)]
    static extern bool EvtRender(IntPtr context, IntPtr fragment, uint flags, uint bufferSize, byte[]? buffer, out uint used, out uint properties);

    [DllImport("wevtapi.dll")]
    static extern bool EvtClose(IntPtr handle);

    const uint EvtQueryChannelPath = 0x1, EvtQueryReverseDirection = 0x200, EvtRenderEventXml = 1;

    public enum Kind { Unknown, Planned, Unexpected }

    // Type is "restart" or "shutdown" for a planned one.
    public sealed record Reason(Kind Kind, string Type);

    // bootUtc: when this session of Windows started.
    internal static Reason Read(DateTime bootUtc)
    {
        IntPtr query = EvtQuery(IntPtr.Zero, "System",
            "*[System[(EventID=1074 or EventID=6008 or EventID=41)]]", EvtQueryChannelPath | EvtQueryReverseDirection);
        if (query == IntPtr.Zero) return new Reason(Kind.Unknown, "");
        try
        {
            var events = new IntPtr[8];
            for (int seen = 0; seen < 40;)
            {
                if (!EvtNext(query, (uint)events.Length, events, 1000, 0, out uint returned) || returned == 0) break;
                for (int i = 0; i < returned; i++, seen++)
                {
                    string xml = Render(events[i]);
                    EvtClose(events[i]);

                    var id = Regex.Match(xml, @"<EventID[^>]*>(\d+)</EventID>");
                    var time = Regex.Match(xml, @"SystemTime='([^']+)'");
                    if (!id.Success || !time.Success || !DateTime.TryParse(time.Groups[1].Value, null,
                            System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var at))
                        continue;

                    // 41 and 6008 are written during the boot that followed the bad shutdown.
                    if ((id.Value.Contains(">41<") || id.Value.Contains(">6008<")) && at >= bootUtc.AddMinutes(-2))
                        return new Reason(Kind.Unexpected, "");
                    // 1074 was written just before the shutdown, so before this boot.
                    if (id.Value.Contains(">1074<") && at < bootUtc)
                    {
                        // <Data> values: process, computer, reason text, reason code, type, comment, user.
                        var data = Regex.Matches(xml, @"<Data[^>]*>([^<]*)</Data>");
                        string type = data.Count > 4 ? data[4].Groups[1].Value.Trim().ToLowerInvariant() : "";
                        // The text is in the language of Windows ("restart", "yeniden başlat", "kapat"):
                        // reduce it to the two cases that matter.
                        return new Reason(Kind.Planned, type.Contains("restart") || type.Contains("yeniden") || type.Contains("reboot") ? "restart" : "shutdown");
                    }
                }
            }
        }
        finally
        {
            EvtClose(query);
        }
        return new Reason(Kind.Unknown, "");
    }

    static string Render(IntPtr eventHandle)
    {
        EvtRender(IntPtr.Zero, eventHandle, EvtRenderEventXml, 0, null, out uint used, out _);
        if (used == 0) return "";
        var buffer = new byte[used];
        return EvtRender(IntPtr.Zero, eventHandle, EvtRenderEventXml, used, buffer, out _, out _)
            ? Encoding.Unicode.GetString(buffer).TrimEnd('\0')
            : "";
    }
}
