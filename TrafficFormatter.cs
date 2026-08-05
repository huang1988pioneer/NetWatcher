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

    /// <summary>
    /// Default dashboard rate display.  Uses auto-scaling units (B/s, KB/s,
    /// MB/s, GB/s) so that rates below 1 MB/s are not rounded to "0.00 MB/s",
    /// which users misread as "0 MB/s".
    /// </summary>
    public static string FormatSpeed(double bytesPerSecond) => FormatBytesPerSecond(bytesPerSecond);

    /// <summary>Always format as MB/s for the simplified dashboard.</summary>
    public static string FormatMBps(double bytesPerSecond)
    {
        var mbps = Math.Max(0, bytesPerSecond) / BytesPerMBps;
        return mbps >= 100 ? $"{mbps:0.#} MB/s"
             : mbps >= 10  ? $"{mbps:0.##} MB/s"
             : mbps >= 1   ? $"{mbps:0.##} MB/s"
                           : $"{mbps:0.00} MB/s";
    }

    public static double BytesPerSecondToMBps(double bytesPerSecond) =>
        Math.Max(0, bytesPerSecond) / BytesPerMBps;

    public static double MBpsToKbps(double mbps) => mbps * 1024d;

    public static double KbpsToMBps(double kbps) => kbps / 1024d;

    /// <summary>Decimal megabits/s (Speedtest-style): 1 Mbps = 1,000,000 bits/s.</summary>
    public static double BytesPerSecondToMbps(double bytesPerSecond) =>
        Math.Max(0, bytesPerSecond) * 8d / 1_000_000d;

    public static string FormatMbps(double bytesPerSecond)
    {
        var mbps = BytesPerSecondToMbps(bytesPerSecond);
        return mbps >= 100 ? $"{mbps:0.#} Mbps"
             : mbps >= 10  ? $"{mbps:0.##} Mbps"
             : mbps >= 1   ? $"{mbps:0.##} Mbps"
                           : $"{mbps:0.00} Mbps";
    }

    public static string FormatLatencyMs(double milliseconds)
    {
        if (milliseconds < 10)
        {
            return $"{milliseconds:0.0} ms";
        }

        return milliseconds < 100
            ? $"{milliseconds:0.#} ms"
            : $"{milliseconds:0} ms";
    }

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
