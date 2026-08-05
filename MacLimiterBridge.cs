using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NetWatcher.App;

/// <summary>
/// Control-plane bridge to the signed native macOS Network Extension host.
/// The Avalonia process never reads the App Group directly; the native host owns
/// the rule store and NEFilterManager configuration.
/// </summary>
public sealed class MacLimiterBridge
{
    private const double LowPriorityUploadKbps = 128;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string? _hostPath;

    public MacLimiterBridge()
    {
        _hostPath = FindHostPath();
    }

    public bool IsAvailable => OperatingSystem.IsMacOS() && _hostPath is not null;

    public string CapabilityText => !OperatingSystem.IsMacOS()
        ? "僅供 macOS 使用。"
        : _hostPath is null
            ? "未安裝 macOS Limiter Host；程式限速尚不可用。"
            : "macOS Network Extension Host 已就緒；首次套用會要求系統核准。";

    public async Task<LimitApplyResult> ApplyAsync(
        int processId,
        TrafficPriority priority,
        double downloadLimitKbps,
        double uploadLimitKbps,
        bool isEnabled,
        CancellationToken cancellationToken = default)
    {
        if (!IsAvailable)
        {
            return LimitApplyResult.Fail(CapabilityText);
        }

        if (!isEnabled || priority is TrafficPriority.Normal or TrafficPriority.High)
        {
            return await SendAsync(new MacLimiterRequest
            {
                Command = "removeProcessRule",
                ProcessIdentifier = processId
            }, cancellationToken);
        }

        int? inbound = priority == TrafficPriority.Block || downloadLimitKbps <= 0
            ? null
            : ToBytesPerSecond(downloadLimitKbps);
        var outboundKbps = priority == TrafficPriority.Low && uploadLimitKbps <= 0
            ? LowPriorityUploadKbps
            : uploadLimitKbps;
        int? outbound = priority == TrafficPriority.Block || outboundKbps <= 0
            ? null
            : ToBytesPerSecond(outboundKbps);

        var enable = await SendAsync(new MacLimiterRequest
        {
            Command = "setEnabled",
            Enabled = true
        }, cancellationToken);
        if (!enable.Success)
        {
            return enable;
        }

        return await SendAsync(new MacLimiterRequest
        {
            Command = "upsertProcessRule",
            ProcessIdentifier = processId,
            InboundBytesPerSecond = inbound,
            OutboundBytesPerSecond = outbound,
            BlockConnections = priority == TrafficPriority.Block
        }, cancellationToken);
    }

    private async Task<LimitApplyResult> SendAsync(MacLimiterRequest request, CancellationToken cancellationToken)
    {
        if (_hostPath is null)
        {
            return LimitApplyResult.Fail(CapabilityText);
        }

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = _hostPath,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        try
        {
            process.Start();
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request, JsonOptions));
            process.StandardInput.Close();
            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            var output = await outputTask;
            var error = await errorTask;

            var response = JsonSerializer.Deserialize<MacLimiterResponse>(output, JsonOptions);
            if (response is null)
            {
                return LimitApplyResult.Fail(string.IsNullOrWhiteSpace(error)
                    ? "macOS Limiter Host 沒有回傳有效結果。"
                    : error.Trim());
            }

            return response.Success
                ? LimitApplyResult.Ok(FormatResponse(response))
                : LimitApplyResult.Fail(FormatResponse(response));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return LimitApplyResult.Fail($"macOS Limiter Host 失敗：{ex.Message}");
        }
    }

    private static string FormatResponse(MacLimiterResponse response)
    {
        if (!string.IsNullOrWhiteSpace(response.BundleIdentifier))
        {
            return $"{response.Message} · {response.BundleIdentifier}";
        }

        return response.Message;
    }

    private static int ToBytesPerSecond(double kibibytesPerSecond) =>
        (int)Math.Clamp(Math.Round(kibibytesPerSecond * 1024d), 1, int.MaxValue);

    private static string? FindHostPath()
    {
        var configured = Environment.GetEnvironmentVariable("NETWATCHER_LIMITER_HOST_PATH");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return configured;
        }

        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "NetWatcherLimiterHost.app", "Contents", "MacOS", "NetWatcherLimiterHost")
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    private sealed class MacLimiterRequest
    {
        public string Command { get; init; } = string.Empty;
        public bool? Enabled { get; init; }
        public int? ProcessIdentifier { get; init; }
        public int? InboundBytesPerSecond { get; init; }
        public int? OutboundBytesPerSecond { get; init; }
        public bool? BlockConnections { get; init; }
    }

    private sealed class MacLimiterResponse
    {
        public bool Success { get; init; }
        public string Message { get; init; } = string.Empty;
        public string? BundleIdentifier { get; init; }
    }
}
