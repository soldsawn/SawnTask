namespace SawnTask.Core;

public enum HealthLevel { Good, Caution, Bad }

// What the disk reports about itself. Fields the disk does not give are null.
public sealed record DiskHealth(
    string Source,
    int CriticalWarning,
    int? TemperatureC,
    int? SpareLeftPercent,
    int? SpareThresholdPercent,
    int? PercentUsed,
    double? TerabytesRead,
    double? TerabytesWritten,
    long? PowerCycles,
    long? PowerOnHours,
    long? UnsafeShutdowns,
    long? MediaErrors,
    bool? PredictFailure,
    long? ReallocatedSectors,
    long? PendingSectors)
{
    // The overall level and the reasons (text key + arguments) behind it; no reasons means all is well.
    public (HealthLevel Level, List<(string Key, object[] Args)> Reasons) Assess()
    {
        var level = HealthLevel.Good;
        var reasons = new List<(string, object[])>();
        void Add(HealthLevel l, string key, params object[] args)
        {
            if (l > level) level = l;
            reasons.Add((key, args));
        }

        if (CriticalWarning != 0) Add(HealthLevel.Bad, "DB.H.Critical");
        if (PredictFailure == true) Add(HealthLevel.Bad, "DB.H.Predict");
        if (SpareLeftPercent is int spare && SpareThresholdPercent is int threshold && threshold > 0 && spare < threshold)
            Add(HealthLevel.Bad, "DB.H.SpareLow");
        else if (SpareLeftPercent is int low && low < 20)
            Add(HealthLevel.Caution, "DB.H.Spare", low);
        if (PercentUsed >= 100) Add(HealthLevel.Bad, "DB.H.WornOut");
        else if (PercentUsed is int used && used >= 80) Add(HealthLevel.Caution, "DB.H.Worn", used);
        if (TemperatureC >= 85) Add(HealthLevel.Bad, "DB.H.TempHigh", TemperatureC.Value);
        else if (TemperatureC >= 70) Add(HealthLevel.Caution, "DB.H.TempWarm", TemperatureC.Value);
        if (MediaErrors > 0) Add(HealthLevel.Caution, "DB.H.Media", MediaErrors.Value);
        if (ReallocatedSectors > 0) Add(HealthLevel.Caution, "DB.H.Realloc", ReallocatedSectors.Value);
        if (PendingSectors > 0) Add(HealthLevel.Caution, "DB.H.Pending", PendingSectors.Value);
        return (level, reasons);
    }
}
