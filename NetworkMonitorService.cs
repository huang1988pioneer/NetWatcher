using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace NetWatcher.App;

public sealed class NetworkMonitorService
{
    private readonly EtwProcessMonitorService _processMonitorService = new();
    private readonly Dictionary<string, InterfaceCounters> _interfaceCounters = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PerformanceCounterPair> _perfCounters = new(StringComparer.OrdinalIgnoreCase);
    private string? _selectedInterfaceId;
    private DateTimeOffset? _lastSampleTime;
    private bool _perfCountersInitialized;

    public IReadOnlyList<NetworkInterfaceOption> GetInterfaces()
    {
        var list = NetworkInterface.GetAllNetworkInterfaces()
            .Where(IsCandidateInterface)
            .Select(nic => new NetworkInterfaceOption(nic.Id, nic.Name, nic.Description))
            .OrderBy(nic => nic.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        list.Insert(0, new NetworkInterfaceOption(string.Empty, "全部介面", "加總所有已連線介面（建議）"));
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
        var intervalSeconds = Math.Max(0.2, (now - (_lastSampleTime ?? now.AddSeconds(-1))).TotalSeconds);
        _lastSampleTime = now;

        var allCandidates = NetworkInterface.GetAllNetworkInterfaces()
            .Where(IsCandidateInterface)
            .ToList();

        var nics = allCandidates;
        if (!string.IsNullOrWhiteSpace(_selectedInterfaceId))
        {
            nics = allCandidates
                .Where(nic => string.Equals(nic.Id, _selectedInterfaceId, StringComparison.OrdinalIgnoreCase))
                .ToList();

            // If selected NIC disappeared, fall back to all.
            if (nics.Count == 0)
            {
                nics = allCandidates;
            }
        }

        // Per-interface delta is more accurate than summing totals then subtracting
        // (avoids false zero when one adapter is added/removed).
        double totalDownload = 0;
        double totalUpload = 0;
        var anySampled = false;

        foreach (var nic in nics)
        {
            long received;
            long sent;
            try
            {
                var stats = nic.GetIPStatistics();
                received = stats.BytesReceived;
                sent = stats.BytesSent;
            }
            catch
            {
                continue;
            }

            if (!_interfaceCounters.TryGetValue(nic.Id, out var counters))
            {
                counters = new InterfaceCounters();
                _interfaceCounters[nic.Id] = counters;
            }

            if (counters.HasBaseline)
            {
                // Counter reset (adapter rebind / driver reload)
                var deltaIn = received >= counters.LastReceived
                    ? received - counters.LastReceived
                    : received;
                var deltaOut = sent >= counters.LastSent
                    ? sent - counters.LastSent
                    : sent;

                totalDownload += Math.Max(0, deltaIn);
                totalUpload += Math.Max(0, deltaOut);
                anySampled = true;
            }

            counters.LastReceived = received;
            counters.LastSent = sent;
            counters.HasBaseline = true;
        }

        var downloadBps = anySampled ? totalDownload / intervalSeconds : 0;
        var uploadBps = anySampled ? totalUpload / intervalSeconds : 0;

        // Performance counters as fallback when IP statistics stay flat (some VPN / virtual NICs).
        if (downloadBps <= 1 && uploadBps <= 1 && string.IsNullOrWhiteSpace(_selectedInterfaceId))
        {
            var perf = TryReadPerformanceCounters();
            if (perf.DownloadBps > downloadBps)
            {
                downloadBps = perf.DownloadBps;
            }

            if (perf.UploadBps > uploadBps)
            {
                uploadBps = perf.UploadBps;
            }
        }

        var processSnapshot = _processMonitorService.CollectSnapshot(intervalSeconds);
        var processStatusMessage = BuildProcessStatusMessage(processSnapshot, downloadBps, uploadBps);

        // If ETW sees more traffic than NIC counters (e.g. wrong adapter selected), prefer ETW totals.
        var etwDownload = processSnapshot.Processes.Sum(p => p.DownloadBytesPerSecond);
        var etwUpload = processSnapshot.Processes.Sum(p => p.UploadBytesPerSecond);
        if (etwDownload > downloadBps * 1.15 || (downloadBps < 256 && etwDownload > downloadBps))
        {
            downloadBps = Math.Max(downloadBps, etwDownload);
        }

        if (etwUpload > uploadBps * 1.15 || (uploadBps < 256 && etwUpload > uploadBps))
        {
            uploadBps = Math.Max(uploadBps, etwUpload);
        }

        var primary = nics.FirstOrDefault() ?? allCandidates.FirstOrDefault();
        string interfaceName;
        string interfaceDetail;
        if (string.IsNullOrWhiteSpace(_selectedInterfaceId))
        {
            interfaceName = primary is null ? "無連線介面" : primary.Name;
            interfaceDetail = primary is null ? string.Empty : primary.Description;
            if (allCandidates.Count > 1)
            {
                interfaceName = $"全部介面（{allCandidates.Count}）";
            }
        }
        else
        {
            interfaceName = primary?.Name ?? "所選介面";
            interfaceDetail = primary?.Description ?? string.Empty;
        }

        var ip = ResolveIpv4(primary) ?? ResolveIpv4(allCandidates.FirstOrDefault()) ?? "—";
        var isConnected = allCandidates.Count > 0;

        return new NetworkSnapshot(
            downloadBps,
            uploadBps,
            processSnapshot.Processes,
            processStatusMessage,
            interfaceName,
            interfaceDetail,
            ip,
            isConnected);
    }

    private static bool IsCandidateInterface(NetworkInterface nic)
    {
        if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback)
        {
            return false;
        }

        // Keep common active / semi-active adapters (Wi-Fi, Ethernet, VPN tunnel).
        return nic.OperationalStatus is OperationalStatus.Up
            or OperationalStatus.Unknown
            or OperationalStatus.Dormant;
    }

    private (double DownloadBps, double UploadBps) TryReadPerformanceCounters()
    {
        if (!OperatingSystem.IsWindows())
        {
            return (0, 0);
        }

        try
        {
            EnsurePerformanceCounters();
            double down = 0;
            double up = 0;
            foreach (var pair in _perfCounters.Values)
            {
                try
                {
                    // NextValue is average since previous call (~1s with our timer).
                    down += Math.Max(0, pair.Received.NextValue());
                    up += Math.Max(0, pair.Sent.NextValue());
                }
                catch
                {
                    // Instance may have disappeared.
                }
            }

            return (down, up);
        }
        catch
        {
            return (0, 0);
        }
    }

    private void EnsurePerformanceCounters()
    {
        if (_perfCountersInitialized || !OperatingSystem.IsWindows())
        {
            return;
        }

        _perfCountersInitialized = true;
        try
        {
            var category = new PerformanceCounterCategory("Network Interface");
            foreach (var instance in category.GetInstanceNames())
            {
                if (instance.Contains("Loopback", StringComparison.OrdinalIgnoreCase) ||
                    instance.Contains("isatap", StringComparison.OrdinalIgnoreCase) ||
                    instance.Contains("Teredo", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    var recv = new PerformanceCounter("Network Interface", "Bytes Received/sec", instance, readOnly: true);
                    var sent = new PerformanceCounter("Network Interface", "Bytes Sent/sec", instance, readOnly: true);
                    // Prime counters (first NextValue is often 0).
                    _ = recv.NextValue();
                    _ = sent.NextValue();
                    _perfCounters[instance] = new PerformanceCounterPair(recv, sent);
                }
                catch
                {
                    // Skip unavailable instances.
                }
            }
        }
        catch
        {
            // Performance counters unavailable on some systems.
        }
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

    private static string BuildProcessStatusMessage(
        ProcessMonitorSnapshot snapshot,
        double downloadBps,
        double uploadBps)
    {
        if (snapshot.Processes.Count > 0)
        {
            return $"已顯示 {snapshot.Processes.Count} 個有流量的程式 · {snapshot.StatusMessage}";
        }

        if (!snapshot.IsRunning)
        {
            if (downloadBps > 0 || uploadBps > 0)
            {
                return snapshot.StatusMessage + " 總流量仍可由網卡計數器顯示。";
            }

            return snapshot.StatusMessage;
        }

        if (downloadBps > 1024 || uploadBps > 1024)
        {
            return "網卡有流量，但 ETW 尚未對應到程式（可能是受保護行程 / 驅動卸載）。";
        }

        return "ETW 已啟用，目前沒有偵測到單一程式流量。";
    }

    public bool TryRestartEtw() => _processMonitorService.TryRestart();

    public string EtwStatus => _processMonitorService.StartupStatus;

    public bool IsEtwRunning => _processMonitorService.IsRunning;

    public void Dispose()
    {
        foreach (var pair in _perfCounters.Values)
        {
            pair.Received.Dispose();
            pair.Sent.Dispose();
        }

        _perfCounters.Clear();
        _processMonitorService.Dispose();
    }

    private sealed class InterfaceCounters
    {
        public long LastReceived { get; set; }

        public long LastSent { get; set; }

        public bool HasBaseline { get; set; }
    }

    private sealed class PerformanceCounterPair
    {
        public PerformanceCounterPair(PerformanceCounter received, PerformanceCounter sent)
        {
            Received = received;
            Sent = sent;
        }

        public PerformanceCounter Received { get; }

        public PerformanceCounter Sent { get; }
    }
}
