using System.IO;
using SawnTask.Native;

namespace SawnTask;

// "SawnTask.exe --ipf-probe out.txt": connect to Intel Dynamic Tuning, list every
// participant and domain it announces, and try GET_TEMPERATURE on each domain.
public static class IpfProbe
{
    public static int Run(TextWriter o)
    {
        IntelDttClient client;
        try
        {
            client = IntelDttClient.Connect();
        }
        catch (Exception e)
        {
            o.WriteLine($"CONNECT FAILED: {e.Message}");
            return 1;
        }

        using (client)
        {
            o.WriteLine($"ipfcore.dll: {client.LibraryPath}");
            o.WriteLine($"ipfcore version {client.CoreVersion}, sdk {client.CoreSdkVersion}");
            o.WriteLine("connected; waiting for the service to announce participants...");

            // Participants arrive asynchronously through our callbacks.
            for (int i = 0; i < 50 && (client.Domains.Count == 0 || !client.HasPrimitive); i++)
                Thread.Sleep(100);
            Thread.Sleep(500);

            o.WriteLine($"AppCreate received primitive function: {client.HasPrimitive}");
            o.WriteLine();
            o.WriteLine("PARTICIPANTS");
            foreach (var p in client.Participants.OrderBy(p => p.Name))
                o.WriteLine($"  handle={p.Handle,-6} name={p.Name,-8} bus={p.Bus} acpi={p.AcpiScope,-24} desc={p.Description}");

            o.WriteLine();
            o.WriteLine("DOMAINS -> GET_TEMPERATURE");
            var participants = client.Participants.ToDictionary(p => p.Handle);
            int readings = 0;
            foreach (var d in client.Domains.OrderBy(d => d.Participant).ThenBy(d => d.Handle))
            {
                double? c = client.ReadCelsius(d, out int rc);
                string owner = participants.TryGetValue(d.Participant, out var p) ? p.Name : "?";
                string label = p is null ? "" : IntelDttClient.Label(p, d);
                o.WriteLine($"  {owner,-8} domain={d.Name,-4} type={d.Type,-3} rc={rc,-5} {(c is double v ? $"{v:0.0} °C" : "—"),-9} {label}");
                if (c is not null) readings++;
            }
            o.WriteLine();
            o.WriteLine("DOMAINS -> GET_RAPL_POWER");
            foreach (var d in client.Domains.OrderBy(d => d.Participant).ThenBy(d => d.Handle))
            {
                double? w = client.ReadWatts(d, out int rc);
                string owner = participants.TryGetValue(d.Participant, out var p) ? p.Name : "?";
                o.WriteLine($"  {owner,-8} domain={d.Name,-4} rc={rc,-5} {(w is double v ? $"{v:0.0} W" : "—")}");
            }

            // RAPL power is energy/time between two reads, so sample the CPU domains a few times.
            o.WriteLine();
            o.WriteLine("TCPU GET_RAPL_POWER, 4 reads 1 s apart");
            var cpuDomains = client.Domains.Where(d => participants.TryGetValue(d.Participant, out var p) && IntelDttClient.IsCpuParticipant(p)).ToList();
            for (int i = 0; i < 4; i++)
            {
                Thread.Sleep(1000);
                o.WriteLine("  " + string.Join("   ", cpuDomains.Select(d => $"{d.Name}={(client.ReadWatts(d, out int rc) is double v ? $"{v:0.0} W" : $"rc {rc}")}")));
            }

            o.WriteLine();
            o.WriteLine(readings > 0 ? $"RESULT: {readings} temperature readings" : "RESULT: no readings");
            return readings > 0 ? 0 : 1;
        }
    }
}
