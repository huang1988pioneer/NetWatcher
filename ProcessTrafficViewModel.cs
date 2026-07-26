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
    private bool _isDownloadLimitEnabled;
    private bool _isUploadLimitEnabled;
    private bool _isLimitControlEnabled = true;
    private string _limitStatusText = "Normal";
    private bool _isApplyingLimit;
    private bool _suppressLimitEvents;
    private string _statusText = "閒置";
    private bool _isRunning;
    private TrafficPriorityOption _selectedPriority = TrafficPriorityOption.All[1];
    private SpeedLimitOption _selectedDownloadLimit = SpeedLimitOption.Presets[0];
    private SpeedLimitOption _selectedUploadLimit = SpeedLimitOption.Presets[0];
    private IBrush _avatarBrush = new SolidColorBrush(Color.Parse("#3B82F6"));

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
                var kbps = value.IsUnlimited ? 0 : TrafficFormatter.MBpsToKbps(value.MegaBytesPerSecond);
                DownloadLimitKbpsText = kbps > 0 ? kbps.ToString("0.##") : string.Empty;
                IsDownloadLimitEnabled = !value.IsUnlimited;
                if (!value.IsUnlimited || !SelectedUploadLimit.IsUnlimited)
                {
                    SelectedPriority = TrafficPriorityOption.All.First(x => x.Priority == TrafficPriority.Limit);
                }
                else if (SelectedPriority.Priority == TrafficPriority.Limit)
                {
                    SelectedPriority = TrafficPriorityOption.All.First(x => x.Priority == TrafficPriority.Normal);
                }

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
                var kbps = value.IsUnlimited ? 0 : TrafficFormatter.MBpsToKbps(value.MegaBytesPerSecond);
                UploadLimitKbpsText = kbps > 0 ? kbps.ToString("0.##") : string.Empty;
                IsUploadLimitEnabled = !value.IsUnlimited;
                if (!value.IsUnlimited || !SelectedDownloadLimit.IsUnlimited)
                {
                    SelectedPriority = TrafficPriorityOption.All.First(x => x.Priority == TrafficPriority.Limit);
                }
                else if (SelectedPriority.Priority == TrafficPriority.Limit)
                {
                    SelectedPriority = TrafficPriorityOption.All.First(x => x.Priority == TrafficPriority.Normal);
                }

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
                    IsDownloadLimitEnabled = ParseKbps(DownloadLimitKbpsText) > 0;
                    IsUploadLimitEnabled = ParseKbps(UploadLimitKbpsText) > 0;
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

    public bool HasActiveLimit =>
        IsLimitControlEnabled &&
        (IsDownloadLimitEnabled ||
         IsUploadLimitEnabled ||
         SelectedPriority.Priority is not TrafficPriority.Normal);

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
        IsRunning = active || HasActiveLimit;
        StatusText = active
            ? "執行中"
            : HasActiveLimit
                ? "已控管"
                : "閒置";
    }

    public void LoadLimitSettings(ProcessLimitSettings settings)
    {
        _suppressLimitEvents = true;
        try
        {
            DownloadLimitKbpsText = settings.DownloadLimitKbps > 0 ? settings.DownloadLimitKbps.ToString("0.##") : string.Empty;
            UploadLimitKbpsText = settings.UploadLimitKbps > 0 ? settings.UploadLimitKbps.ToString("0.##") : string.Empty;
            IsDownloadLimitEnabled = settings.DownloadLimitEnabled;
            IsUploadLimitEnabled = settings.UploadLimitEnabled;
            IsLimitControlEnabled = settings.IsLimitControlEnabled;
            SelectedPriority = TrafficPriorityOption.FromName(settings.Priority);
            _selectedDownloadLimit = SpeedLimitOption.FromKbps(settings.DownloadLimitKbps);
            _selectedUploadLimit = SpeedLimitOption.FromKbps(settings.UploadLimitKbps);
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
        return new ProcessLimitSettings
        {
            DownloadLimitKbps = SelectedDownloadLimit.IsUnlimited
                ? ParseKbps(DownloadLimitKbpsText)
                : TrafficFormatter.MBpsToKbps(SelectedDownloadLimit.MegaBytesPerSecond),
            UploadLimitKbps = SelectedUploadLimit.IsUnlimited
                ? ParseKbps(UploadLimitKbpsText)
                : TrafficFormatter.MBpsToKbps(SelectedUploadLimit.MegaBytesPerSecond),
            DownloadLimitEnabled = IsLimitControlEnabled &&
                                   (IsDownloadLimitEnabled || !SelectedDownloadLimit.IsUnlimited),
            UploadLimitEnabled = IsLimitControlEnabled &&
                                 (IsUploadLimitEnabled || !SelectedUploadLimit.IsUnlimited),
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

        return double.TryParse(text.Trim(), out var value) && value > 0 ? value : 0;
    }

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
