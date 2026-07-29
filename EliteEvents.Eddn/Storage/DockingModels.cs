namespace EliteEvents.Eddn.Storage;

/// <summary>Docking activity for one station, read back from <see cref="RedisKeys.Station"/>.</summary>
public class StationDockingInfo
{
    public string SystemName { get; set; } = string.Empty;
    public string StationName { get; set; } = string.Empty;
    public string StationType { get; set; } = string.Empty;
    public int DockingCount { get; set; }
    public DateTimeOffset LastSeen { get; set; }

    public override string ToString() => $"{SystemName}: {StationName} ({StationType} - {DockingCount})";
}

/// <summary>One day of docking activity for a fleet carrier.</summary>
public class CarrierDockingInfo
{
    public string CarrierId { get; set; } = "";
    public DateTime Date { get; set; } = DateTime.MinValue;
    public int DockingCount { get; set; }

    public override string ToString() => $"Fleet Carrier {CarrierId} - {Date:yyyy-MM-dd} - {DockingCount}";
}

/// <summary>An entry in the weekly most-visited leaderboard.</summary>
public class SystemVisitInfo
{
    public SystemVisitInfo(string systemName, long visits)
    {
        SystemName = systemName;
        VisitCount = visits;
    }

    public string SystemName { get; set; } = "";
    public long VisitCount { get; set; }

    public override string ToString() => $"{SystemName} - {VisitCount}";
}
