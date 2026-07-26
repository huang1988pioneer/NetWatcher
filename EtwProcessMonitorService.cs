using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;

namespace NetWatcher.App;

public sealed class EtwProcessMonitorService : IDisposable
{
    private readonly object _sync = new();
    private readonly ConcurrentDictionary<int, ProcessIdentity> _processIdentityCache = new();
    private readonly Dictionary<int, ProcessCounter> _processCounters = new();
    private readonly string _sessionName = $"NetWatcher-Etw-{Environment.ProcessId}";

    private TraceEventSession? _session;
    private Task? _processingTask;
    private string _startupStatus = "正在啟動 ETW 監聽...";
    private bool _isRunning;
    private bool _isDisposed;

    public EtwProcessMonitorService()
    {
        Start();
    }

    public bool IsRunning => _isRunning;

    public string StartupStatus => _startupStatus;

    public ProcessMonitorSnapshot CollectSnapshot(double intervalSeconds)
    {
        var safeInterval = Math.Max(0.2, intervalSeconds);

        lock (_sync)
        {
            // Aggregate by process name so multi-process browsers (Chrome/Edge) show one row.
            var byName = new Dictionary<string, AggregatedProcess>(StringComparer.OrdinalIgnoreCase);

            foreach (var counter in _processCounters.Values)
            {
                var identity = _processIdentityCache.GetOrAdd(counter.ProcessId, ResolveIdentity);
                var key = string.IsNullOrWhiteSpace(identity.ProcessName)
                    ? $"pid:{counter.ProcessId}"
                    : identity.ProcessName;

                if (!byName.TryGetValue(key, out var agg))
                {
                    agg = new AggregatedProcess(counter.ProcessId, identity.ProcessName, identity.Description);
                    byName[key] = agg;
                }

                agg.DownloadBytes += counter.DownloadBytes;
                agg.UploadBytes += counter.UploadBytes;
                // Prefer a real PID that still exists.
                if (agg.ProcessId <= 0)
                {
                    agg.ProcessId = counter.ProcessId;
                }
            }

            var processes = byName.Values
                .Select(agg => new ProcessTrafficSnapshot(
                    agg.ProcessId,
                    agg.ProcessName,
                    agg.Description,
                    agg.DownloadBytes / safeInterval,
                    agg.UploadBytes / safeInterval))
                .Where(p => p.DownloadBytesPerSecond > 0 || p.UploadBytesPerSecond > 0)
                .OrderByDescending(p => p.DownloadBytesPerSecond + p.UploadBytesPerSecond)
                .ToList();

            _processCounters.Clear();
            return new ProcessMonitorSnapshot(processes, _startupStatus, _isRunning);
        }
    }

    private void Start()
    {
        if (!OperatingSystem.IsWindows())
        {
            _startupStatus = "目前只有 Windows 支援 ETW 單一程式流量監聽。";
            return;
        }

        try
        {
            _session = new TraceEventSession(_sessionName)
            {
                StopOnDispose = true
            };

            // NetworkTCPIP covers IPv4/IPv6 TCP+UDP kernel events used for per-process accounting.
            _session.EnableKernelProvider(KernelTraceEventParser.Keywords.NetworkTCPIP);

            var kernel = _session.Source.Kernel;

            // IPv4 TCP/UDP
            kernel.TcpIpRecv += data => RecordDownload(data.ProcessID, data.size);
            kernel.TcpIpSend += data => RecordUpload(data.ProcessID, data.size);
            kernel.UdpIpRecv += data => RecordDownload(data.ProcessID, data.size);
            kernel.UdpIpSend += data => RecordUpload(data.ProcessID, data.size);

            // IPv6 TCP/UDP (many modern downloads use IPv6 / dual-stack)
            kernel.TcpIpRecvIPV6 += data => RecordDownload(data.ProcessID, data.size);
            kernel.TcpIpSendIPV6 += data => RecordUpload(data.ProcessID, data.size);
            kernel.UdpIpRecvIPV6 += data => RecordDownload(data.ProcessID, data.size);
            kernel.UdpIpSendIPV6 += data => RecordUpload(data.ProcessID, data.size);

            _processingTask = Task.Run(() =>
            {
                try
                {
                    _session.Source.Process();
                }
                catch (Exception ex) when (!_isDisposed)
                {
                    _startupStatus = $"ETW 監聽中斷：{ex.Message}";
                    _isRunning = false;
                }
            });

            _startupStatus = "ETW 單一程式流量監聽中（TCP/UDP · IPv4/IPv6）";
            _isRunning = true;
        }
        catch (UnauthorizedAccessException)
        {
            _startupStatus = "ETW 需要系統管理員權限，請以系統管理員身分執行（否則看不到單一程式流量）。";
            _isRunning = false;
        }
        catch (Exception ex)
        {
            _startupStatus = $"ETW 啟動失敗：{ex.Message}";
            _isRunning = false;
        }
    }

    private void RecordDownload(int processId, int bytes)
    {
        if (processId <= 0 || bytes <= 0)
        {
            return;
        }

        lock (_sync)
        {
            if (!_processCounters.TryGetValue(processId, out var counter))
            {
                counter = new ProcessCounter(processId);
                _processCounters.Add(processId, counter);
            }

            counter.DownloadBytes += bytes;
        }
    }

    private void RecordUpload(int processId, int bytes)
    {
        if (processId <= 0 || bytes <= 0)
        {
            return;
        }

        lock (_sync)
        {
            if (!_processCounters.TryGetValue(processId, out var counter))
            {
                counter = new ProcessCounter(processId);
                _processCounters.Add(processId, counter);
            }

            counter.UploadBytes += bytes;
        }
    }

    private static ProcessIdentity ResolveIdentity(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            string path;
            try
            {
                path = process.MainModule?.FileName ?? string.Empty;
            }
            catch
            {
                path = string.Empty;
            }

            return new ProcessIdentity(
                process.ProcessName,
                string.IsNullOrWhiteSpace(path) ? "系統或受保護行程" : path);
        }
        catch
        {
            return new ProcessIdentity($"pid{processId}", "無法取得程式資訊");
        }
    }

    public void Dispose()
    {
        _isDisposed = true;
        _isRunning = false;

        try
        {
            _session?.Dispose();
        }
        catch
        {
            // Ignore ETW session cleanup issues during shutdown.
        }

        _session = null;
    }

    private sealed class ProcessCounter
    {
        public ProcessCounter(int processId)
        {
            ProcessId = processId;
        }

        public int ProcessId { get; }

        public long DownloadBytes { get; set; }

        public long UploadBytes { get; set; }
    }

    private sealed class AggregatedProcess
    {
        public AggregatedProcess(int processId, string processName, string description)
        {
            ProcessId = processId;
            ProcessName = processName;
            Description = description;
        }

        public int ProcessId { get; set; }

        public string ProcessName { get; }

        public string Description { get; }

        public long DownloadBytes { get; set; }

        public long UploadBytes { get; set; }
    }

    private sealed record ProcessIdentity(string ProcessName, string Description);
}

public sealed record ProcessMonitorSnapshot(
    IReadOnlyList<ProcessTrafficSnapshot> Processes,
    string StatusMessage,
    bool IsRunning);
