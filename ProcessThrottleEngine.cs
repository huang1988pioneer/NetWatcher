using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace NetWatcher.App;

/// <summary>
/// Userspace bandwidth shaper that actually affects both download and upload
/// without a kernel filter driver.
///
/// Method: if a process exceeded its limit last interval, suspend all matching
/// processes for a fraction of the next second (duty-cycle). Crude but real.
/// Complements Windows QoS (upload-only, admin).
/// </summary>
public sealed class ProcessThrottleEngine : IDisposable
{
    private readonly ConcurrentDictionary<string, ThrottleTarget> _targets =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly object _cycleSync = new();
    private CancellationTokenSource? _cycleCts;
    private bool _disposed;

    public string LastActionText { get; private set; } = "軟限速待命";

    public void SetLimit(
        string processName,
        double downloadLimitBytesPerSecond,
        double uploadLimitBytesPerSecond,
        bool enabled)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            return;
        }

        if (!enabled || (downloadLimitBytesPerSecond <= 0 && uploadLimitBytesPerSecond <= 0))
        {
            _targets.TryRemove(processName, out _);
            return;
        }

        _targets[processName] = new ThrottleTarget(
            processName,
            Math.Max(0, downloadLimitBytesPerSecond),
            Math.Max(0, uploadLimitBytesPerSecond));
    }

    public void Clear(string processName)
    {
        _targets.TryRemove(processName, out _);
    }

    public void ClearAll()
    {
        _targets.Clear();
        CancelCycle();
        LastActionText = "已清除軟限速";
    }

    /// <summary>
    /// Called ~1s with measured rates. Adjusts suspend duty-cycle for next second.
    /// </summary>
    public void OnSample(
        string processName,
        double measuredDownloadBps,
        double measuredUploadBps)
    {
        if (_disposed || !_targets.TryGetValue(processName, out var target))
        {
            return;
        }

        var dlFactor = target.DownloadLimitBps > 0 && measuredDownloadBps > target.DownloadLimitBps
            ? target.DownloadLimitBps / measuredDownloadBps
            : 1d;

        var ulFactor = target.UploadLimitBps > 0 && measuredUploadBps > target.UploadLimitBps
            ? target.UploadLimitBps / measuredUploadBps
            : 1d;

        // Strongest restriction wins (smallest run fraction).
        var runFraction = Math.Clamp(Math.Min(dlFactor, ulFactor), 0.05, 1d);
        var suspendMs = (int)Math.Round((1d - runFraction) * 1000d);

        if (suspendMs < 40)
        {
            LastActionText = $"{processName}：在限速內";
            return;
        }

        // Cap suspend so UI/process stay responsive.
        suspendMs = Math.Min(suspendMs, 850);
        _ = RunSuspendCycleAsync(processName, suspendMs);
    }

    private async Task RunSuspendCycleAsync(string processName, int suspendMs)
    {
        CancellationTokenSource cts;
        lock (_cycleSync)
        {
            _cycleCts?.Cancel();
            _cycleCts?.Dispose();
            cts = new CancellationTokenSource();
            _cycleCts = cts;
        }

        var pids = FindPids(processName);
        if (pids.Count == 0)
        {
            LastActionText = $"{processName}：找不到行程，無法軟限速";
            return;
        }

        var suspended = new List<IntPtr>();
        try
        {
            foreach (var pid in pids)
            {
                try
                {
                    var handle = OpenProcess(ProcessAccessFlags.SuspendResume, false, pid);
                    if (handle == IntPtr.Zero)
                    {
                        continue;
                    }

                    if (NtSuspendProcess(handle) == 0)
                    {
                        suspended.Add(handle);
                    }
                    else
                    {
                        CloseHandle(handle);
                    }
                }
                catch
                {
                    // Skip protected processes.
                }
            }

            if (suspended.Count == 0)
            {
                LastActionText = $"{processName}：無法暫停行程（可能受保護/權限不足）";
                return;
            }

            LastActionText =
                $"{processName}：軟限速暫停 {suspendMs}ms（{suspended.Count} 個行程）";

            try
            {
                await Task.Delay(suspendMs, cts.Token);
            }
            catch (OperationCanceledException)
            {
                // Newer cycle superseded this one.
            }
        }
        finally
        {
            foreach (var handle in suspended)
            {
                try
                {
                    NtResumeProcess(handle);
                }
                catch
                {
                    // ignore
                }
                finally
                {
                    CloseHandle(handle);
                }
            }
        }
    }

    private static List<int> FindPids(string processName)
    {
        var name = processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? Path.GetFileNameWithoutExtension(processName)
            : processName;

        try
        {
            return Process.GetProcessesByName(name).Select(p =>
            {
                try
                {
                    return p.Id;
                }
                finally
                {
                    p.Dispose();
                }
            }).Where(id => id > 0).Distinct().ToList();
        }
        catch
        {
            return [];
        }
    }

    private void CancelCycle()
    {
        lock (_cycleSync)
        {
            try
            {
                _cycleCts?.Cancel();
                _cycleCts?.Dispose();
            }
            catch
            {
                // ignore
            }

            _cycleCts = null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ClearAll();
    }

    private sealed record ThrottleTarget(
        string ProcessName,
        double DownloadLimitBps,
        double UploadLimitBps);

    [Flags]
    private enum ProcessAccessFlags : uint
    {
        SuspendResume = 0x0800
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(ProcessAccessFlags processAccess, bool bInheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("ntdll.dll")]
    private static extern int NtSuspendProcess(IntPtr processHandle);

    [DllImport("ntdll.dll")]
    private static extern int NtResumeProcess(IntPtr processHandle);
}
