using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;

namespace NetWatcher.App;

public enum SpeedTestPhase
{
    Idle,
    Preparing,
    Latency,
    Download,
    Upload,
    Completed,
    Failed,
    Cancelled
}

public sealed record SpeedTestProgress(
    SpeedTestPhase Phase,
    string StatusText,
    double InstantBytesPerSecond,
    double? PingMs,
    double? JitterMs,
    double? DownloadBytesPerSecond,
    double? UploadBytesPerSecond,
    string ServerInfo,
    double ProgressPercent);

public sealed record SpeedTestResult(
    bool Success,
    double? PingMs,
    double? JitterMs,
    double? DownloadBytesPerSecond,
    double? UploadBytesPerSecond,
    string ServerInfo,
    string? ErrorMessage,
    DateTimeOffset CompletedAt);

/// <summary>
/// Lightweight internet speed test modeled after consumer tools (latency → download → upload).
/// Uses Cloudflare's public measurement endpoints — no API key required.
/// </summary>
public sealed class SpeedTestService : IDisposable
{
    private const string BaseUrl = "https://speed.cloudflare.com";
    private const string MetaUrl = BaseUrl + "/meta";
    private const int LatencySamples = 10;
    private const int DownloadSeconds = 12;
    private const int UploadSeconds = 10;
    private const int ParallelStreams = 4;
    private const int DownloadChunkBytes = 25_000_000; // 25 MB per request
    private const int UploadChunkBytes = 4_000_000;    // 4 MB per request

    private readonly HttpClient _http;
    private bool _disposed;

    public SpeedTestService()
    {
        _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(45)
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("NetWatcher-SpeedTest/1.0");
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
    }

    public async Task<SpeedTestResult> RunAsync(
        IProgress<SpeedTestProgress>? progress,
        CancellationToken cancellationToken)
    {
        var serverInfo = "Cloudflare";
        double? pingMs = null;
        double? jitterMs = null;
        double? downloadBps = null;
        double? uploadBps = null;

        try
        {
            Report(progress, SpeedTestPhase.Preparing, "準備測速伺服器…", 0, serverInfo);

            serverInfo = await ResolveServerInfoAsync(cancellationToken).ConfigureAwait(false);
            Report(progress, SpeedTestPhase.Preparing, "已連線測速節點", 4, serverInfo,
                pingMs, jitterMs, downloadBps, uploadBps);

            cancellationToken.ThrowIfCancellationRequested();
            Report(progress, SpeedTestPhase.Latency, "正在測量延遲…", 8, serverInfo,
                pingMs, jitterMs, downloadBps, uploadBps);

            (pingMs, jitterMs) = await MeasureLatencyAsync(progress, serverInfo, cancellationToken)
                .ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            Report(progress, SpeedTestPhase.Download, "正在測試下載速度…", 20, serverInfo,
                pingMs, jitterMs, downloadBps, uploadBps, 0);

            downloadBps = await MeasureDownloadAsync(progress, serverInfo, pingMs, jitterMs, cancellationToken)
                .ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            Report(progress, SpeedTestPhase.Upload, "正在測試上傳速度…", 60, serverInfo,
                pingMs, jitterMs, downloadBps, uploadBps, 0);

            uploadBps = await MeasureUploadAsync(progress, serverInfo, pingMs, jitterMs, downloadBps, cancellationToken)
                .ConfigureAwait(false);

            Report(progress, SpeedTestPhase.Completed, "測速完成", 100, serverInfo,
                pingMs, jitterMs, downloadBps, uploadBps);

            return new SpeedTestResult(
                Success: true,
                PingMs: pingMs,
                JitterMs: jitterMs,
                DownloadBytesPerSecond: downloadBps,
                UploadBytesPerSecond: uploadBps,
                ServerInfo: serverInfo,
                ErrorMessage: null,
                CompletedAt: DateTimeOffset.Now);
        }
        catch (OperationCanceledException)
        {
            Report(progress, SpeedTestPhase.Cancelled, "測速已取消", 0, serverInfo,
                pingMs, jitterMs, downloadBps, uploadBps);
            return new SpeedTestResult(
                false, pingMs, jitterMs, downloadBps, uploadBps, serverInfo,
                "已取消", DateTimeOffset.Now);
        }
        catch (Exception ex)
        {
            var message = string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name : ex.Message;
            Report(progress, SpeedTestPhase.Failed, $"測速失敗：{message}", 0, serverInfo,
                pingMs, jitterMs, downloadBps, uploadBps);
            return new SpeedTestResult(
                false, pingMs, jitterMs, downloadBps, uploadBps, serverInfo,
                message, DateTimeOffset.Now);
        }
    }

    private async Task<string> ResolveServerInfoAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _http.GetAsync(MetaUrl, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            var root = doc.RootElement;
            var city = root.TryGetProperty("city", out var cityEl) ? cityEl.GetString() : null;
            var region = root.TryGetProperty("region", out var regionEl) ? regionEl.GetString() : null;
            var country = root.TryGetProperty("country", out var countryEl) ? countryEl.GetString() : null;
            var asn = root.TryGetProperty("asn", out var asnEl) ? asnEl.ToString() : null;
            var org = root.TryGetProperty("asOrganization", out var orgEl) ? orgEl.GetString() : null;

            var place = string.Join(", ",
                new[] { city, region, country }
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Select(s => s!.Trim()));

            if (string.IsNullOrWhiteSpace(place))
            {
                place = "Cloudflare";
            }

            if (!string.IsNullOrWhiteSpace(org))
            {
                return string.IsNullOrWhiteSpace(asn)
                    ? $"{place} · {org}"
                    : $"{place} · AS{asn} {org}";
            }

            return place;
        }
        catch
        {
            return "Cloudflare 邊緣節點";
        }
    }

    private async Task<(double PingMs, double JitterMs)> MeasureLatencyAsync(
        IProgress<SpeedTestProgress>? progress,
        string serverInfo,
        CancellationToken cancellationToken)
    {
        var samples = new List<double>(LatencySamples);
        var url = $"{BaseUrl}/__down?bytes=0";

        // Warm-up (discard).
        try
        {
            using var warm = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            _ = await warm.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Continue; first real sample may absorb cold-start cost.
        }

        for (var i = 0; i < LatencySamples; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sw = Stopwatch.StartNew();
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            _ = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            sw.Stop();

            if (response.IsSuccessStatusCode)
            {
                samples.Add(sw.Elapsed.TotalMilliseconds);
            }

            var pingSoFar = samples.Count > 0 ? samples.Min() : (double?)null;
            var jitterSoFar = samples.Count >= 2 ? ComputeJitter(samples) : (double?)null;
            var pct = 8 + (i + 1) * 10.0 / LatencySamples;
            Report(progress, SpeedTestPhase.Latency,
                $"測量延遲 {i + 1}/{LatencySamples}…",
                pct, serverInfo, pingSoFar, jitterSoFar, null, null);
        }

        if (samples.Count == 0)
        {
            throw new InvalidOperationException("無法取得延遲樣本，請檢查網路連線。");
        }

        return (samples.Min(), ComputeJitter(samples));
    }

    private async Task<double> MeasureDownloadAsync(
        IProgress<SpeedTestProgress>? progress,
        string serverInfo,
        double? pingMs,
        double? jitterMs,
        CancellationToken cancellationToken)
    {
        var totalBytes = 0L;
        var lockObj = new object();
        var started = Stopwatch.StartNew();
        var deadline = TimeSpan.FromSeconds(DownloadSeconds);
        var lastReport = Stopwatch.StartNew();

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(deadline + TimeSpan.FromSeconds(8));

        var workers = Enumerable.Range(0, ParallelStreams).Select(async __ =>
        {
            var buffer = new byte[64 * 1024];
            while (!linked.IsCancellationRequested && started.Elapsed < deadline)
            {
                try
                {
                    var url = $"{BaseUrl}/__down?bytes={DownloadChunkBytes}&r={Guid.NewGuid():N}";
                    using var response = await _http.GetAsync(
                        url, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        continue;
                    }

                    await using var stream = await response.Content.ReadAsStreamAsync(linked.Token)
                        .ConfigureAwait(false);
                    int read;
                    while ((read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), linked.Token)
                               .ConfigureAwait(false)) > 0)
                    {
                        long localTotal;
                        lock (lockObj)
                        {
                            totalBytes += read;
                            localTotal = totalBytes;
                        }

                        if (lastReport.ElapsedMilliseconds >= 200)
                        {
                            lastReport.Restart();
                            var elapsed = Math.Max(started.Elapsed.TotalSeconds, 0.001);
                            var bps = localTotal / elapsed;
                            var pct = 20 + Math.Min(38, started.Elapsed.TotalSeconds / DownloadSeconds * 38);
                            Report(progress, SpeedTestPhase.Download, "下載測試中…", pct, serverInfo,
                                pingMs, jitterMs, bps, null, bps);
                        }

                        if (started.Elapsed >= deadline)
                        {
                            break;
                        }
                    }
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception) when (!cancellationToken.IsCancellationRequested)
                {
                    // Retry other streams; one failed chunk should not kill the test.
                }
            }
        });

        await Task.WhenAll(workers).ConfigureAwait(false);

        var finalElapsed = Math.Max(started.Elapsed.TotalSeconds, 0.001);
        long finalBytes;
        lock (lockObj)
        {
            finalBytes = totalBytes;
        }

        if (finalBytes < 64 * 1024)
        {
            throw new InvalidOperationException("下載測試資料量過低，請稍後再試。");
        }

        return finalBytes / finalElapsed;
    }

    private async Task<double> MeasureUploadAsync(
        IProgress<SpeedTestProgress>? progress,
        string serverInfo,
        double? pingMs,
        double? jitterMs,
        double? downloadBps,
        CancellationToken cancellationToken)
    {
        var payload = new byte[UploadChunkBytes];
        Random.Shared.NextBytes(payload);

        var totalBytes = 0L;
        var lockObj = new object();
        var started = Stopwatch.StartNew();
        var deadline = TimeSpan.FromSeconds(UploadSeconds);
        var lastReport = Stopwatch.StartNew();

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(deadline + TimeSpan.FromSeconds(8));

        var workers = Enumerable.Range(0, ParallelStreams).Select(async __ =>
        {
            while (!linked.IsCancellationRequested && started.Elapsed < deadline)
            {
                try
                {
                    using var content = new ByteArrayContent(payload);
                    content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                    using var response = await _http.PostAsync($"{BaseUrl}/__up", content, linked.Token)
                        .ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        continue;
                    }

                    // Drain response body.
                    _ = await response.Content.ReadAsByteArrayAsync(linked.Token).ConfigureAwait(false);

                    long localTotal;
                    lock (lockObj)
                    {
                        totalBytes += payload.Length;
                        localTotal = totalBytes;
                    }

                    if (lastReport.ElapsedMilliseconds >= 200)
                    {
                        lastReport.Restart();
                        var elapsed = Math.Max(started.Elapsed.TotalSeconds, 0.001);
                        var bps = localTotal / elapsed;
                        var pct = 60 + Math.Min(38, started.Elapsed.TotalSeconds / UploadSeconds * 38);
                        Report(progress, SpeedTestPhase.Upload, "上傳測試中…", pct, serverInfo,
                            pingMs, jitterMs, downloadBps, bps, bps);
                    }
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception) when (!cancellationToken.IsCancellationRequested)
                {
                    // Keep trying on other streams.
                }
            }
        });

        await Task.WhenAll(workers).ConfigureAwait(false);

        var finalElapsed = Math.Max(started.Elapsed.TotalSeconds, 0.001);
        long finalBytes;
        lock (lockObj)
        {
            finalBytes = totalBytes;
        }

        if (finalBytes < 32 * 1024)
        {
            throw new InvalidOperationException("上傳測試資料量過低，請稍後再試。");
        }

        return finalBytes / finalElapsed;
    }

    private static double ComputeJitter(IReadOnlyList<double> samples)
    {
        if (samples.Count < 2)
        {
            return 0;
        }

        double sum = 0;
        for (var i = 1; i < samples.Count; i++)
        {
            sum += Math.Abs(samples[i] - samples[i - 1]);
        }

        return sum / (samples.Count - 1);
    }

    private static void Report(
        IProgress<SpeedTestProgress>? progress,
        SpeedTestPhase phase,
        string status,
        double percent,
        string serverInfo,
        double? pingMs = null,
        double? jitterMs = null,
        double? downloadBps = null,
        double? uploadBps = null,
        double instantBps = 0)
    {
        progress?.Report(new SpeedTestProgress(
            phase,
            status,
            instantBps,
            pingMs,
            jitterMs,
            downloadBps,
            uploadBps,
            serverInfo,
            Math.Clamp(percent, 0, 100)));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _http.Dispose();
    }
}
