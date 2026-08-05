using System.Globalization;
using Avalonia.Media;

namespace NetWatcher.App;

public sealed class ProcessTrafficViewModel : ObservableObject
{
    private static readonly string[] AvatarPalette =
    [
        "#3B82F6", "#22C55E", "#F59E0B", "#EF4444", "#A855F7",
        "#06B6D4", "#EC4899", "#84CC16", "#F97316", "#6366F1"
    ];

    private string _processName = string.Empty;
    private string _description = string.Empty;
    private string _downloadSpeedText = "0 B/s";
    private string _uploadSpeedText = "0 B/s";
    private int _processId;
    private double _downloadBytesPerSecond;
    private double _uploadBytesPerSecond;
    private string _downloadLimitKbpsText = "";
    private string _uploadLimitKbpsText = "";
    private string _downloadLimitMbpsText = "";
    private string _uploadLimitMbpsText = "";
    private bool _isDownloadLimitEnabled;
    private bool _isUploadLimitEnabled;
    private bool _isLimitControlEnabled = true;
    private string _limitStatusText = "Normal";
    private bool _isApplyingLimit;
    private bool _suppressLimitEvents;
    private string _statusText = "閒置";
    private bool _isRunning;
    private DateTimeOffset _lastActivityUtc = DateTimeOffset.UtcNow;
    private TrafficPriorityOption _selectedPriority = TrafficPriorityOption.All[1];
    private SpeedLimitOption _selectedDownloadLimit = SpeedLimitOption.Presets[0];
    private SpeedLimitOption _selectedUploadLimit = SpeedLimitOption.Presets[0];
    private IBrush _avatarBrush = new SolidColorBrush(Color.Parse("#3B82F6"));

    /// <summary>How long a process row stays after traffic drops to zero.</summary>
    public static readonly TimeSpan IdleRetention = TimeSpan.FromSeconds(30);

    public event Func<ProcessTrafficViewModel, Task>? LimitSettingsChanged;

    public IReadOnlyList<TrafficPriorityOption> PriorityOptions => TrafficPriorityOption.All;

    public IReadOnlyList<SpeedLimitOption> LimitOptions => SpeedLimitOption.Presets;

    public string ProcessName
    {
        get => _processName;
        set
        {
            if (SetProperty(ref _processName, value))
            {
                RaisePropertyChanged(nameof(Initial));
                AvatarBrush = BuildAvatarBrush(value);
            }
        }
    }

    public string Description
    {
        get => _description;
        set => SetProperty(ref _description, value);
    }

    public string DownloadSpeedText
    {
        get => _downloadSpeedText;
        set => SetProperty(ref _downloadSpeedText, value);
    }

    public string UploadSpeedText
    {
        get => _uploadSpeedText;
        set => SetProperty(ref _uploadSpeedText, value);
    }

    public int ProcessId
    {
        get => _processId;
        set => SetProperty(ref _processId, value);
    }

    public double DownloadBytesPerSecond
    {
        get => _downloadBytesPerSecond;
        set => SetProperty(ref _downloadBytesPerSecond, value);
    }

    public double UploadBytesPerSecond
    {
        get => _uploadBytesPerSecond;
        set => SetProperty(ref _uploadBytesPerSecond, value);
    }

    public string DownloadLimitKbpsText
    {
        get => _downloadLimitKbpsText;
        set
        {
            if (SetProperty(ref _downloadLimitKbpsText, value) && !_suppressLimitEvents)
            {
                _ = RaiseLimitSettingsChangedAsync();
            }
        }
    }

    public string UploadLimitKbpsText
    {
        get => _uploadLimitKbpsText;
        set
        {
            if (SetProperty(ref _uploadLimitKbpsText, value) && !_suppressLimitEvents)
            {
                _ = RaiseLimitSettingsChangedAsync();
            }
        }
    }

    /// <summary>Free-form download limit in MB/s (shown under the preset ComboBox).</summary>
    public string DownloadLimitMbpsText
    {
        get => _downloadLimitMbpsText;
        set
        {
            if (!SetProperty(ref _downloadLimitMbpsText, value) || _suppressLimitEvents)
            {
                return;
            }

            ApplyCustomMbpsInput(isDownload: true, value);
        }
    }

    /// <summary>Free-form upload limit in MB/s (shown under the preset ComboBox).</summary>
    public string UploadLimitMbpsText
    {
        get => _uploadLimitMbpsText;
        set
        {
            if (!SetProperty(ref _uploadLimitMbpsText, value) || _suppressLimitEvents)
            {
                return;
            }

            ApplyCustomMbpsInput(isDownload: false, value);
        }
    }

    public bool IsDownloadLimitEnabled
    {
        get => _isDownloadLimitEnabled;
        set
        {
            if (SetProperty(ref _isDownloadLimitEnabled, value) && !_suppressLimitEvents)
            {
                _ = RaiseLimitSettingsChangedAsync();
            }
        }
    }

    public bool IsUploadLimitEnabled
    {
        get => _isUploadLimitEnabled;
        set
        {
            if (SetProperty(ref _isUploadLimitEnabled, value) && !_suppressLimitEvents)
            {
                _ = RaiseLimitSettingsChangedAsync();
            }
        }
    }

    /// <summary>Master toggle for this process speed control (dashboard 操作 switch).</summary>
    public bool IsLimitControlEnabled
    {
        get => _isLimitControlEnabled;
        set
        {
            if (SetProperty(ref _isLimitControlEnabled, value) && !_suppressLimitEvents)
            {
                _ = RaiseLimitSettingsChangedAsync();
            }
        }
    }

    public SpeedLimitOption SelectedDownloadLimit
    {
        get => _selectedDownloadLimit;
        set
        {
            if (value is null)
            {
                return;
            }

            if (SetProperty(ref _selectedDownloadLimit, value) && !_suppressLimitEvents)
            {
                _suppressLimitEvents = true;
                SyncFromSelectedOption(
                    value,
                    setMbps: v => _downloadLimitMbpsText = v,
                    setKbps: v => _downloadLimitKbpsText = v,
                    setEnabled: v => _isDownloadLimitEnabled = v,
                    nameof(DownloadLimitMbpsText),
                    nameof(DownloadLimitKbpsText),
                    nameof(IsDownloadLimitEnabled),
                    _downloadLimitMbpsText);
                SyncPriorityFromLimits();
                _suppressLimitEvents = false;
                _ = RaiseLimitSettingsChangedAsync();
            }
        }
    }

    public SpeedLimitOption SelectedUploadLimit
    {
        get => _selectedUploadLimit;
        set
        {
            if (value is null)
            {
                return;
            }

            if (SetProperty(ref _selectedUploadLimit, value) && !_suppressLimitEvents)
            {
                _suppressLimitEvents = true;
                SyncFromSelectedOption(
                    value,
                    setMbps: v => _uploadLimitMbpsText = v,
                    setKbps: v => _uploadLimitKbpsText = v,
                    setEnabled: v => _isUploadLimitEnabled = v,
                    nameof(UploadLimitMbpsText),
                    nameof(UploadLimitKbpsText),
                    nameof(IsUploadLimitEnabled),
                    _uploadLimitMbpsText);
                SyncPriorityFromLimits();
                _suppressLimitEvents = false;
                _ = RaiseLimitSettingsChangedAsync();
            }
        }
    }

    public TrafficPriorityOption SelectedPriority
    {
        get => _selectedPriority;
        set
        {
            if (value is null)
            {
                return;
            }

            if (SetProperty(ref _selectedPriority, value) && !_suppressLimitEvents)
            {
                RaisePropertyChanged(nameof(PriorityBrush));
                RaisePropertyChanged(nameof(PriorityBadgeText));
                if (value.Priority == TrafficPriority.Limit)
                {
                    _suppressLimitEvents = true;
                    IsDownloadLimitEnabled = ResolveLimitKbps(SelectedDownloadLimit, DownloadLimitMbpsText) > 0;
                    IsUploadLimitEnabled = ResolveLimitKbps(SelectedUploadLimit, UploadLimitMbpsText) > 0;
                    _suppressLimitEvents = false;
                }

                _ = RaiseLimitSettingsChangedAsync();
            }
        }
    }

    public string LimitStatusText
    {
        get => _limitStatusText;
        set => SetProperty(ref _limitStatusText, value);
    }

    public bool IsApplyingLimit
    {
        get => _isApplyingLimit;
        set => SetProperty(ref _isApplyingLimit, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public bool IsRunning
    {
        get => _isRunning;
        private set => SetProperty(ref _isRunning, value);
    }

    public IBrush AvatarBrush
    {
        get => _avatarBrush;
        private set => SetProperty(ref _avatarBrush, value);
    }

    public double TotalBytesPerSecond => DownloadBytesPerSecond + UploadBytesPerSecond;

    /// <summary>UTC timestamp of the last non-zero traffic sample (or first sighting).</summary>
    public DateTimeOffset LastActivityUtc
    {
        get => _lastActivityUtc;
        private set => SetProperty(ref _lastActivityUtc, value);
    }

    public bool HasActiveLimit =>
        IsLimitControlEnabled &&
        (IsDownloadLimitEnabled ||
         IsUploadLimitEnabled ||
         SelectedPriority.Priority is not TrafficPriority.Normal);

    /// <summary>True while traffic is flowing, limits are on, or still within the idle grace window.</summary>
    public bool ShouldRemainVisible =>
        HasActiveLimit ||
        DownloadBytesPerSecond > 0 ||
        UploadBytesPerSecond > 0 ||
        DateTimeOffset.UtcNow - LastActivityUtc < IdleRetention;

    public string PriorityBadgeText => SelectedPriority.Label;

    public IBrush PriorityBrush => SelectedPriority.Priority switch
    {
        TrafficPriority.High => new SolidColorBrush(Color.Parse("#22C55E")),
        TrafficPriority.Low => new SolidColorBrush(Color.Parse("#F59E0B")),
        TrafficPriority.Limit => new SolidColorBrush(Color.Parse("#3B82F6")),
        TrafficPriority.Block => new SolidColorBrush(Color.Parse("#EF4444")),
        _ => new SolidColorBrush(Color.Parse("#8A96A8"))
    };

    public string Initial =>
        string.IsNullOrWhiteSpace(ProcessName)
            ? "?"
            : char.ToUpperInvariant(ProcessName.Trim()[0]).ToString();

    public void UpdateTraffic(int processId, string description, double downloadBps, double uploadBps)
    {
        ProcessId = processId;
        Description = description;
        DownloadBytesPerSecond = downloadBps;
        UploadBytesPerSecond = uploadBps;
        DownloadSpeedText = TrafficFormatter.FormatSpeed(downloadBps);
        UploadSpeedText = TrafficFormatter.FormatSpeed(uploadBps);

        var active = downloadBps > 0 || uploadBps > 0;
        if (active)
        {
            LastActivityUtc = DateTimeOffset.UtcNow;
        }

        var withinGrace = DateTimeOffset.UtcNow - LastActivityUtc < IdleRetention;
        IsRunning = active || HasActiveLimit || withinGrace;
        StatusText = active
            ? "執行中"
            : HasActiveLimit
                ? "已控管"
                : withinGrace
                    ? "稍候"
                    : "閒置";
    }

    /// <summary>Mark first appearance so the 30s retention window starts now.</summary>
    public void TouchActivity() => LastActivityUtc = DateTimeOffset.UtcNow;

    public void LoadLimitSettings(ProcessLimitSettings settings)
    {
        _suppressLimitEvents = true;
        try
        {
            _downloadLimitKbpsText = settings.DownloadLimitKbps > 0
                ? settings.DownloadLimitKbps.ToString("0.##", CultureInfo.InvariantCulture)
                : string.Empty;
            _uploadLimitKbpsText = settings.UploadLimitKbps > 0
                ? settings.UploadLimitKbps.ToString("0.##", CultureInfo.InvariantCulture)
                : string.Empty;
            _downloadLimitMbpsText = FormatMbpsField(TrafficFormatter.KbpsToMBps(settings.DownloadLimitKbps));
            _uploadLimitMbpsText = FormatMbpsField(TrafficFormatter.KbpsToMBps(settings.UploadLimitKbps));
            _isDownloadLimitEnabled = settings.DownloadLimitEnabled;
            _isUploadLimitEnabled = settings.UploadLimitEnabled;
            _isLimitControlEnabled = settings.IsLimitControlEnabled;
            _selectedPriority = TrafficPriorityOption.FromName(settings.Priority);
            _selectedDownloadLimit = SpeedLimitOption.FromKbps(settings.DownloadLimitKbps);
            _selectedUploadLimit = SpeedLimitOption.FromKbps(settings.UploadLimitKbps);

            RaisePropertyChanged(nameof(DownloadLimitKbpsText));
            RaisePropertyChanged(nameof(UploadLimitKbpsText));
            RaisePropertyChanged(nameof(DownloadLimitMbpsText));
            RaisePropertyChanged(nameof(UploadLimitMbpsText));
            RaisePropertyChanged(nameof(IsDownloadLimitEnabled));
            RaisePropertyChanged(nameof(IsUploadLimitEnabled));
            RaisePropertyChanged(nameof(IsLimitControlEnabled));
            RaisePropertyChanged(nameof(SelectedPriority));
            RaisePropertyChanged(nameof(SelectedDownloadLimit));
            RaisePropertyChanged(nameof(SelectedUploadLimit));
            LimitStatusText = SelectedPriority.Priority == TrafficPriority.Normal && !HasActiveLimit
                ? "Normal"
                : $"已載入 {SelectedPriority.Label}";
            RaisePropertyChanged(nameof(PriorityBrush));
            RaisePropertyChanged(nameof(PriorityBadgeText));
            RaisePropertyChanged(nameof(HasActiveLimit));
        }
        finally
        {
            _suppressLimitEvents = false;
        }
    }

    public ProcessLimitSettings ToLimitSettings()
    {
        var downloadKbps = ResolveLimitKbps(SelectedDownloadLimit, DownloadLimitMbpsText);
        var uploadKbps = ResolveLimitKbps(SelectedUploadLimit, UploadLimitMbpsText);

        return new ProcessLimitSettings
        {
            DownloadLimitKbps = downloadKbps,
            UploadLimitKbps = uploadKbps,
            DownloadLimitEnabled = IsLimitControlEnabled &&
                                   (downloadKbps > 0 || IsDownloadLimitEnabled ||
                                    SelectedDownloadLimit.IsCustom || !SelectedDownloadLimit.IsUnlimited),
            UploadLimitEnabled = IsLimitControlEnabled &&
                                 (uploadKbps > 0 || IsUploadLimitEnabled ||
                                  SelectedUploadLimit.IsCustom || !SelectedUploadLimit.IsUnlimited),
            Priority = SelectedPriority.Priority.ToString(),
            IsLimitControlEnabled = IsLimitControlEnabled
        };
    }

    public static double ParseKbps(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        return double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
               || double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out value)
            ? value > 0 ? value : 0
            : 0;
    }

    public static double ParseMbps(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        var trimmed = text.Trim();
        // Allow "3", "3.5", "3,5", or "3 MB/s".
        trimmed = trimmed.Replace("MB/s", "", StringComparison.OrdinalIgnoreCase)
            .Replace("MBps", "", StringComparison.OrdinalIgnoreCase)
            .Replace("mb", "", StringComparison.OrdinalIgnoreCase)
            .Trim();

        if (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ||
            double.TryParse(trimmed, NumberStyles.Float, CultureInfo.CurrentCulture, out value))
        {
            return value > 0 ? value : 0;
        }

        return 0;
    }

    public static double ResolveLimitKbps(SpeedLimitOption selected, string? mbpsText)
    {
        if (selected.IsCustom)
        {
            var mbps = ParseMbps(mbpsText);
            return mbps > 0 ? TrafficFormatter.MBpsToKbps(mbps) : 0;
        }

        if (selected.IsUnlimited)
        {
            return 0;
        }

        return TrafficFormatter.MBpsToKbps(selected.MegaBytesPerSecond);
    }

    private void ApplyCustomMbpsInput(bool isDownload, string text)
    {
        var mbps = ParseMbps(text);
        var kbps = mbps > 0 ? TrafficFormatter.MBpsToKbps(mbps) : 0;
        var kbpsText = kbps > 0 ? kbps.ToString("0.##", CultureInfo.InvariantCulture) : string.Empty;

        _suppressLimitEvents = true;
        if (isDownload)
        {
            _downloadLimitKbpsText = kbpsText;
            RaisePropertyChanged(nameof(DownloadLimitKbpsText));

            if (string.IsNullOrWhiteSpace(text))
            {
                // Empty custom field while "自訂" stays selected does not force unlimited until apply.
            }
            else if (!_selectedDownloadLimit.IsCustom)
            {
                _selectedDownloadLimit = SpeedLimitOption.Custom;
                RaisePropertyChanged(nameof(SelectedDownloadLimit));
            }

            _isDownloadLimitEnabled = kbps > 0 || _selectedDownloadLimit.IsCustom;
            RaisePropertyChanged(nameof(IsDownloadLimitEnabled));
        }
        else
        {
            _uploadLimitKbpsText = kbpsText;
            RaisePropertyChanged(nameof(UploadLimitKbpsText));

            if (!string.IsNullOrWhiteSpace(text) && !_selectedUploadLimit.IsCustom)
            {
                _selectedUploadLimit = SpeedLimitOption.Custom;
                RaisePropertyChanged(nameof(SelectedUploadLimit));
            }

            _isUploadLimitEnabled = kbps > 0 || _selectedUploadLimit.IsCustom;
            RaisePropertyChanged(nameof(IsUploadLimitEnabled));
        }

        SyncPriorityFromLimits();
        _suppressLimitEvents = false;
        _ = RaiseLimitSettingsChangedAsync();
    }

    private void SyncFromSelectedOption(
        SpeedLimitOption value,
        Action<string> setMbps,
        Action<string> setKbps,
        Action<bool> setEnabled,
        string mbpsProp,
        string kbpsProp,
        string enabledProp,
        string currentMbpsText)
    {
        if (value.IsCustom)
        {
            // Keep free-form text; enable when a positive MB/s is present.
            setEnabled(ParseMbps(currentMbpsText) > 0);
            RaisePropertyChanged(enabledProp);
            return;
        }

        if (value.IsUnlimited)
        {
            setMbps(string.Empty);
            setKbps(string.Empty);
            setEnabled(false);
        }
        else
        {
            var mbps = value.MegaBytesPerSecond;
            var kbps = TrafficFormatter.MBpsToKbps(mbps);
            setMbps(FormatMbpsField(mbps));
            setKbps(kbps.ToString("0.##", CultureInfo.InvariantCulture));
            setEnabled(true);
        }

        RaisePropertyChanged(mbpsProp);
        RaisePropertyChanged(kbpsProp);
        RaisePropertyChanged(enabledProp);
    }

    private void SyncPriorityFromLimits()
    {
        var hasLimit =
            ResolveLimitKbps(SelectedDownloadLimit, DownloadLimitMbpsText) > 0 ||
            ResolveLimitKbps(SelectedUploadLimit, UploadLimitMbpsText) > 0 ||
            SelectedDownloadLimit.IsCustom ||
            SelectedUploadLimit.IsCustom ||
            !SelectedDownloadLimit.IsUnlimited ||
            !SelectedUploadLimit.IsUnlimited;

        if (hasLimit)
        {
            if (SelectedPriority.Priority is TrafficPriority.Normal or TrafficPriority.High)
            {
                _selectedPriority = TrafficPriorityOption.All.First(x => x.Priority == TrafficPriority.Limit);
                RaisePropertyChanged(nameof(SelectedPriority));
                RaisePropertyChanged(nameof(PriorityBrush));
                RaisePropertyChanged(nameof(PriorityBadgeText));
            }
        }
        else if (SelectedPriority.Priority == TrafficPriority.Limit)
        {
            _selectedPriority = TrafficPriorityOption.All.First(x => x.Priority == TrafficPriority.Normal);
            RaisePropertyChanged(nameof(SelectedPriority));
            RaisePropertyChanged(nameof(PriorityBrush));
            RaisePropertyChanged(nameof(PriorityBadgeText));
        }
    }

    private static string FormatMbpsField(double mbps) =>
        mbps > 0 ? mbps.ToString("0.###", CultureInfo.InvariantCulture) : string.Empty;

    private static IBrush BuildAvatarBrush(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return new SolidColorBrush(Color.Parse(AvatarPalette[0]));
        }

        var hash = 0;
        foreach (var ch in name)
        {
            hash = (hash * 31) + ch;
        }

        var color = AvatarPalette[Math.Abs(hash) % AvatarPalette.Length];
        return new SolidColorBrush(Color.Parse(color));
    }

    private async Task RaiseLimitSettingsChangedAsync()
    {
        RaisePropertyChanged(nameof(HasActiveLimit));
        if (LimitSettingsChanged is null)
        {
            return;
        }

        await LimitSettingsChanged.Invoke(this);
    }
}
