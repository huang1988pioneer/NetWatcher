namespace NetWatcher.App;

public static class TrafficFormatter
{
    private static readonly string[] RateUnits = ["B/s", "KB/s", "MB/s", "GB/s", "TB/s"];
    private static readonly string[] VolumeUnits = ["B", "KB", "MB", "GB", "TB", "PB"];

    /// <summary>1 MB/s (1024-based) = 1,048,576 bytes/s = 1,024 KB/s.</summary>
    public const double BytesPerMBps = 1024d * 1024d;

    public static string FormatBytesPerSecond(double bytesPerSecond)
    {
        return FormatScaled(bytesPerSecond, RateUnits);
    }

    public static string FormatBytes(double bytes)
    {
        return FormatScaled(bytes, VolumeUnits);
    }

    /// <summary>Default dashboard rate unit: MB/s (binary megabytes per second).</summary>
    public static string FormatSpeed(double bytesPerSecond) => FormatBytesPerSecond(bytesPerSecond);

    public static double BytesPerSecondToMBps(double bytesPerSecond) =>
        Math.Max(0, bytesPerSecond) / BytesPerMBps;

    public static double MBpsToKbps(double mbps) => mbps * 1024d;

    public static double KbpsToMBps(double kbps) => kbps / 1024d;

    private static string FormatScaled(double amount, string[] units)
    {
        var value = Math.Max(0, amount);
        var unitIndex = 0;

        while (value >= 1024 && unitIndex < units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        return $"{value:0.##} {units[unitIndex]}";
    }
}
