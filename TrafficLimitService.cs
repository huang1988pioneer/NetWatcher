using System.Diagnostics;
using System.Text;

namespace NetWatcher.App;

/// <summary>
/// Traffic shaping helpers inspired by NetBalancer / Eltrafico.
/// Uses Windows Policy-based QoS for outbound (upload) throttling and Firewall for Block.
/// True inbound (download) throttling requires a kernel filter driver (not available here).
/// </summary>
public sealed class TrafficLimitService : IDisposable
{
    private const string PolicyPrefix = "NetWatcher_";
    private const string FirewallPrefix = "NetWatcher_Block_";
    private const double LowPriorityUploadKbps = 128;

    private readonly object _sync = new();
    private readonly HashSet<string> _activePolicies = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _activeFirewallRules = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public bool IsWindowsSupported => OperatingSystem.IsWindows();

    public bool IsElevated => AdminElevation.IsElevated();

    public string CapabilityText
    {
        get
        {
            if (!IsWindowsSupported)
            {
                return "限速/優先級：目前僅 Windows 可實際套用；其他平台僅保留設定。";
            }

            if (!IsElevated)
            {
                return "限速需要系統管理員權限。目前未以系統管理員執行，無法套用 QoS / 防火牆規則。請按「以系統管理員重新啟動」。";
            }

            return "已取得系統管理員權限。上傳限速：Windows QoS（有效）· 下載限速：Windows 無法可靠限制入站，僅記錄設定 · Block：防火牆阻擋。";
        }
    }

    public async Task<LimitApplyResult> ApplyPriorityAsync(
        string processName,
        string? executablePath,
        TrafficPriority priority,
        double downloadLimitKbps,
        double uploadLimitKbps,
        CancellationToken cancellationToken = default)
    {
        if (!IsWindowsSupported)
        {
            return LimitApplyResult.Fail("目前平台不支援實際限速。");
        }

        if (!IsElevated && priority is not TrafficPriority.Normal and not TrafficPriority.High)
        {
            return LimitApplyResult.Fail("需要系統管理員權限才能限速。請在設定頁按「以系統管理員重新啟動」。");
        }

        var messages = new List<string>();
        var anyFail = false;

        // Always clear previous block first unless re-applying Block.
        if (priority != TrafficPriority.Block)
        {
            var unblock = await SetBlockAsync(processName, executablePath, enable: false, cancellationToken);
            if (!string.IsNullOrWhiteSpace(unblock.Message) && !unblock.Success)
            {
                messages.Add(unblock.Message);
            }
        }

        switch (priority)
        {
            case TrafficPriority.High:
            case TrafficPriority.Normal:
            {
                var remove = await RemoveLimitAsync(processName, "UL", cancellationToken);
                messages.Add(priority == TrafficPriority.High
                    ? "優先級 High：不限速"
                    : "已解除限速（Normal）");
                if (!remove.Success)
                {
                    anyFail = true;
                    messages.Add(remove.Message);
                }

                break;
            }

            case TrafficPriority.Low:
            {
                var lowRate = uploadLimitKbps > 0 ? uploadLimitKbps : LowPriorityUploadKbps;
                var low = await ApplyUploadLimitAsync(processName, lowRate, cancellationToken);
                messages.Add(low.Message);
                anyFail |= !low.Success;

                if (downloadLimitKbps > 0)
                {
                    var dl = await ApplyDownloadLimitAsync(processName, downloadLimitKbps, cancellationToken);
                    messages.Add(dl.Message);
                }

                break;
            }

            case TrafficPriority.Limit:
            {
                if (uploadLimitKbps > 0)
                {
                    var ul = await ApplyUploadLimitAsync(processName, uploadLimitKbps, cancellationToken);
                    messages.Add(ul.Message);
                    anyFail |= !ul.Success;
                }
                else
                {
                    await RemoveLimitAsync(processName, "UL", cancellationToken);
                    if (downloadLimitKbps <= 0)
                    {
                        messages.Add("請選擇上傳或下載限制值");
                        anyFail = true;
                    }
                }

                if (downloadLimitKbps > 0)
                {
                    var dl = await ApplyDownloadLimitAsync(processName, downloadLimitKbps, cancellationToken);
                    messages.Add(dl.Message);
                    // Download cannot be enforced — not counted as hard fail if upload succeeded.
                }

                break;
            }

            case TrafficPriority.Block:
            {
                await RemoveLimitAsync(processName, "UL", cancellationToken);
                var block = await SetBlockAsync(processName, executablePath, enable: true, cancellationToken);
                messages.Add(block.Message);
                anyFail |= !block.Success;
                break;
            }
        }

        var text = string.Join(" · ", messages.Where(m => !string.IsNullOrWhiteSpace(m)).TakeLast(3));
        return anyFail ? LimitApplyResult.Fail(text) : LimitApplyResult.Ok(text);
    }

    public async Task<LimitApplyResult> ApplyUploadLimitAsync(string processName, double limitKbps, CancellationToken cancellationToken = default)
    {
        if (!IsWindowsSupported)
        {
            return LimitApplyResult.Fail("目前平台不支援實際限速。");
        }

        if (!IsElevated)
        {
            return LimitApplyResult.Fail("上傳限速失敗：需要系統管理員權限。");
        }

        if (limitKbps <= 0)
        {
            return await RemoveLimitAsync(processName, "UL", cancellationToken);
        }

        var appMatch = ToAppMatchName(processName);
        var policyName = BuildPolicyName(processName, "UL");
        // limitKbps is kilobytes/sec → bits/sec = KB/s * 1024 * 8
        var bitsPerSecond = (ulong)Math.Max(8, Math.Round(limitKbps * 1024d * 8d));

        await RemovePolicyInternalAsync(policyName, cancellationToken);

        // PolicyStore ActiveStore is session-scoped and applies immediately when elevated.
        var script =
            "$ErrorActionPreference = 'Stop'; " +
            $"New-NetQosPolicy -Name '{Escape(policyName)}' -AppPathNameMatchCondition '{Escape(appMatch)}' " +
            $"-ThrottleRateActionBitsPerSecond {bitsPerSecond} -NetworkProfile All -PolicyStore ActiveStore | Out-Null";

        var result = await RunPowerShellAsync(script, cancellationToken);
        if (result.Success)
        {
            lock (_sync)
            {
                _activePolicies.Add(policyName);
            }

            var mbps = limitKbps / 1024d;
            return LimitApplyResult.Ok(
                $"已套用上傳限速 {mbps:0.##} MB/s（{limitKbps:0.##} KB/s）→ {appMatch}");
        }

        var err = string.IsNullOrWhiteSpace(result.Error)
            ? "套用上傳限速失敗（可能需要系統管理員權限）。"
            : result.Error;

        if (err.Contains("拒絕", StringComparison.OrdinalIgnoreCase) ||
            err.Contains("Access", StringComparison.OrdinalIgnoreCase) ||
            err.Contains("denied", StringComparison.OrdinalIgnoreCase) ||
            err.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase))
        {
            return LimitApplyResult.Fail("上傳限速失敗：權限不足，請以系統管理員重新啟動。");
        }

        return LimitApplyResult.Fail(err);
    }

    public Task<LimitApplyResult> ApplyDownloadLimitAsync(string processName, double limitKbps, CancellationToken cancellationToken = default)
    {
        if (limitKbps <= 0)
        {
            return Task.FromResult(LimitApplyResult.Ok("已關閉下載限速設定。"));
        }

        if (!IsWindowsSupported)
        {
            return Task.FromResult(LimitApplyResult.Fail("目前平台不支援實際限速。"));
        }

        var mbps = limitKbps / 1024d;
        // Windows Policy-based QoS throttle only affects outbound traffic.
        return Task.FromResult(LimitApplyResult.Ok(
            $"下載 {mbps:0.##} MB/s 已記錄（Windows QoS 無法可靠限制下載/入站；請改限上傳或使用含驅動的工具）"));
    }

    public Task<LimitApplyResult> RemoveLimitAsync(string processName, string direction, CancellationToken cancellationToken = default)
    {
        var policyName = BuildPolicyName(processName, direction);
        return RemovePolicyInternalAsync(policyName, cancellationToken);
    }

    public async Task RemoveAllAsync(CancellationToken cancellationToken = default)
    {
        List<string> policies;
        List<string> firewallRules;
        lock (_sync)
        {
            policies = _activePolicies.ToList();
            firewallRules = _activeFirewallRules.ToList();
        }

        foreach (var policy in policies)
        {
            await RemovePolicyInternalAsync(policy, cancellationToken);
        }

        foreach (var rule in firewallRules)
        {
            await RemoveFirewallRuleAsync(rule, cancellationToken);
        }

        if (IsWindowsSupported)
        {
            var qosCleanup =
                "$ErrorActionPreference = 'SilentlyContinue'; " +
                $"Get-NetQosPolicy -PolicyStore ActiveStore | Where-Object {{ $_.Name -like '{PolicyPrefix}*' }} | " +
                "ForEach-Object { Remove-NetQosPolicy -Name $_.Name -PolicyStore ActiveStore -Confirm:$false }";
            await RunPowerShellAsync(qosCleanup, cancellationToken);

            var fwCleanup =
                "$ErrorActionPreference = 'SilentlyContinue'; " +
                $"Get-NetFirewallRule | Where-Object {{ $_.DisplayName -like '{FirewallPrefix}*' }} | Remove-NetFirewallRule";
            await RunPowerShellAsync(fwCleanup, cancellationToken);
        }
    }

    private async Task<LimitApplyResult> SetBlockAsync(
        string processName,
        string? executablePath,
        bool enable,
        CancellationToken cancellationToken)
    {
        if (!IsWindowsSupported)
        {
            return enable
                ? LimitApplyResult.Fail("目前平台不支援 Block。")
                : LimitApplyResult.Ok("已清除 Block 設定。");
        }

        if (!IsElevated && enable)
        {
            return LimitApplyResult.Fail("Block 失敗：需要系統管理員權限。");
        }

        var ruleBase = BuildFirewallRuleName(processName);
        var ruleOut = ruleBase + "_Out";
        var ruleIn = ruleBase + "_In";

        if (!enable)
        {
            await RemoveFirewallRuleAsync(ruleOut, cancellationToken);
            await RemoveFirewallRuleAsync(ruleIn, cancellationToken);
            return LimitApplyResult.Ok("已解除 Block");
        }

        var program = ResolveProgramPath(processName, executablePath);
        if (string.IsNullOrWhiteSpace(program))
        {
            return LimitApplyResult.Fail("Block 需要可執行檔路徑（無法從行程取得路徑）。");
        }

        await RemoveFirewallRuleAsync(ruleOut, cancellationToken);
        await RemoveFirewallRuleAsync(ruleIn, cancellationToken);

        var outScript =
            "$ErrorActionPreference = 'Stop'; " +
            $"New-NetFirewallRule -DisplayName '{Escape(ruleOut)}' -Direction Outbound -Action Block " +
            $"-Program '{Escape(program)}' -Enabled True | Out-Null";
        var inScript =
            "$ErrorActionPreference = 'Stop'; " +
            $"New-NetFirewallRule -DisplayName '{Escape(ruleIn)}' -Direction Inbound -Action Block " +
            $"-Program '{Escape(program)}' -Enabled True | Out-Null";

        var outResult = await RunPowerShellAsync(outScript, cancellationToken);
        var inResult = await RunPowerShellAsync(inScript, cancellationToken);

        if (outResult.Success)
        {
            lock (_sync)
            {
                _activeFirewallRules.Add(ruleOut);
            }
        }

        if (inResult.Success)
        {
            lock (_sync)
            {
                _activeFirewallRules.Add(ruleIn);
            }
        }

        if (outResult.Success || inResult.Success)
        {
            return LimitApplyResult.Ok($"已 Block 網路：{Path.GetFileName(program)}");
        }

        var err = string.Join(" ", new[] { outResult.Error, inResult.Error }.Where(s => !string.IsNullOrWhiteSpace(s)));
        return LimitApplyResult.Fail(string.IsNullOrWhiteSpace(err)
            ? "Block 失敗（需要系統管理員權限）。"
            : err);
    }

    private async Task RemoveFirewallRuleAsync(string ruleName, CancellationToken cancellationToken)
    {
        if (!IsWindowsSupported)
        {
            lock (_sync)
            {
                _activeFirewallRules.Remove(ruleName);
            }

            return;
        }

        var script =
            "$ErrorActionPreference = 'SilentlyContinue'; " +
            $"Get-NetFirewallRule -DisplayName '{Escape(ruleName)}' -ErrorAction SilentlyContinue | Remove-NetFirewallRule";
        await RunPowerShellAsync(script, cancellationToken);
        lock (_sync)
        {
            _activeFirewallRules.Remove(ruleName);
        }
    }

    private async Task<LimitApplyResult> RemovePolicyInternalAsync(string policyName, CancellationToken cancellationToken)
    {
        if (!IsWindowsSupported)
        {
            lock (_sync)
            {
                _activePolicies.Remove(policyName);
            }

            return LimitApplyResult.Ok("已清除限速設定。");
        }

        var escaped = Escape(policyName);
        var script =
            "$ErrorActionPreference = 'SilentlyContinue'; " +
            $"if (Get-NetQosPolicy -Name '{escaped}' -PolicyStore ActiveStore -ErrorAction SilentlyContinue) " +
            $"{{ Remove-NetQosPolicy -Name '{escaped}' -PolicyStore ActiveStore -Confirm:$false }}";

        var result = await RunPowerShellAsync(script, cancellationToken);
        lock (_sync)
        {
            _activePolicies.Remove(policyName);
        }

        return result.Success
            ? LimitApplyResult.Ok("已移除限速原則。")
            : LimitApplyResult.Fail(result.Error);
    }

    private static string ResolveProgramPath(string processName, string? executablePath)
    {
        if (!string.IsNullOrWhiteSpace(executablePath) &&
            executablePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
            File.Exists(executablePath))
        {
            return executablePath;
        }

        if (!string.IsNullOrWhiteSpace(executablePath) &&
            executablePath.Contains('\\') &&
            File.Exists(executablePath))
        {
            return executablePath;
        }

        try
        {
            var processes = Process.GetProcessesByName(
                processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                    ? Path.GetFileNameWithoutExtension(processName)
                    : processName);

            foreach (var process in processes)
            {
                try
                {
                    var path = process.MainModule?.FileName;
                    if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                    {
                        return path;
                    }
                }
                catch
                {
                    // Protected process.
                }
                finally
                {
                    process.Dispose();
                }
            }
        }
        catch
        {
            // Ignore enumeration failures.
        }

        return string.Empty;
    }

    private static string BuildFirewallRuleName(string processName)
    {
        var safe = new string(processName
            .Where(ch => char.IsLetterOrDigit(ch) || ch is '_' or '-' or '.')
            .ToArray());
        if (string.IsNullOrWhiteSpace(safe))
        {
            safe = "app";
        }

        if (safe.Length > 40)
        {
            safe = safe[..40];
        }

        return $"{FirewallPrefix}{safe}";
    }

    private static string BuildPolicyName(string processName, string direction)
    {
        var safe = new string(processName
            .Where(ch => char.IsLetterOrDigit(ch) || ch is '_' or '-' or '.')
            .ToArray());
        if (string.IsNullOrWhiteSpace(safe))
        {
            safe = "app";
        }

        if (safe.Length > 40)
        {
            safe = safe[..40];
        }

        return $"{PolicyPrefix}{direction}_{safe}";
    }

    private static string ToAppMatchName(string processName)
    {
        var name = processName.Trim();
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return name;
        }

        return $"{name}.exe";
    }

    private static string Escape(string value) => value.Replace("'", "''");

    private static async Task<(bool Success, string Error)> RunPowerShellAsync(string script, CancellationToken cancellationToken)
    {
        try
        {
            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -EncodedCommand {encoded}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process is null)
            {
                return (false, "無法啟動 PowerShell。");
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            if (process.ExitCode == 0)
            {
                return (true, string.Empty);
            }

            var error = string.Join(" ", new[] { stderr, stdout }.Where(s => !string.IsNullOrWhiteSpace(s))).Trim();
            return (false, string.IsNullOrWhiteSpace(error) ? $"PowerShell 結束代碼 {process.ExitCode}" : error);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Avoid blocking UI shutdown with PowerShell cleanup when not elevated
        // (common source of "hung close" / resource pressure).
        if (!IsWindowsSupported || !IsElevated)
        {
            lock (_sync)
            {
                _activePolicies.Clear();
                _activeFirewallRules.Clear();
            }

            return;
        }

        try
        {
            // Bound cleanup so exit cannot hang forever.
            var cleanup = RemoveAllAsync();
            if (!cleanup.Wait(TimeSpan.FromSeconds(3)))
            {
                // Let process exit; OS will drop ActiveStore session policies.
            }
        }
        catch
        {
            // Ignore cleanup failures on shutdown.
        }
    }
}

public sealed record LimitApplyResult(bool Success, string Message)
{
    public static LimitApplyResult Ok(string message) => new(true, message);

    public static LimitApplyResult Fail(string message) => new(false, message);
}
