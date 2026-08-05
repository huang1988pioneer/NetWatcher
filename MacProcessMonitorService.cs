using System.Diagnostics;
using System.Globalization;

namespace NetWatcher.App;

public sealed class MacProcessMonitorService
{
    private readonly Dictionary<int, MacProcessCounter> _counters = new();
    private string _statusMessage = "macOS nettop 尚未取樣。";

    public ProcessMonitorSnapshot CollectSnapshot(double intervalSeconds)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return new ProcessMonitorSnapshot([], "此平台尚未支援單一程式流量。", false);
        }

        try
        {
            var current = ReadCounters();
            if (current.Count == 0)
            {
                _statusMessage = "macOS nettop 沒有回傳程式流量資料。";
                return new ProcessMonitorSnapshot([], _statusMessage, true);
            }

            var snapshots = BuildSnapshots(current, intervalSeconds);
            _counters.Clear();
            foreach (var pair in current)
            {
                _counters[pair.Key] = pair.Value;
            }

            _statusMessage = snapshots.Count > 0
                ? "macOS nettop 已啟用 · 約每秒更新"
                : "macOS nettop 已啟用，目前沒有偵測到單一程式流量。";

            return new ProcessMonitorSnapshot(snapshots, _statusMessage, true);
        }
        catch (Exception ex)
        {
            _statusMessage = $"macOS nettop 取樣失敗：{ex.Message}";
            return new ProcessMonitorSnapshot([], _statusMessage, false);
        }
    }

    private List<ProcessTrafficSnapshot> BuildSnapshots(
        IReadOnlyDictionary<int, MacProcessCounter> current,
        double intervalSeconds)
    {
        var aggregate = new Dictionary<string, AggregatedMacProcess>(StringComparer.OrdinalIgnoreCase);

        foreach (var pair in current)
        {
            if (!_counters.TryGetValue(pair.Key, out var previous))
            {
                continue;
            }

            var currentCounter = pair.Value;
            var deltaIn = currentCounter.BytesIn >= previous.BytesIn
                ? currentCounter.BytesIn - previous.BytesIn
                : 0;
            var deltaOut = currentCounter.BytesOut >= previous.BytesOut
                ? currentCounter.BytesOut - previous.BytesOut
                : 0;

            if (deltaIn <= 0 && deltaOut <= 0)
            {
                continue;
            }

            var processName = currentCounter.ProcessName;
            if (!aggregate.TryGetValue(processName, out var item))
            {
                item = new AggregatedMacProcess(currentCounter.ProcessId, processName);
                aggregate[processName] = item;
            }

            item.DownloadBytesPerSecond += deltaIn / intervalSeconds;
            item.UploadBytesPerSecond += deltaOut / intervalSeconds;
        }

        return aggregate.Values
            .Where(p => p.DownloadBytesPerSecond > 16 || p.UploadBytesPerSecond > 16)
            .OrderByDescending(p => p.DownloadBytesPerSecond + p.UploadBytesPerSecond)
            .Take(80)
            .Select(p => new ProcessTrafficSnapshot(
                p.ProcessId,
                p.ProcessName,
                $"PID {p.ProcessId} · macOS nettop",
                p.DownloadBytesPerSecond,
                p.UploadBytesPerSecond))
            .ToList();
    }

    private static Dictionary<int, MacProcessCounter> ReadCounters()
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "/usr/bin/nettop",
            ArgumentList =
            {
                "-P",
                "-L",
                "1",
                "-x",
                "-n",
                "-J",
                "bytes_in,bytes_out"
            },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        process.Start();
        var output = process.StandardOutput.ReadToEnd();
        if (!process.WaitForExit(2500))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Ignore cleanup failures.
            }
        }

        var counters = new Dictionary<int, MacProcessCounter>();
        using var reader = new StringReader(output);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line) ||
                line[0] == ',' ||
                line.StartsWith("time,", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var parts = line.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length < 3 ||
                !TryParseProcess(parts[0], out var name, out var pid) ||
                !long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var bytesIn) ||
                !long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var bytesOut))
            {
                continue;
            }

            counters[pid] = new MacProcessCounter(pid, name, bytesIn, bytesOut);
        }

        return counters;
    }

    private static bool TryParseProcess(string value, out string processName, out int processId)
    {
        processName = string.Empty;
        processId = 0;

        var split = value.LastIndexOf('.');
        if (split <= 0 || split == value.Length - 1)
        {
            return false;
        }

        if (!int.TryParse(value[(split + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out processId))
        {
            return false;
        }

        processName = value[..split].Trim();
        return !string.IsNullOrWhiteSpace(processName);
    }

    private sealed record MacProcessCounter(
        int ProcessId,
        string ProcessName,
        long BytesIn,
        long BytesOut);

    private sealed class AggregatedMacProcess
    {
        public AggregatedMacProcess(int processId, string processName)
        {
            ProcessId = processId;
            ProcessName = processName;
        }

        public int ProcessId { get; }

        public string ProcessName { get; }

        public double DownloadBytesPerSecond { get; set; }

        public double UploadBytesPerSecond { get; set; }
    }
}
