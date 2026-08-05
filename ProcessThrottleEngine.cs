using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace NetWatcher.App;

/// <summary>
/// Packet-level process shaper backed by WinDivert.
/// Matching TCP/UDP packets (IPv4 + IPv6) are delayed in user mode and
/// re-injected on a virtual-clock schedule so the long-term average stays
/// at the configured MB/s (small burst for Windows timer granularity).
/// </summary>
public sealed class ProcessThrottleEngine : IDisposable
{
    private const int AfInet = 2;
    private const int AfInet6 = 23;
    private const int TcpTableOwnerPidAll = 5;
    private const int UdpTableOwnerPid = 1;
    private const int NetworkLayer = 0;
    private const ulong ShutdownBoth = 3;
    private const int WinDivertParamQueueLength = 0;
    private const int WinDivertParamQueueTime = 1;
    private const int WinDivertParamQueueSize = 2;
    private static readonly IntPtr InvalidHandle = new(-1);

    /// <summary>Max burst ahead of the schedule (seconds). Keeps 1s readings within a few %.</summary>
    private const double BurstSeconds = 0.05d;

    private readonly ConcurrentDictionary<string, ThrottleTarget> _targets = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<BucketKey, RateScheduler> _schedulers = new();
    private readonly object _lifecycleSync = new();
    private readonly object _ownershipSync = new();

    private volatile OwnershipSnapshot _ownership = OwnershipSnapshot.Empty;
    private CancellationTokenSource? _workerCts;
    private Task? _worker;
    private IntPtr _handle = IntPtr.Zero;
    private long _lastOwnershipRefresh;
    private bool _disposed;

    public string LastActionText { get; private set; } = "封包限速待命";

    public void SetLimit(string processName, double downloadLimitBytesPerSecond, double uploadLimitBytesPerSecond, bool enabled)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            return;
        }

        if (!enabled || (downloadLimitBytesPerSecond <= 0 && uploadLimitBytesPerSecond <= 0))
        {
            _targets.TryRemove(processName, out _);
            _schedulers.TryRemove(new BucketKey(processName, false), out _);
            _schedulers.TryRemove(new BucketKey(processName, true), out _);
        }
        else
        {
            var dl = Math.Max(0, downloadLimitBytesPerSecond);
            var ul = Math.Max(0, uploadLimitBytesPerSecond);
            _targets[processName] = new ThrottleTarget(processName, dl, ul);

            // Reset schedulers so a new rate does not inherit old debt/credit.
            _schedulers.AddOrUpdate(
                new BucketKey(processName, false),
                _ => new RateScheduler(),
                (_, _) => new RateScheduler());
            _schedulers.AddOrUpdate(
                new BucketKey(processName, true),
                _ => new RateScheduler(),
                (_, _) => new RateScheduler());

            LastActionText =
                $"{processName} 限速設定：↓{FormatRate(dl)} ↑{FormatRate(ul)}";
        }

        RefreshOwnership(force: true);
        if (!_targets.IsEmpty)
        {
            EnsureWorker();
        }
    }

    public void Clear(string processName)
    {
        _targets.TryRemove(processName, out _);
        _schedulers.TryRemove(new BucketKey(processName, false), out _);
        _schedulers.TryRemove(new BucketKey(processName, true), out _);
        RefreshOwnership(force: true);
        if (_targets.IsEmpty)
        {
            LastActionText = "已移除封包限速";
        }
    }

    public void ClearAll()
    {
        _targets.Clear();
        _schedulers.Clear();
        _ownership = OwnershipSnapshot.Empty;
        LastActionText = "已移除封包限速";
    }

    /// <summary>
    /// Called ~1s from the UI sample loop; refreshes PID/port ownership.
    /// Shaping itself happens on every diverted packet.
    /// </summary>
    public void OnSample(string processName, double measuredDownloadBps, double measuredUploadBps) =>
        RefreshOwnership(force: false);

    private static string FormatRate(double bytesPerSecond) =>
        bytesPerSecond <= 0
            ? "不限"
            : $"{bytesPerSecond / TrafficFormatter.BytesPerMBps:0.##} MB/s";

    private void EnsureWorker()
    {
        if (!OperatingSystem.IsWindows() || _disposed)
        {
            return;
        }

        lock (_lifecycleSync)
        {
            if (_worker is { IsCompleted: false })
            {
                return;
            }

            _workerCts = new CancellationTokenSource();
            _worker = Task.Run(() => RunAsync(_workerCts.Token));
        }
    }

    private void RefreshOwnership(bool force)
    {
        if (!OperatingSystem.IsWindows() || _targets.IsEmpty)
        {
            if (_targets.IsEmpty)
            {
                _ownership = OwnershipSnapshot.Empty;
            }

            return;
        }

        var now = Stopwatch.GetTimestamp();
        // Refresh at least twice per second so new connections are caught quickly.
        if (!force && now - Interlocked.Read(ref _lastOwnershipRefresh) < Stopwatch.Frequency / 2)
        {
            return;
        }

        Interlocked.Exchange(ref _lastOwnershipRefresh, now);

        var byPid = new Dictionary<int, ThrottleTarget>();
        foreach (var target in _targets.Values)
        {
            var name = Path.GetFileNameWithoutExtension(target.ProcessName);
            try
            {
                foreach (var process in Process.GetProcessesByName(name))
                {
                    try
                    {
                        byPid[process.Id] = target;
                    }
                    finally
                    {
                        process.Dispose();
                    }
                }
            }
            catch
            {
                // Process may have exited.
            }
        }

        var byPort = new Dictionary<PortKey, int>();
        if (byPid.Count > 0)
        {
            var wanted = byPid.Keys.ToHashSet();
            CollectTcpOwners(AfInet, rowSize: 24, portOffset: 8, pidOffset: 20, wanted, byPort);
            CollectTcpOwners(AfInet6, rowSize: 56, portOffset: 20, pidOffset: 52, wanted, byPort);
            CollectUdpOwners(AfInet, rowSize: 12, portOffset: 4, pidOffset: 8, wanted, byPort);
            CollectUdpOwners(AfInet6, rowSize: 28, portOffset: 20, pidOffset: 24, wanted, byPort);
        }

        // Atomic swap — readers never see a half-cleared map.
        lock (_ownershipSync)
        {
            _ownership = new OwnershipSnapshot(byPid, byPort);
        }
    }

    private static void CollectTcpOwners(
        int family,
        int rowSize,
        int portOffset,
        int pidOffset,
        HashSet<int> wanted,
        Dictionary<PortKey, int> byPort)
    {
        var size = 0;
        if (GetExtendedTcpTable(IntPtr.Zero, ref size, true, family, TcpTableOwnerPidAll, 0) != 122 || size <= 4)
        {
            return;
        }

        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(buffer, ref size, true, family, TcpTableOwnerPidAll, 0) != 0)
            {
                return;
            }

            var count = Marshal.ReadInt32(buffer);
            for (var index = 0; index < count; index++)
            {
                var row = IntPtr.Add(buffer, 4 + index * rowSize);
                var pid = Marshal.ReadInt32(row, pidOffset);
                if (!wanted.Contains(pid))
                {
                    continue;
                }

                var rawPort = unchecked((uint)Marshal.ReadInt32(row, portOffset));
                var port = Ntohs(rawPort);
                if (port != 0)
                {
                    byPort[new PortKey(6, port)] = pid;
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static void CollectUdpOwners(
        int family,
        int rowSize,
        int portOffset,
        int pidOffset,
        HashSet<int> wanted,
        Dictionary<PortKey, int> byPort)
    {
        var size = 0;
        if (GetExtendedUdpTable(IntPtr.Zero, ref size, true, family, UdpTableOwnerPid, 0) != 122 || size <= 4)
        {
            return;
        }

        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedUdpTable(buffer, ref size, true, family, UdpTableOwnerPid, 0) != 0)
            {
                return;
            }

            var count = Marshal.ReadInt32(buffer);
            for (var index = 0; index < count; index++)
            {
                var row = IntPtr.Add(buffer, 4 + index * rowSize);
                var pid = Marshal.ReadInt32(row, pidOffset);
                if (!wanted.Contains(pid))
                {
                    continue;
                }

                var rawPort = unchecked((uint)Marshal.ReadInt32(row, portOffset));
                var port = Ntohs(rawPort);
                if (port != 0)
                {
                    byPort[new PortKey(17, port)] = pid;
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static ushort Ntohs(uint rawPort) =>
        (ushort)(((rawPort & 0xFF) << 8) | ((rawPort >> 8) & 0xFF));

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            _handle = WinDivertOpen("tcp or udp", NetworkLayer, 0, 0);
            if (_handle == InvalidHandle || _handle == IntPtr.Zero)
            {
                var error = Marshal.GetLastWin32Error();
                LastActionText = error == 5
                    ? "封包限速需要系統管理員權限"
                    : $"封包限速無法啟動（WinDivert：{error}）";
                return;
            }

            // Larger queues so delayed reinjection does not drop packets under load.
            WinDivertSetParam(_handle, WinDivertParamQueueLength, 16384);
            WinDivertSetParam(_handle, WinDivertParamQueueTime, 8000);
            WinDivertSetParam(_handle, WinDivertParamQueueSize, 32 * 1024 * 1024);

            LastActionText = "封包限速已啟動";

            // Single-threaded recv so WinDivert ordering stays sane.  Matching
            // packets that need delay are scheduled and re-injected on a worker
            // task — non-matching traffic is never blocked behind a throttle wait.
            var packet = new byte[0xFFFF];
            while (!cancellationToken.IsCancellationRequested)
            {
                var address = new WindivertAddress();
                if (!WinDivertRecv(_handle, packet, (uint)packet.Length, out var length, ref address))
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }

                    LastActionText = $"封包限速接收失敗（{Marshal.GetLastWin32Error()}）";
                    break;
                }

                if (TryGetTarget(packet, (int)length, address.Outbound, out var target, out var upload))
                {
                    var rate = upload
                        ? target.UploadLimitBytesPerSecond
                        : target.DownloadLimitBytesPerSecond;

                    if (rate > 0)
                    {
                        var scheduler = _schedulers.GetOrAdd(
                            new BucketKey(target.ProcessName, upload),
                            _ => new RateScheduler());
                        var delay = scheduler.Schedule(length, rate);

                        LastActionText =
                            $"{target.ProcessName} 封包限速中：{(upload ? "↑" : "↓")}{rate / TrafficFormatter.BytesPerMBps:0.##} MB/s";

                        if (delay > TimeSpan.Zero)
                        {
                            // Copy only when we must hold the packet past this loop iteration.
                            var held = new byte[length];
                            Buffer.BlockCopy(packet, 0, held, 0, (int)length);
                            var heldAddress = address;
                            var heldLength = length;
                            _ = ReinjectionAsync(held, heldLength, heldAddress, delay, cancellationToken);
                            continue;
                        }
                    }
                }

                if (!WinDivertSend(_handle, packet, length, out _, ref address))
                {
                    LastActionText = $"封包限速重新注入失敗（{Marshal.GetLastWin32Error()}）";
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (DllNotFoundException)
        {
            LastActionText = "缺少 WinDivert.dll，無法啟動真正限速";
        }
        catch (Exception ex)
        {
            LastActionText = $"封包限速錯誤：{ex.Message}";
        }
        finally
        {
            // Dispose may have already closed the handle; only close if we still own it.
            var handle = Interlocked.Exchange(ref _handle, IntPtr.Zero);
            if (handle != IntPtr.Zero && handle != InvalidHandle)
            {
                try
                {
                    WinDivertClose(handle);
                }
                catch
                {
                    // ignore
                }
            }
        }
    }

    private async Task ReinjectionAsync(
        byte[] packet,
        uint length,
        WindivertAddress address,
        TimeSpan delay,
        CancellationToken cancellationToken)
    {
        try
        {
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }

            var handle = Volatile.Read(ref _handle);
            if (handle == IntPtr.Zero || handle == InvalidHandle)
            {
                return;
            }

            if (!WinDivertSend(handle, packet, length, out _, ref address))
            {
                LastActionText = $"封包限速重新注入失敗（{Marshal.GetLastWin32Error()}）";
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            LastActionText = $"封包處理錯誤：{ex.Message}";
        }
    }

    private bool TryGetTarget(byte[] packet, int length, bool outbound, out ThrottleTarget target, out bool upload)
    {
        target = default!;
        upload = outbound;
        if (length < 1)
        {
            return false;
        }

        var version = packet[0] >> 4;
        int headerLength;
        int protocolOffset;
        if (version == 4)
        {
            headerLength = (packet[0] & 0x0F) * 4;
            protocolOffset = 9;
        }
        else if (version == 6)
        {
            headerLength = 40;
            protocolOffset = 6;
        }
        else
        {
            return false;
        }

        if (headerLength < 20 || length < headerLength + 4)
        {
            return false;
        }

        var protocol = packet[protocolOffset];
        if (protocol is not 6 and not 17)
        {
            return false;
        }

        var sourcePort = (ushort)((packet[headerLength] << 8) | packet[headerLength + 1]);
        var destinationPort = (ushort)((packet[headerLength + 2] << 8) | packet[headerLength + 3]);
        var localPort = outbound ? sourcePort : destinationPort;

        var snap = _ownership;
        return snap.ByPort.TryGetValue(new PortKey(protocol, localPort), out var pid)
               && snap.ByPid.TryGetValue(pid, out target!);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        IntPtr handleToClose = IntPtr.Zero;
        lock (_lifecycleSync)
        {
            try
            {
                _workerCts?.Cancel();
            }
            catch
            {
                // ignore
            }

            // Unblock WinDivertRecv immediately so the UI is not stuck on worker.Wait.
            var handle = Interlocked.Exchange(ref _handle, IntPtr.Zero);
            if (handle != IntPtr.Zero && handle != InvalidHandle)
            {
                try
                {
                    WinDivertShutdown(handle, ShutdownBoth);
                }
                catch
                {
                    // ignore
                }

                handleToClose = handle;
            }
        }

        // Short wait only — never stall window close for seconds.
        try
        {
            _worker?.Wait(TimeSpan.FromMilliseconds(250));
        }
        catch
        {
            // ignore shutdown races
        }

        if (handleToClose != IntPtr.Zero)
        {
            try
            {
                WinDivertClose(handleToClose);
            }
            catch
            {
                // ignore
            }
        }

        try
        {
            _workerCts?.Dispose();
        }
        catch
        {
            // ignore
        }

        ClearAll();
    }

    /// <summary>
    /// Virtual-clock rate scheduler.  Each accepted packet advances a timeline
    /// by bytes/rate.  Long-term throughput equals <paramref name="bytesPerSecond"/>;
    /// a short burst window absorbs Windows timer jitter without free unlimited bursts.
    /// </summary>
    private sealed class RateScheduler
    {
        private readonly object _sync = new();
        private long _nextSendTicks;

        public TimeSpan Schedule(uint bytes, double bytesPerSecond)
        {
            if (bytesPerSecond <= 0 || bytes == 0)
            {
                return TimeSpan.Zero;
            }

            lock (_sync)
            {
                var now = Stopwatch.GetTimestamp();
                var costTicks = (long)Math.Ceiling(bytes / bytesPerSecond * Stopwatch.Frequency);
                if (costTicks < 1)
                {
                    costTicks = 1;
                }

                var burstTicks = (long)(BurstSeconds * Stopwatch.Frequency);

                // If we are idle longer than the burst window, do not bank unlimited credit —
                // only allow up to BurstSeconds of free head-start.
                if (_nextSendTicks < now - burstTicks)
                {
                    _nextSendTicks = now - burstTicks;
                }

                var startTicks = Math.Max(now, _nextSendTicks);
                var delayTicks = startTicks - now;
                _nextSendTicks = startTicks + costTicks;

                if (delayTicks <= 0)
                {
                    return TimeSpan.Zero;
                }

                return TimeSpan.FromSeconds((double)delayTicks / Stopwatch.Frequency);
            }
        }
    }

    private sealed class OwnershipSnapshot
    {
        public static readonly OwnershipSnapshot Empty = new(
            new Dictionary<int, ThrottleTarget>(),
            new Dictionary<PortKey, int>());

        public OwnershipSnapshot(
            Dictionary<int, ThrottleTarget> byPid,
            Dictionary<PortKey, int> byPort)
        {
            ByPid = byPid;
            ByPort = byPort;
        }

        public Dictionary<int, ThrottleTarget> ByPid { get; }
        public Dictionary<PortKey, int> ByPort { get; }
    }

    private sealed record ThrottleTarget(
        string ProcessName,
        double DownloadLimitBytesPerSecond,
        double UploadLimitBytesPerSecond);

    private readonly record struct BucketKey(string ProcessName, bool Upload);
    private readonly record struct PortKey(byte Protocol, ushort Port);

    [StructLayout(LayoutKind.Explicit, Size = 80)]
    private struct WindivertAddress
    {
        // WinDivert 2.2: Timestamp@0, then UINT32 bitfields at offset 8:
        // Layer:8 Event:8 Sniffed:1 Outbound:1 ...
        [FieldOffset(8)] private readonly uint _flags;

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

    [DllImport("WinDivert.dll", SetLastError = true, CallingConvention = CallingConvention.Cdecl)]
    private static extern bool WinDivertSetParam(IntPtr handle, int param, ulong value);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern int GetExtendedTcpTable(IntPtr table, ref int size, bool order, int ipVersion, int tableClass, uint reserved);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern int GetExtendedUdpTable(IntPtr table, ref int size, bool order, int ipVersion, int tableClass, uint reserved);
}
