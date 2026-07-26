namespace NetWatcher.App;

public sealed record ProcessTrafficSnapshot(
    int ProcessId,
    string ProcessName,
    string Description,
    double DownloadBytesPerSecond,
    double UploadBytesPerSecond);

public sealed record NetworkSnapshot(
    double TotalDownloadBytesPerSecond,
    double TotalUploadBytesPerSecond,
    IReadOnlyList<ProcessTrafficSnapshot> Processes,
    string ProcessStatusMessage,
    string SelectedInterfaceName,
    string SelectedInterfaceDetail = "",
    string LocalIpAddress = "—",
    bool IsConnected = false);

public sealed record TrafficLogEntry(
    DateTime Timestamp,
    double TotalDownloadBytesPerSecond,
    double TotalUploadBytesPerSecond);

public sealed record NetworkInterfaceOption(
    string Id,
    string Name,
    string Description)
{
    public string DisplayName =>
        string.IsNullOrWhiteSpace(Description) || Description == Name
            ? Name
            : $"{Name} — {Description}";

    public override string ToString() => DisplayName;
}

public sealed class ProcessLimitSettings
{
    public double DownloadLimitKbps { get; set; }
    public double UploadLimitKbps { get; set; }
    public bool DownloadLimitEnabled { get; set; }
    public bool UploadLimitEnabled { get; set; }

    /// <summary>NetBalancer-style priority: Normal, High, Low, Block, Limit</summary>
    public string Priority { get; set; } = "Normal";

    /// <summary>When false, process limits are ignored (toggle off in dashboard).</summary>
    public bool IsLimitControlEnabled { get; set; } = true;
}

public enum TrafficPriority
{
    Normal,
    High,
    Low,
    Block,
    Limit
}

public sealed record TrafficPriorityOption(TrafficPriority Priority, string Label)
{
    public override string ToString() => Label;

    public static IReadOnlyList<TrafficPriorityOption> All { get; } =
    [
        new(TrafficPriority.High, "High"),
        new(TrafficPriority.Normal, "Normal"),
        new(TrafficPriority.Low, "Low"),
        new(TrafficPriority.Limit, "Limit"),
        new(TrafficPriority.Block, "Block")
    ];

    public static TrafficPriorityOption FromName(string? name)
    {
        if (Enum.TryParse<TrafficPriority>(name, ignoreCase: true, out var priority))
        {
            return All.FirstOrDefault(x => x.Priority == priority) ?? All[1];
        }

        return All[1]; // Normal
    }
}

/// <summary>MB/s preset used by dashboard limit ComboBoxes (binary megabytes/s).</summary>
public sealed record SpeedLimitOption(double MegaBytesPerSecond, string Label)
{
    public override string ToString() => Label;

    public bool IsUnlimited => MegaBytesPerSecond <= 0;

    public static IReadOnlyList<SpeedLimitOption> Presets { get; } =
    [
        new(0, "不限制"),
        new(0.1, "0.1 MB/s"),
        new(0.25, "0.25 MB/s"),
        new(0.5, "0.5 MB/s"),
        new(1, "1 MB/s"),
        new(2, "2 MB/s"),
        new(5, "5 MB/s"),
        new(10, "10 MB/s"),
        new(20, "20 MB/s"),
        new(50, "50 MB/s")
    ];

    public static SpeedLimitOption FromKbps(double kbps)
    {
        if (kbps <= 0)
        {
            return Presets[0];
        }

        var mbps = TrafficFormatter.KbpsToMBps(kbps);
        var match = Presets.FirstOrDefault(p => Math.Abs(p.MegaBytesPerSecond - mbps) < 0.02);
        return match ?? Presets.OrderBy(p => Math.Abs(p.MegaBytesPerSecond - mbps)).First();
    }
}

public enum AppNavPage
{
    Overview,
    Processes,
    Network,
    Limits,
    Settings,
    History
}

public sealed record AppNavItem(AppNavPage Page, string Label, string Glyph)
{
    public override string ToString() => Label;
}
