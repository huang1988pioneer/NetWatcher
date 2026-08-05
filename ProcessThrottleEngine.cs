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

    /// <summary>
    /// Max burst credit (seconds). ~15ms ≈ 1.5% of a 1s UI sample at steady state,
    /// so a 1 MB/s limit stays within a small overshoot band without looking "unlimited".
    /// </summary>
    private const double BurstSeconds = 0.015d;

    private readonly ConcurrentDictionary<string, ThrottleTarget> _targets = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<BucketKey, PacedLane> _lanes = new();
    private readonly object _lifecycleSync = new();
    private readonly object _ownershipSync = new();

    private volatile OwnershipSnapshot _ownership = OwnershipSnapshot.Empty;
    private CancellationTokenSource? _workerCts;
    private Task? _worker;
    private IntPtr _handle = IntPtr.Zero;
    private long _lastOwnershipRefresh;
    private long _lastWorkerStartTicks;
    private bool _permanentStartFailure;
    private bool _disposed;

    public string LastActionText { get; private set; } = "封包限速待命";

    /// <summary>
    /// True when the last engine status indicates WinDivert could not start or stay running.
    /// </summary>
    public bool HasStartupFailure
    {
        get
        {
            if (_permanentStartFailure)
            {
                return true;
            }

            var text = LastActionText;
            return text.Contains("無法", StringComparison.Ordinal) ||
                   text.Contains("缺少", StringComparison.Ordinal) ||
                   text.Contains("需要系統管理員", StringComparison.Ordinal) ||
                   text.Contains("錯誤", StringComparison.Ordinal) ||
                   text.Contains("接收失敗", StringComparison.Ordinal) ||
                   text.Contains("重新注入失敗", StringComparison.Ordinal) ||
                   text.Contains("尚未就緒", StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// True once WinDivert opened successfully (or is actively shaping packets).
    /// </summary>
    public bool IsEngineRunning
    {
        get
        {
            var handle = Volatile.Read(ref _handle);
            return handle != IntPtr.Zero && handle != InvalidHandle;
        }
    }

    /// <summary>
    /// Waits briefly for the WinDivert worker to open a handle or report failure after SetLimit.
    /// </summary>
    public async Task<string> WaitForStartupAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (IsEngineRunning || HasStartupFailure)
            {
                return LastActionText;
            }

            await Task.Delay(40, cancellationToken).ConfigureAwait(false);
        }

        // Still "待命/設定" after timeout usually means DLL load hung or worker never started.
        if (!IsEngineRunning && !HasStartupFailure && !_targets.IsEmpty)
        {
            LastActionText = "封包限速尚未就緒（請確認以系統管理員執行，且 WinDivert.dll / WinDivert64.sys 與程式同目錄）";
        }

        return LastActionText;
    }

    public void SetLimit(string processName, double downloadLimitBytesPerSecond, double uploadLimitBytesPerSecond, bool enabled)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            return;
        }

        if (!enabled || (downloadLimitBytesPerSecond <= 0 && uploadLimitBytesPerSecond <= 0))
        {
            _targets.TryRemove(processName, out _);
            DropLane(new BucketKey(processName, false));
            DropLane(new BucketKey(processName, true));
        }
        else
        {
            var dl = Math.Max(0, downloadLimitBytesPerSecond);
            var ul = Math.Max(0, uploadLimitBytesPerSecond);
            _targets[processName] = new ThrottleTarget(processName, dl, ul);

            // Reset paced lanes so a new rate does not inherit old debt/credit.
            ReplaceLane(new BucketKey(processName, false));
            ReplaceLane(new BucketKey(processName, true));

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
        DropLane(new BucketKey(processName, false));
        DropLane(new BucketKey(processName, true));
        RefreshOwnership(force: true);
        if (_targets.IsEmpty)
        {
            LastActionText = "已移除封包限速";
        }
    }

    public void ClearAll()
    {
        _targets.Clear();
        foreach (var key in _lanes.Keys.ToArray())
        {
            DropLane(key);
        }

        _ownership = OwnershipSnapshot.Empty;
        LastActionText = "已移除封包限速";
    }

    private void ReplaceLane(BucketKey key)
    {
        DropLane(key);
        _lanes[key] = new PacedLane(this);
    }

    private void DropLane(BucketKey key)
    {
        if (_lanes.TryRemove(key, out var lane))
        {
            lane.Cancel();
        }
    }

    /// <summary>True when this process name currently has an active throttle target.</summary>
    public bool HasLimit(string processName) =>
        !string.IsNullOrWhiteSpace(processName) && _targets.ContainsKey(processName);

    /// <summary>
    /// Called ~1s from the UI sample loop; refreshes PID/port ownership and
    /// restarts the WinDivert worker if it died while limits are still active.
    /// Shaping itself happens on every diverted packet.
    /// </summary>
    public void OnSample(string processName, double measuredDownloadBps, double measuredUploadBps)
    {
        RefreshOwnership(force: false);
        if (_targets.IsEmpty || IsEngineRunning || _permanentStartFailure)
        {
            return;
        }

        // Transient failures (recv error, driver blip): retry at most every 2s.
        var now = Stopwatch.GetTimestamp();
        if (now - Interlocked.Read(ref _lastWorkerStartTicks) < Stopwatch.Frequency * 2)
        {
            return;
        }

        EnsureWorker();
    }

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

            // Previous worker may have exited after an error — drop its CTS before restart.
            try
            {
                _workerCts?.Cancel();
            }
            catch
            {
                // ignore
            }

            try
            {
                _workerCts?.Dispose();
            }
            catch
            {
                // ignore
            }

            Interlocked.Exchange(ref _lastWorkerStartTicks, Stopwatch.GetTimestamp());
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
        // Refresh ~4×/s so new browser/worker connections are captured before free traffic escapes.
        if (!force && now - Interlocked.Read(ref _lastOwnershipRefresh) < Stopwatch.Frequency / 4)
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
        // Retry: table size can grow between the size query and the real read.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var size = 0;
            var probe = GetExtendedTcpTable(IntPtr.Zero, ref size, true, family, TcpTableOwnerPidAll, 0);
            if (probe != 122 || size <= 4)
            {
                return;
            }

            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                var status = GetExtendedTcpTable(buffer, ref size, true, family, TcpTableOwnerPidAll, 0);
                if (status == 122)
                {
                    // Buffer too small — grow and retry.
                    continue;
                }

                if (status != 0)
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

                return;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
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
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var size = 0;
            var probe = GetExtendedUdpTable(IntPtr.Zero, ref size, true, family, UdpTableOwnerPid, 0);
            if (probe != 122 || size <= 4)
            {
                return;
            }

            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                var status = GetExtendedUdpTable(buffer, ref size, true, family, UdpTableOwnerPid, 0);
                if (status == 122)
                {
                    continue;
                }

                if (status != 0)
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

                return;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
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
                // Access denied / missing driver are not fixed by spinning restarts.
                _permanentStartFailure = error is 5 or 2 or 3 or 126 or 127;
                LastActionText = error == 5
                    ? "封包限速需要系統管理員權限"
                    : $"封包限速無法啟動（WinDivert：{error}）";
                return;
            }

            _permanentStartFailure = false;

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
                        // Always pace through a serial lane: concurrent Task.Delay reinjects
                        // undershoot Windows timer quanta and the observed rate can far exceed
                        // the configured MB/s (e.g. "1 MB/s" looking unrestricted).
                        var held = new byte[length];
                        Buffer.BlockCopy(packet, 0, held, 0, (int)length);
                        var key = new BucketKey(target.ProcessName, upload);
                        var lane = _lanes.GetOrAdd(key, static (_, engine) => new PacedLane(engine), this);
                        lane.Enqueue(held, length, address, rate, cancellationToken);

                        LastActionText =
                            $"{target.ProcessName} 限速中：{(upload ? "↑" : "↓")}{rate / TrafficFormatter.BytesPerMBps:0.##} MB/s（趨近目標）";
                        continue;
                    }
                }

                SendPacket(packet, length, ref address);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (DllNotFoundException)
        {
            _permanentStartFailure = true;
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

    private void SendPacket(byte[] packet, uint length, ref WindivertAddress address)
    {
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

    /// <summary>
    /// Sleep until an absolute <see cref="Stopwatch"/> deadline.
    /// Uses Task.Delay for long waits, then spin-finishes the last ~1–1.5ms so
    /// short inter-packet gaps at 1 MB/s (~1.4ms per MTU) are not rounded away
    /// by the default ~15.6ms Windows timer quantum.
    /// </summary>
    private static async Task WaitUntilAsync(long deadlineTicks, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remaining = deadlineTicks - Stopwatch.GetTimestamp();
            if (remaining <= 0)
            {
                return;
            }

            var ms = remaining * 1000.0 / Stopwatch.Frequency;
            if (ms > 1.5)
            {
                var sleepMs = Math.Max(1, (int)Math.Floor(ms - 1.0));
                await Task.Delay(sleepMs, cancellationToken).ConfigureAwait(false);
                continue;
            }

            while (Stopwatch.GetTimestamp() < deadlineTicks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Thread.SpinWait(64);
            }

            return;
        }
    }

    private bool TryGetTarget(byte[] packet, int length, bool outbound, out ThrottleTarget target, out bool upload)
    {
        target = default!;
        // Direction from WinDivert: outbound = process upload, inbound = download.
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
        // Client local port is the source for outbound, destination for inbound.
        var primaryLocal = outbound ? sourcePort : destinationPort;
        var fallbackLocal = outbound ? destinationPort : sourcePort;

        var snap = _ownership;
        if (TryMatchPort(snap, protocol, primaryLocal, out target!))
        {
            return true;
        }

        // Fallback: if Outbound bit were misread, the other port may still map to a target.
        // Keep the WinDivert direction for upload/download rate selection.
        return TryMatchPort(snap, protocol, fallbackLocal, out target!);
    }

    private static bool TryMatchPort(
        OwnershipSnapshot snap,
        byte protocol,
        ushort localPort,
        out ThrottleTarget target)
    {
        target = default!;
        return localPort != 0 &&
               snap.ByPort.TryGetValue(new PortKey(protocol, localPort), out var pid) &&
               snap.ByPid.TryGetValue(pid, out target!);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var key in _lanes.Keys.ToArray())
        {
            DropLane(key);
        }

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
    /// Serial paced reinjection for one (process, direction). Packets are sent in order
    /// on a virtual clock so long-term average converges to the configured MB/s.
    /// </summary>
    private sealed class PacedLane
    {
        private readonly ProcessThrottleEngine _engine;
        private readonly ConcurrentQueue<HeldPacket> _queue = new();
        private readonly RateScheduler _scheduler = new();
        private int _pumpRunning;
        private volatile bool _cancelled;

        public PacedLane(ProcessThrottleEngine engine)
        {
            _engine = engine;
        }

        public void Cancel() => _cancelled = true;

        public void Enqueue(
            byte[] packet,
            uint length,
            WindivertAddress address,
            double bytesPerSecond,
            CancellationToken cancellationToken)
        {
            if (_cancelled || bytesPerSecond <= 0 || length == 0)
            {
                return;
            }

            _queue.Enqueue(new HeldPacket(packet, length, address, bytesPerSecond));
            EnsurePump(cancellationToken);
        }

        private void EnsurePump(CancellationToken cancellationToken)
        {
            if (Interlocked.CompareExchange(ref _pumpRunning, 1, 0) != 0)
            {
                return;
            }

            _ = PumpAsync(cancellationToken);
        }

        private async Task PumpAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!_cancelled && !cancellationToken.IsCancellationRequested)
                {
                    if (!_queue.TryDequeue(out var item))
                    {
                        break;
                    }

                    var deadline = _scheduler.Reserve(item.Length, item.BytesPerSecond);
                    await WaitUntilAsync(deadline, cancellationToken).ConfigureAwait(false);

                    if (_cancelled || cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }

                    var address = item.Address;
                    _engine.SendPacket(item.Packet, item.Length, ref address);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _engine.LastActionText = $"封包處理錯誤：{ex.Message}";
            }
            finally
            {
                Interlocked.Exchange(ref _pumpRunning, 0);

                // Race: items may have been enqueued after we saw empty and before we cleared the flag.
                if (!_cancelled &&
                    !cancellationToken.IsCancellationRequested &&
                    !_queue.IsEmpty)
                {
                    EnsurePump(cancellationToken);
                }
            }
        }

        private readonly record struct HeldPacket(
            byte[] Packet,
            uint Length,
            WindivertAddress Address,
            double BytesPerSecond);
    }

    /// <summary>
    /// Virtual-clock rate scheduler. Each accepted packet advances a timeline by
    /// bytes/rate. Long-term throughput converges to <paramref name="bytesPerSecond"/>;
    /// a short burst credit absorbs timer granularity without free unlimited bursts.
    /// </summary>
    private sealed class RateScheduler
    {
        private readonly object _sync = new();
        private long _nextSendTicks;

        /// <summary>Reserve transmission of <paramref name="bytes"/>; returns absolute Stopwatch deadline.</summary>
        public long Reserve(uint bytes, double bytesPerSecond)
        {
            if (bytesPerSecond <= 0 || bytes == 0)
            {
                return Stopwatch.GetTimestamp();
            }

            lock (_sync)
            {
                var now = Stopwatch.GetTimestamp();
                // Exact cost: floor would slightly overshoot; ceiling slightly undershoots.
                // Prefer mild undershoot so 1 MB/s UI samples sit at or just under the target.
                var costTicks = (long)Math.Ceiling(bytes / bytesPerSecond * Stopwatch.Frequency);
                if (costTicks < 1)
                {
                    costTicks = 1;
                }

                var burstTicks = (long)(BurstSeconds * Stopwatch.Frequency);

                // Cap idle credit — no banking unlimited free burst after a pause.
                if (_nextSendTicks < now - burstTicks)
                {
                    _nextSendTicks = now - burstTicks;
                }

                var startTicks = Math.Max(now, _nextSendTicks);
                _nextSendTicks = startTicks + costTicks;
                return startTicks;
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
