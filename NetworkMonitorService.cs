using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace NetWatcher.App;

public sealed class NetworkMonitorService
{
    private readonly EtwProcessMonitorService _processMonitorService = new();
    private readonly Dictionary<string, InterfaceCounters> _interfaceCounters = new(StringComparer.OrdinalIgnoreCase);
    private string? _selectedInterfaceId;
    private DateTimeOffset? _lastSampleTime;

    public IReadOnlyList<NetworkInterfaceOption> GetInterfaces()
    {
        var list = NetworkInterface.GetAllNetworkInterfaces()
            .Where(nic => nic.NetworkInterfaceType is not NetworkInterfaceType.Loopback)
            .Where(nic => nic.OperationalStatus == OperationalStatus.Up)
            .Select(nic => new NetworkInterfaceOption(nic.Id, nic.Name, nic.Description))
            .OrderBy(nic => nic.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        list.Insert(0, new NetworkInterfaceOption(string.Empty, "全部介面", "加總所有已連線介面"));
        return list;
    }

    public void SetSelectedInterface(string? interfaceId)
    {
        _selectedInterfaceId = string.IsNullOrWhiteSpace(interfaceId) ? null : interfaceId;
    }

    public Task<NetworkSnapshot> CaptureAsync()
    {
        return Task.Run(Capture);
    }

    private NetworkSnapshot Capture()
    {
        var now = DateTimeOffset.UtcNow;
        var intervalSeconds = Math.Max(1e-6, (now - (_lastSampleTime ?? now.AddSeconds(-1))).TotalSeconds);
        _lastSampleTime = now;

        var allUp = NetworkInterface.GetAllNetworkInterfaces()
            .Where(nic => nic.OperationalStatus == OperationalStatus.Up)
            .Where(nic => nic.NetworkInterfaceType is not NetworkInterfaceType.Loopback)
            .ToList();

        var nics = allUp;
        if (!string.IsNullOrWhiteSpace(_selectedInterfaceId))
        {
            nics = allUp
                .Where(nic => string.Equals(nic.Id, _selectedInterfaceId, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        var totalBytesReceived = 0L;
        var totalBytesSent = 0L;
        foreach (var nic in nics)
        {
            var stats = nic.GetIPStatistics();
            totalBytesReceived += stats.BytesReceived;
            totalBytesSent += stats.BytesSent;
        }

        var key = _selectedInterfaceId ?? "__all__";
        if (!_interfaceCounters.TryGetValue(key, out var counters))
        {
            counters = new InterfaceCounters();
            _interfaceCounters[key] = counters;
        }

        var totalDownload = counters.LastReceived == 0
            ? 0
            : Math.Max(0, totalBytesReceived - counters.LastReceived) / intervalSeconds;
        var totalUpload = counters.LastSent == 0
            ? 0
            : Math.Max(0, totalBytesSent - counters.LastSent) / intervalSeconds;

        counters.LastReceived = totalBytesReceived;
        counters.LastSent = totalBytesSent;

        var processSnapshot = _processMonitorService.CollectSnapshot(intervalSeconds);
        var processStatusMessage = BuildProcessStatusMessage(processSnapshot);

        var primary = nics.FirstOrDefault() ?? allUp.FirstOrDefault();
        string interfaceName;
        string interfaceDetail;
        if (string.IsNullOrWhiteSpace(_selectedInterfaceId))
        {
            interfaceName = primary is null
                ? "無連線介面"
                : primary.Name;
            interfaceDetail = primary is null
                ? string.Empty
                : primary.Description;
        }
        else
        {
            interfaceName = primary?.Name ?? "所選介面";
            interfaceDetail = primary?.Description ?? string.Empty;
        }

        var ip = ResolveIpv4(primary) ?? ResolveIpv4(allUp.FirstOrDefault()) ?? "—";
        var isConnected = allUp.Count > 0;

        return new NetworkSnapshot(
            totalDownload,
            totalUpload,
            processSnapshot.Processes,
            processStatusMessage,
            interfaceName,
            interfaceDetail,
            ip,
            isConnected);
    }

    private static string? ResolveIpv4(NetworkInterface? nic)
    {
        if (nic is null)
        {
            return null;
        }

        try
        {
            return nic.GetIPProperties()
                .UnicastAddresses
                .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork &&
                                     !IPAddress.IsLoopback(a.Address))
                ?.Address
                .ToString();
        }
        catch
        {
            return null;
        }
    }

    private static string BuildProcessStatusMessage(ProcessMonitorSnapshot snapshot)
    {
        if (snapshot.Processes.Count > 0)
        {
            return $"已顯示 {snapshot.Processes.Count} 個有流量的程式";
        }

        if (!snapshot.IsRunning)
        {
            return snapshot.StatusMessage;
        }

        return "ETW 已啟用，目前沒有偵測到單一程式流量。";
    }

    public void Dispose()
    {
        _processMonitorService.Dispose();
    }

    private sealed class InterfaceCounters
    {
        public long LastReceived { get; set; }

        public long LastSent { get; set; }
    }
}
