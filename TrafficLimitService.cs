using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace NetWatcher.App;

/// <summary>
/// Traffic control:
/// 1) WinDivert packet shaping (download + upload) via ProcessThrottleEngine — needs admin.
/// 2) Windows Policy-based QoS for outbound (upload) when elevated (extra belt).
/// 3) Firewall block for Block priority.
/// </summary>
public sealed class TrafficLimitService : IDisposable
{
    private const string PolicyPrefix = "NetWatcher_";
    private const string FirewallPrefix = "NetWatcher_Block_";
    private const double LowPriorityUploadKbps = 128;

    private readonly object _sync = new();
    private readonly HashSet<string> _activePolicies = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _activeFirewallRules = new(StringComparer.OrdinalIgnoreCase);
    private readonly ProcessThrottleEngine _softThrottle = new();
    private bool _disposed;

    public bool IsWindowsSupported => OperatingSystem.IsWindows();

    public bool IsElevated => AdminElevation.IsElevated();

    public ProcessThrottleEngine SoftThrottle => _softThrottle;

    public string CapabilityText
    {
        get
        {
            if (!IsWindowsSupported)
            {
                return "限速僅 Windows 可實際套用。";
            }

            var admin = IsElevated ? "已提權" : "未提權（封包限速/QoS 需管理員）";
            return $"{admin} · 下載/上傳：WinDivert 封包限速（目標 MB/s，允許微小誤差）· 上傳 QoS：額外輔助 · 需系統管理員。";
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

        var messages = new List<string>();
        var anyFail = false;

        // Soft throttle always available (same user processes).
        ConfigureSoftThrottle(processName, priority, downloadLimitKbps, uploadLimitKbps);

        if (!IsElevated && priority is TrafficPriority.Block)
        {
            return LimitApplyResult.Fail("Block 需要系統管理員權限。");
        }

        // Clear previous block first unless re-applying Block.
        if (priority != TrafficPriority.Block)
        {
            var unblock = await SetBlockAsync(processName, executablePath, enable: false, cancellationToken);
            if (!unblock.Success && !string.IsNullOrWhiteSpace(unblock.Message))
            {
                // ignore non-fatal
            }
        }

        switch (priority)
        {
            case TrafficPriority.High:
            case TrafficPriority.Normal:
            {
                _softThrottle.Clear(processName);
                if (IsElevated)
                {
                    await RemoveLimitAsync(processName, "UL", cancellationToken);
                    await RemoveLimitAsync(processName, "UL2", cancellationToken);
                }

                messages.Add(priority == TrafficPriority.High ? "High：不限速" : "已解除限速");
                break;
            }

            case TrafficPriority.Low:
            {
                var lowRate = uploadLimitKbps > 0 ? uploadLimitKbps : LowPriorityUploadKbps;
                messages.Add($"封包限速：上傳≤{FormatLimitMBps(lowRate)}");
                if (downloadLimitKbps > 0)
                {
                    messages.Add($"下載≤{FormatLimitMBps(downloadLimitKbps)}");
                }

                if (IsElevated)
                {
                    var qos = await ApplyUploadLimitAsync(processName, lowRate, cancellationToken);
                    messages.Add(qos.Success ? "QoS上傳已套用" : "QoS失敗:" + Short(qos.Message));
                    anyFail |= !qos.Success;
                }
                else
                {
                    messages.Add("未提權：封包限速/QoS 無法生效，請以系統管理員執行");
                    anyFail = true;
                }

                break;
            }

            case TrafficPriority.Limit:
            {
                if (uploadLimitKbps <= 0 && downloadLimitKbps <= 0)
                {
                    _softThrottle.Clear(processName);
                    return LimitApplyResult.Fail("請選擇下載或上傳限制值");
                }

                if (downloadLimitKbps > 0)
                {
                    messages.Add($"下載限速 {FormatLimitMBps(downloadLimitKbps)}");
                }

                if (uploadLimitKbps > 0)
                {
                    messages.Add($"上傳限速 {FormatLimitMBps(uploadLimitKbps)}");
                    if (IsElevated)
                    {
                        var qos = await ApplyUploadLimitAsync(processName, uploadLimitKbps, cancellationToken);
                        messages.Add(qos.Success
                            ? $"QoS上傳已驗證 {FormatLimitMBps(uploadLimitKbps)}"
                            : "QoS失敗:" + Short(qos.Message));
                        anyFail |= !qos.Success;
                    }
                }
                else if (IsElevated)
                {
                    await RemoveLimitAsync(processName, "UL", cancellationToken);
                    await RemoveLimitAsync(processName, "UL2", cancellationToken);
                }

                if (!IsElevated)
                {
                    messages.Add("未提權：WinDivert 封包限速無法啟動，請以系統管理員執行");
                    anyFail = true;
                }

                break;
            }

            case TrafficPriority.Block:
            {
                _softThrottle.Clear(processName);
                if (IsElevated)
                {
                    await RemoveLimitAsync(processName, "UL", cancellationToken);
                    await RemoveLimitAsync(processName, "UL2", cancellationToken);
                }

                var block = await SetBlockAsync(processName, executablePath, enable: true, cancellationToken);
                messages.Add(block.Message);
                anyFail |= !block.Success;
                break;
            }
        }

        if (priority is TrafficPriority.Limit or TrafficPriority.Low)
        {
            messages.Add(_softThrottle.LastActionText);
        }

        var text = string.Join(" · ", messages.Where(m => !string.IsNullOrWhiteSpace(m)).Take(4));
        return anyFail ? LimitApplyResult.Fail(text) : LimitApplyResult.Ok(text);
    }

    private void ConfigureSoftThrottle(
        string processName,
        TrafficPriority priority,
        double downloadLimitKbps,
        double uploadLimitKbps)
    {
        if (priority is TrafficPriority.Normal or TrafficPriority.High or TrafficPriority.Block)
        {
            _softThrottle.Clear(processName);
            return;
        }

        // Stored unit is KiB/s (1024-based): UI "1 MB/s" → 1024 KiB/s → 1,048,576 B/s.
        var dlBps = downloadLimitKbps > 0 ? downloadLimitKbps * 1024d : 0;
        var ulBps = uploadLimitKbps > 0 ? uploadLimitKbps * 1024d : 0;

        // Low priority always has an upload soft cap.
        if (priority == TrafficPriority.Low && ulBps <= 0)
        {
            ulBps = LowPriorityUploadKbps * 1024d;
        }

        _softThrottle.SetLimit(processName, dlBps, ulBps, enabled: dlBps > 0 || ulBps > 0);
    }

    /// <summary>Format stored KiB/s limit as binary MB/s for status text (never show 0 when limit &gt; 0).</summary>
    private static string FormatLimitMBps(double limitKibPerSecond)
    {
        if (limitKibPerSecond <= 0)
        {
            return "不限";
        }

        var mbps = limitKibPerSecond / 1024d;
        return mbps >= 1 ? $"{mbps:0.##} MB/s" : $"{mbps:0.###} MB/s";
    }

    public async Task<LimitApplyResult> ApplyUploadLimitAsync(
        string processName,
        double limitKbps,
        CancellationToken cancellationToken = default)
    {
        if (!IsWindowsSupported)
        {
            return LimitApplyResult.Fail("目前平台不支援實際限速。");
        }

        if (!IsElevated)
        {
            return LimitApplyResult.Fail("上傳 QoS 需要系統管理員權限。");
        }

        if (limitKbps <= 0)
        {
            await RemoveLimitAsync(processName, "UL", cancellationToken);
            await RemoveLimitAsync(processName, "UL2", cancellationToken);
            return LimitApplyResult.Ok("已移除上傳 QoS。");
        }

        var appMatch = ToAppMatchName(processName);
        var policyName = BuildPolicyName(processName, "UL");
        // KB/s → bits/s
        var bitsPerSecond = (ulong)Math.Max(8000, Math.Round(limitKbps * 1024d * 8d));

        await RemoveLimitAsync(processName, "UL", cancellationToken);
        await RemoveLimitAsync(processName, "UL2", cancellationToken);

        // ActiveStore (immediate) + PersistentStore (survives until removed).
        // IPProtocol Both + all network profiles.
        var script =
            "$ErrorActionPreference = 'Stop'; " +
            $"$name = '{Escape(policyName)}'; " +
            $"$app = '{Escape(appMatch)}'; " +
            $"$bps = [uint64]{bitsPerSecond}; " +
            "foreach ($store in @('ActiveStore','PersistentStore')) { " +
            "  try { Remove-NetQosPolicy -Name $name -PolicyStore $store -Confirm:$false -ErrorAction SilentlyContinue } catch {} " +
            "  New-NetQosPolicy -Name $name -AppPathNameMatchCondition $app " +
            "    -IPProtocolMatchCondition Both -NetworkProfile All " +
            "    -ThrottleRateActionBitsPerSecond $bps -PolicyStore $store | Out-Null " +
            "} " +
            // Verify ActiveStore policy exists and report throttle.
            "$p = Get-NetQosPolicy -Name $name -PolicyStore ActiveStore -ErrorAction Stop; " +
            "if (-not $p) { throw 'policy missing after create' }; " +
            "'OK:' + $p.Name + ' app=' + $p.AppPathNameMatchCondition + ' bps=' + $bps";

        var result = await RunPowerShellAsync(script, cancellationToken);
        if (result.Success)
        {
            lock (_sync)
            {
                _activePolicies.Add(policyName);
            }

            var mbps = limitKbps / 1024d;
            return LimitApplyResult.Ok($"QoS上傳 {mbps:0.##} MB/s → {appMatch} 已建立並驗證");
        }

        // Fallback: ActiveStore only (some SKUs restrict PersistentStore).
        var fallback =
            "$ErrorActionPreference = 'Stop'; " +
            $"$name = '{Escape(policyName)}'; " +
            $"Remove-NetQosPolicy -Name $name -PolicyStore ActiveStore -Confirm:$false -ErrorAction SilentlyContinue; " +
            $"New-NetQosPolicy -Name $name -AppPathNameMatchCondition '{Escape(appMatch)}' " +
            $"-NetworkProfile All -ThrottleRateActionBitsPerSecond {bitsPerSecond} -PolicyStore ActiveStore | Out-Null; " +
            "Get-NetQosPolicy -Name $name -PolicyStore ActiveStore | Out-Null; 'OK-fallback'";

        var fb = await RunPowerShellAsync(fallback, cancellationToken);
        if (fb.Success)
        {
            lock (_sync)
            {
                _activePolicies.Add(policyName);
            }

            return LimitApplyResult.Ok($"QoS上傳已套用(ActiveStore) → {appMatch}");
        }

        var err = string.IsNullOrWhiteSpace(result.Error) ? fb.Error : result.Error;
        if (ContainsAccessDenied(err))
        {
            return LimitApplyResult.Fail("上傳 QoS 權限不足，請以系統管理員重新啟動。");
        }

        return LimitApplyResult.Fail(string.IsNullOrWhiteSpace(err) ? "上傳 QoS 套用失敗" : Short(err));
    }

    public Task<LimitApplyResult> ApplyDownloadLimitAsync(
        string processName,
        double limitKbps,
        CancellationToken cancellationToken = default)
    {
        // Download hard-limit is not available via Windows QoS; soft throttle handles it.
        if (limitKbps <= 0)
        {
            return Task.FromResult(LimitApplyResult.Ok("已關閉下載限速設定。"));
        }

        return Task.FromResult(LimitApplyResult.Ok(
            $"下載 {FormatLimitMBps(limitKbps)} 由 WinDivert 封包限速執行（需管理員）"));
    }

    public Task<LimitApplyResult> RemoveLimitAsync(
        string processName,
        string direction,
        CancellationToken cancellationToken = default)
    {
        var policyName = BuildPolicyName(processName, direction);
        return RemovePolicyInternalAsync(policyName, cancellationToken);
    }

    public async Task RemoveAllAsync(CancellationToken cancellationToken = default)
    {
        _softThrottle.ClearAll();

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

        if (IsWindowsSupported && IsElevated)
        {
            var qosCleanup =
                "$ErrorActionPreference = 'SilentlyContinue'; " +
                $"Get-NetQosPolicy -PolicyStore ActiveStore | Where-Object {{ $_.Name -like '{PolicyPrefix}*' }} | " +
                "ForEach-Object { Remove-NetQosPolicy -Name $_.Name -PolicyStore ActiveStore -Confirm:$false }; " +
                $"Get-NetQosPolicy -PolicyStore PersistentStore -ErrorAction SilentlyContinue | Where-Object {{ $_.Name -like '{PolicyPrefix}*' }} | " +
                "ForEach-Object { Remove-NetQosPolicy -Name $_.Name -PolicyStore PersistentStore -Confirm:$false }";
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
            : Short(err));
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

    private async Task<LimitApplyResult> RemovePolicyInternalAsync(
        string policyName,
        CancellationToken cancellationToken)
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
            $"foreach ($store in @('ActiveStore','PersistentStore')) {{ " +
            $"  if (Get-NetQosPolicy -Name '{escaped}' -PolicyStore $store -ErrorAction SilentlyContinue) {{ " +
            $"    Remove-NetQosPolicy -Name '{escaped}' -PolicyStore $store -Confirm:$false " +
            "  } " +
            "}";

        var result = await RunPowerShellAsync(script, cancellationToken);
        lock (_sync)
        {
            _activePolicies.Remove(policyName);
        }

        return result.Success
            ? LimitApplyResult.Ok("已移除限速原則。")
            : LimitApplyResult.Fail(Short(result.Error));
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
        var safe = Sanitize(processName, 40);
        return $"{FirewallPrefix}{safe}";
    }

    private static string BuildPolicyName(string processName, string direction)
    {
        var safe = Sanitize(processName, 40);
        return $"{PolicyPrefix}{direction}_{safe}";
    }

    private static string Sanitize(string processName, int maxLen)
    {
        var safe = new string(processName
            .Where(ch => char.IsLetterOrDigit(ch) || ch is '_' or '-' or '.')
            .ToArray());
        if (string.IsNullOrWhiteSpace(safe))
        {
            safe = "app";
        }

        if (safe.Length > maxLen)
        {
            safe = safe[..maxLen];
        }

        return safe;
    }

    private static string ToAppMatchName(string processName)
    {
        var name = processName.Trim();
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return name;
        }

        // Description may be a full path — reduce to file name for QoS match.
        if (name.Contains('\\') || name.Contains('/'))
        {
            name = Path.GetFileName(name);
        }

        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name : $"{name}.exe";
    }

    private static string Escape(string value) => value.Replace("'", "''");

    private static bool ContainsAccessDenied(string? err) =>
        !string.IsNullOrWhiteSpace(err) &&
        (err.Contains("拒絕", StringComparison.OrdinalIgnoreCase) ||
         err.Contains("Access", StringComparison.OrdinalIgnoreCase) ||
         err.Contains("denied", StringComparison.OrdinalIgnoreCase) ||
         err.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase));

    private static string Short(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var oneLine = Regex.Replace(text, @"\s+", " ").Trim();
        return oneLine.Length <= 140 ? oneLine : oneLine[..140] + "…";
    }

    private static async Task<(bool Success, string Error)> RunPowerShellAsync(
        string script,
        CancellationToken cancellationToken)
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
                return (true, stdout?.Trim() ?? string.Empty);
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
        try
        {
            _softThrottle.Dispose();
        }
        catch
        {
            // ignore
        }

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
            var cleanup = RemoveAllAsync();
            _ = cleanup.Wait(TimeSpan.FromSeconds(3));
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
