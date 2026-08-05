using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;

namespace NetWatcher.App;

public sealed class EtwProcessMonitorService : IDisposable
{
    /// <summary>
    /// Fixed name so restarts reclaim the same ETW slot instead of leaking
    /// NetWatcher-Etw-{pid} sessions after crashes (common cause of 0x800705AA).
    /// </summary>
    private const string SessionName = "NetWatcher-Etw";
    private const string SessionNamePrefix = "NetWatcher-Etw";

    private readonly object _sync = new();
    private readonly ConcurrentDictionary<int, ProcessIdentity> _processIdentityCache = new();
    private readonly Dictionary<int, ProcessCounter> _processCounters = new();

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

    /// <summary>Stop leftover sessions and try starting ETW again (e.g. after 0x800705AA).</summary>
    public bool TryRestart()
    {
        if (_isDisposed)
        {
            return false;
        }

        StopSessionCore();
        StopStaleNetWatcherSessions();
        Start(isRetry: true);
        return _isRunning;
    }

    private void Start(bool isRetry = false)
    {
        if (!OperatingSystem.IsWindows())
        {
            _startupStatus = "目前只有 Windows 支援 ETW 單一程式流量監聽。";
            return;
        }

        // Always reclaim our sessions first — crashed instances leave real-time ETS open.
        StopStaleNetWatcherSessions();

        try
        {
            StartSessionCore();
            _startupStatus = "ETW 單一程式流量監聽中（TCP/UDP · IPv4/IPv6）";
            _isRunning = true;
        }
        catch (UnauthorizedAccessException)
        {
            _startupStatus = "ETW 需要系統管理員權限，請以系統管理員身分執行（否則看不到單一程式流量）。";
            _isRunning = false;
        }
        catch (Exception ex) when (!isRetry && IsNoSystemResources(ex))
        {
            // One more cleanup + retry — classic fix for 0x800705AA / ERROR_NO_SYSTEM_RESOURCES.
            StopSessionCore();
            StopStaleNetWatcherSessions();
            TryStopViaLogman(SessionName);
            Thread.Sleep(300);

            try
            {
                StartSessionCore();
                _startupStatus = "ETW 已重新取得工作階段（先前系統資源不足 0x800705AA）。";
                _isRunning = true;
            }
            catch (Exception retryEx)
            {
                _isRunning = false;
                _startupStatus = FormatEtwFailure(retryEx);
            }
        }
        catch (Exception ex)
        {
            _isRunning = false;
            _startupStatus = FormatEtwFailure(ex);
        }
    }

    private void StartSessionCore()
    {
        StopSessionCore();

        var session = new TraceEventSession(SessionName)
        {
            StopOnDispose = true,
            // Keep buffers modest to reduce ERROR_NO_SYSTEM_RESOURCES under session pressure.
            BufferSizeMB = 16
        };

        try
        {
            // NetworkTCPIP covers IPv4/IPv6 TCP+UDP kernel events.
            session.EnableKernelProvider(KernelTraceEventParser.Keywords.NetworkTCPIP);

            var kernel = session.Source.Kernel;

            kernel.TcpIpRecv += data => RecordDownload(data.ProcessID, data.size);
            kernel.TcpIpSend += data => RecordUpload(data.ProcessID, data.size);
            kernel.UdpIpRecv += data => RecordDownload(data.ProcessID, data.size);
            kernel.UdpIpSend += data => RecordUpload(data.ProcessID, data.size);

            kernel.TcpIpRecvIPV6 += data => RecordDownload(data.ProcessID, data.size);
            kernel.TcpIpSendIPV6 += data => RecordUpload(data.ProcessID, data.size);
            kernel.UdpIpRecvIPV6 += data => RecordDownload(data.ProcessID, data.size);
            kernel.UdpIpSendIPV6 += data => RecordUpload(data.ProcessID, data.size);

            _session = session;

            _processingTask = Task.Run(() =>
            {
                try
                {
                    session.Source.Process();
                }
                catch (Exception ex) when (!_isDisposed)
                {
                    _startupStatus = $"ETW 監聽中斷：{ex.Message}";
                    _isRunning = false;
                }
            });
        }
        catch
        {
            try
            {
                session.Dispose();
            }
            catch
            {
                // ignore
            }

            _session = null;
            throw;
        }
    }

    private void StopSessionCore()
    {
        _isRunning = false;
        var session = _session;
        _session = null;

        if (session is null)
        {
            return;
        }

        try
        {
            session.Stop();
        }
        catch
        {
            // ignore
        }

        try
        {
            session.Dispose();
        }
        catch
        {
            // ignore
        }
    }

    private static void StopStaleNetWatcherSessions()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            IEnumerable<string> names;
            try
            {
                names = TraceEventSession.GetActiveSessionNames();
            }
            catch
            {
                TryStopViaLogman(SessionName);
                return;
            }

            foreach (var name in names)
            {
                if (!name.StartsWith(SessionNamePrefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    // Attach/stop any leftover realtime session (including old NetWatcher-Etw-{pid}).
                    using var existing = TraceEventSession.GetActiveSession(name);
                    existing?.Stop();
                }
                catch
                {
                    TryStopViaLogman(name);
                }
            }
        }
        catch
        {
            TryStopViaLogman(SessionName);
        }
    }

    private static void TryStopViaLogman(string sessionName)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "logman.exe",
                Arguments = $"stop \"{sessionName}\" -ets",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var process = Process.Start(psi);
            process?.WaitForExit(2000);
        }
        catch
        {
            // Best-effort only.
        }
    }

    private static bool IsNoSystemResources(Exception ex)
    {
        if (ex is OutOfMemoryException)
        {
            return true;
        }

        for (var cur = ex; cur is not null; cur = cur.InnerException)
        {
            if (cur is COMException com &&
                (unchecked((uint)com.HResult) == 0x800705AAu || unchecked((uint)com.ErrorCode) == 0x800705AAu))
            {
                return true;
            }

            if (cur is System.ComponentModel.Win32Exception win32 && win32.NativeErrorCode is 1450 or 0x5AA)
            {
                return true;
            }

            var msg = cur.Message ?? string.Empty;
            if (msg.Contains("0x800705AA", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("1450", StringComparison.Ordinal) ||
                msg.Contains("No system resources", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("系統資源不足", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("Insufficient system resources", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string FormatEtwFailure(Exception ex)
    {
        if (IsNoSystemResources(ex))
        {
            return "ETW 啟動失敗 0x800705AA（系統資源不足）：通常是殘留 ETW 工作階段占滿。" +
                   "請關閉所有 NetWatcher 後重開；或系統管理員執行 logman stop \"NetWatcher-Etw\" -ets。" +
                   " 總下載/上傳仍可由網卡顯示。";
        }

        var msg = ex.Message?.Trim() ?? "未知錯誤";
        if (msg.Length > 160)
        {
            msg = msg[..160] + "…";
        }

        return $"ETW 啟動失敗：{msg}";
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
        // Stop in-process only — spawning logman here used to freeze UI close (~2s).
        StopSessionCore();
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
