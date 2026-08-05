using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace NetWatcher.App;

/// <summary>
/// Packet-level process shaper backed by WinDivert.  Matching packets are held
/// in user mode and re-injected at the configured token-bucket rate.  Unlike
/// the previous suspend/resume implementation, this limits network bytes, not
/// CPU execution time.
/// </summary>
public sealed class ProcessThrottleEngine : IDisposable
{
    private const int AfInet = 2;
    private const int TcpTableOwnerPidAll = 5;
    private const int UdpTableOwnerPid = 1;
    private const int NetworkLayer = 0;
    private const ulong ShutdownBoth = 3;
    private static readonly IntPtr InvalidHandle = new(-1);

    private readonly ConcurrentDictionary<string, ThrottleTarget> _targets = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<int, ThrottleTarget> _targetsByPid = new();
    private readonly ConcurrentDictionary<ushort, int> _tcpOwnersByPort = new();
    private readonly ConcurrentDictionary<BucketKey, TokenBucket> _buckets = new();
    private readonly object _lifecycleSync = new();
    private CancellationTokenSource? _workerCts;
    private Task? _worker;
    private IntPtr _handle = IntPtr.Zero;
    private long _lastOwnershipRefresh;
    private bool _disposed;

    public string LastActionText { get; private set; } = "封包限速待命";

    public void SetLimit(string processName, double downloadLimitBytesPerSecond, double uploadLimitBytesPerSecond, bool enabled)
    {
        if (string.IsNullOrWhiteSpace(processName)) return;

        if (!enabled || (downloadLimitBytesPerSecond <= 0 && uploadLimitBytesPerSecond <= 0))
            _targets.TryRemove(processName, out _);
        else
            _targets[processName] = new ThrottleTarget(processName, Math.Max(0, downloadLimitBytesPerSecond), Math.Max(0, uploadLimitBytesPerSecond));

        RefreshOwnership(force: true);
        if (!_targets.IsEmpty) EnsureWorker();
    }

    public void Clear(string processName)
    {
        _targets.TryRemove(processName, out _);
        RefreshOwnership(force: true);
    }

    public void ClearAll()
    {
        _targets.Clear();
        _targetsByPid.Clear();
        _tcpOwnersByPort.Clear();
        _buckets.Clear();
        LastActionText = "已移除封包限速";
    }

    // Retained for the existing monitor call site.  It refreshes PID/port ownership;
    // actual shaping happens on every diverted packet, not on one-second samples.
    public void OnSample(string processName, double measuredDownloadBps, double measuredUploadBps) => RefreshOwnership(force: false);

    private void EnsureWorker()
    {
        if (!OperatingSystem.IsWindows() || _disposed) return;
        lock (_lifecycleSync)
        {
            if (_worker is { IsCompleted: false }) return;
            _workerCts = new CancellationTokenSource();
            _worker = Task.Run(() => RunAsync(_workerCts.Token));
        }
    }

    private void RefreshOwnership(bool force)
    {
        if (!OperatingSystem.IsWindows()) return;
        var now = Stopwatch.GetTimestamp();
        if (!force && now - Interlocked.Read(ref _lastOwnershipRefresh) < Stopwatch.Frequency) return;
        Interlocked.Exchange(ref _lastOwnershipRefresh, now);

        var wanted = new Dictionary<int, ThrottleTarget>();
        foreach (var target in _targets.Values)
        {
            var name = Path.GetFileNameWithoutExtension(target.ProcessName);
            try
            {
                foreach (var process in Process.GetProcessesByName(name))
                {
                    try { wanted[process.Id] = target; }
                    finally { process.Dispose(); }
                }
            }
            catch { /* Process may have exited. */ }
        }

        _targetsByPid.Clear();
        foreach (var pair in wanted) _targetsByPid[pair.Key] = pair.Value;
        RefreshTcpOwners(wanted.Keys);
        RefreshUdpOwners(wanted.Keys);
    }

    private void RefreshTcpOwners(IEnumerable<int> targetPids)
    {
        var wanted = targetPids.ToHashSet();
        _tcpOwnersByPort.Clear();
        if (wanted.Count == 0) return;

        var size = 0;
        if (GetExtendedTcpTable(IntPtr.Zero, ref size, true, AfInet, TcpTableOwnerPidAll, 0) != 122 || size <= 4) return;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(buffer, ref size, true, AfInet, TcpTableOwnerPidAll, 0) != 0) return;
            var count = Marshal.ReadInt32(buffer);
            const int rowSize = 24;
            for (var index = 0; index < count; index++)
            {
                var row = IntPtr.Add(buffer, 4 + index * rowSize);
                var rawPort = unchecked((uint)Marshal.ReadInt32(row, 8));
                var pid = Marshal.ReadInt32(row, 20);
                if (!wanted.Contains(pid)) continue;
                var port = (ushort)(((rawPort & 0xFF) << 8) | ((rawPort >> 8) & 0xFF));
                if (port != 0) _tcpOwnersByPort[port] = pid;
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private void RefreshUdpOwners(IEnumerable<int> targetPids)
    {
        var wanted = targetPids.ToHashSet();
        if (wanted.Count == 0) return;
        var size = 0;
        if (GetExtendedUdpTable(IntPtr.Zero, ref size, true, AfInet, UdpTableOwnerPid, 0) != 122 || size <= 4) return;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedUdpTable(buffer, ref size, true, AfInet, UdpTableOwnerPid, 0) != 0) return;
            var count = Marshal.ReadInt32(buffer);
            const int rowSize = 12;
            for (var index = 0; index < count; index++)
            {
                var row = IntPtr.Add(buffer, 4 + index * rowSize);
                var rawPort = unchecked((uint)Marshal.ReadInt32(row, 4));
                var pid = Marshal.ReadInt32(row, 8);
                if (!wanted.Contains(pid)) continue;
                var port = (ushort)(((rawPort & 0xFF) << 8) | ((rawPort >> 8) & 0xFF));
                if (port != 0) _tcpOwnersByPort[port] = pid;
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            _handle = WinDivertOpen("tcp or udp", NetworkLayer, 0, 0);
            if (_handle == InvalidHandle || _handle == IntPtr.Zero)
            {
                LastActionText = $"封包限速無法啟動（WinDivert：{Marshal.GetLastWin32Error()}）";
                return;
            }

            LastActionText = "封包限速已啟動";

            // Process packets concurrently so that queued packets waiting in the
            // WinDivert kernel buffer don't all burst out once a single delay ends.
            // Each captured packet is dispatched to a handler that independently
            // waits for its token-bucket slot before re-injecting.
            const int maxConcurrency = 64;
            var sem = new SemaphoreSlim(maxConcurrency, maxConcurrency);

            while (!cancellationToken.IsCancellationRequested)
            {
                await sem.WaitAsync(cancellationToken);

                // Each packet needs its own buffer since handlers run concurrently.
                var packet = new byte[0xFFFF];
                var address = new WindivertAddress();
                if (!WinDivertRecv(_handle, packet, (uint)packet.Length, out var length, ref address))
                {
                    sem.Release();
                    if (cancellationToken.IsCancellationRequested) break;
                    LastActionText = $"封包限速接收失敗（{Marshal.GetLastWin32Error()}）";
                    break;
                }

                var capturedLength = length;
                var capturedAddress = address;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        if (TryGetTarget(packet, (int)capturedLength, capturedAddress.Outbound, out var target, out var upload))
                        {
                            var rate = upload ? target.UploadLimitBytesPerSecond : target.DownloadLimitBytesPerSecond;
                            if (rate > 0)
                            {
                                var bucket = _buckets.GetOrAdd(new BucketKey(target.ProcessName, upload), _ => new TokenBucket());
                                await bucket.WaitAsync(capturedLength, rate, cancellationToken);
                                LastActionText = $"{target.ProcessName} 封包限速中：{rate / TrafficFormatter.BytesPerMBps:0.##} MB/s";
                            }
                        }

                        if (!WinDivertSend(_handle, packet, capturedLength, out _, ref capturedAddress))
                        {
                            LastActionText = $"封包限速重新注入失敗（{Marshal.GetLastWin32Error()}）";
                        }
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception ex) { LastActionText = $"封包處理錯誤：{ex.Message}"; }
                    finally
                    {
                        sem.Release();
                    }
                }, cancellationToken);
            }
        }
        catch (OperationCanceledException) { }
        catch (DllNotFoundException) { LastActionText = "缺少 WinDivert.dll，無法啟動真正限速"; }
        catch (Exception ex) { LastActionText = $"封包限速錯誤：{ex.Message}"; }
        finally
        {
            var handle = Interlocked.Exchange(ref _handle, IntPtr.Zero);
            if (handle != IntPtr.Zero && handle != InvalidHandle) WinDivertClose(handle);
        }
    }

    private bool TryGetTarget(byte[] packet, int length, bool outbound, out ThrottleTarget target, out bool upload)
    {
        target = default!;
        upload = outbound;
        if (length < 1) return false;
        var version = packet[0] >> 4;
        var headerLength = version == 4 ? (packet[0] & 0x0F) * 4 : version == 6 ? 40 : 0;
        if (headerLength < 20 || length < headerLength + 4) return false;
        var protocol = version == 4 ? packet[9] : packet[6];
        if (protocol is not 6 and not 17) return false;
        var sourcePort = (ushort)((packet[headerLength] << 8) | packet[headerLength + 1]);
        var destinationPort = (ushort)((packet[headerLength + 2] << 8) | packet[headerLength + 3]);
        var localPort = outbound ? sourcePort : destinationPort;
        return _tcpOwnersByPort.TryGetValue(localPort, out var pid) && _targetsByPid.TryGetValue(pid, out target!);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_lifecycleSync)
        {
            _workerCts?.Cancel();
            if (_handle != IntPtr.Zero && _handle != InvalidHandle) WinDivertShutdown(_handle, ShutdownBoth);
        }
        try { _worker?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _workerCts?.Dispose();
        ClearAll();
    }

    /// <summary>
    /// Strict token-bucket rate limiter.  Grants <c>bytesPerSecond</c> tokens
    /// every second, with a burst window of 50 ms (large enough to absorb the
    /// ~15 ms Windows timer resolution, small enough to keep one-second
    /// measurements within ~5 % of the target).
    ///
    /// Key design choices vs. the previous implementation:
    /// <list type="bullet">
    ///   <item>No free initial tokens – the first batch of packets is also rate-limited.</item>
    ///   <item>Wait time is computed from the exact deficit (<c>bytes - _tokens</c>),
    ///         not from <c>capacity - _tokens</c>, which previously under-waited.</item>
    ///   <item>A minimum wait floor of 5 ms prevents busy-spinning on very small deficits.</item>
    /// </list>
    /// </summary>
    private sealed class TokenBucket
    {
        private readonly object _sync = new();
        private double _tokens;
        private long _lastRefillTicks;
        private bool _initialized;

        public async Task WaitAsync(uint bytes, double bytesPerSecond, CancellationToken cancellationToken)
        {
            if (bytesPerSecond <= 0) return;

            // 50 ms burst window: keeps measured 1-second rate within ~5 % of the
            // target while being comfortably above Windows timer granularity.
            var capacity = Math.Max(bytes, bytesPerSecond * 0.05d);

            while (true)
            {
                var waitSeconds = 0d;
                lock (_sync)
                {
                    var now = Stopwatch.GetTimestamp();
                    if (!_initialized)
                    {
                        _lastRefillTicks = now;
                        _tokens = 0;          // No free burst on first call.
                        _initialized = true;
                    }

                    var elapsedSeconds = (double)(now - _lastRefillTicks) / Stopwatch.Frequency;
                    _tokens = Math.Min(capacity, _tokens + elapsedSeconds * bytesPerSecond);
                    _lastRefillTicks = now;

                    if (_tokens >= bytes)
                    {
                        _tokens -= bytes;
                        return;
                    }

                    // Wait for exactly the deficit, not to refill the whole capacity.
                    var deficit = bytes - _tokens;
                    waitSeconds = Math.Max(0.005d, deficit / bytesPerSecond);
                }

                await Task.Delay(TimeSpan.FromSeconds(waitSeconds), cancellationToken);
            }
        }
    }

    private sealed record ThrottleTarget(string ProcessName, double DownloadLimitBytesPerSecond, double UploadLimitBytesPerSecond);
    private readonly record struct BucketKey(string ProcessName, bool Upload);

    [StructLayout(LayoutKind.Explicit, Size = 80)]
    private struct WindivertAddress
    {
        [FieldOffset(8)] private uint _flags;
        public bool Outbound => (_flags & (1u << 17)) != 0;
    }

    [DllImport("WinDivert.dll", SetLastError = true, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private static extern IntPtr WinDivertOpen(string filter, int layer, short priority, ulong flags);
    [DllImport("WinDivert.dll", SetLastError = true, CallingConvention = CallingConvention.Cdecl)]
    private static extern bool WinDivertRecv(IntPtr handle, byte[] packet, uint packetLen, out uint recvLen, ref WindivertAddress address);
    [DllImport("WinDivert.dll", SetLastError = true, CallingConvention = CallingConvention.Cdecl)]
    private static extern bool WinDivertSend(IntPtr handle, byte[] packet, uint packetLen, out uint sendLen, ref WindivertAddress address);
    [DllImport("WinDivert.dll", SetLastError = true, CallingConvention = CallingConvention.Cdecl)]
    private static extern bool WinDivertShutdown(IntPtr handle, ulong how);
    [DllImport("WinDivert.dll", SetLastError = true, CallingConvention = CallingConvention.Cdecl)]
    private static extern bool WinDivertClose(IntPtr handle);
    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern int GetExtendedTcpTable(IntPtr table, ref int size, bool order, int ipVersion, int tableClass, uint reserved);
    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern int GetExtendedUdpTable(IntPtr table, ref int size, bool order, int ipVersion, int tableClass, uint reserved);
}
