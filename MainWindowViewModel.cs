using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Media;
using Avalonia.Threading;

namespace NetWatcher.App;

public sealed class MainWindowViewModel : ObservableObject, IDisposable
{
    private const int HistoryCapacity = 60;
    private const int LogCapacity = 3600;
    private const double SparkWidth = 320;
    private const double SparkHeight = 100;

    private readonly BirthdayEasterEgg? _birthdayEasterEgg;
    private readonly CsvExportService _csvExportService;
    private readonly NetworkMonitorService _networkMonitorService;
    private readonly TrafficLimitService _trafficLimitService;
    private readonly LimitSettingsStore _limitSettingsStore;
    private readonly TrafficStatsStore _trafficStatsStore;
    private readonly DispatcherTimer _timer;
    private readonly Queue<double> _downloadHistory = new();
    private readonly Queue<double> _uploadHistory = new();
    private readonly List<TrafficLogEntry> _trafficLog = [];
    private readonly Dictionary<string, ProcessTrafficViewModel> _processMap = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CancellationTokenSource> _limitDebounce = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _limitSync = new();

    private string _totalDownloadSpeedText = "0 B/s";
    private string _totalUploadSpeedText = "0 B/s";
    private string _lastUpdatedText = "尚未更新";
    private string _searchText = string.Empty;
    private bool _showOnlyActive = true;
    private SortMode _selectedSortMode = SortMode.Total;
    private readonly IReadOnlyList<SortModeOption> _sortModes =
    [
        new(SortMode.Total, "總流量"),
        new(SortMode.Download, "下載優先"),
        new(SortMode.Upload, "上傳優先")
    ];
    private readonly IReadOnlyList<UiThemeOption> _uiThemes =
    [
        new(UiTheme.Integrated, "整合 · 參考儀表板"),
        new(UiTheme.NetBalancer, "NetBalancer 配色"),
        new(UiTheme.BWMeter, "BWMeter 配色"),
        new(UiTheme.Eltrafico, "Eltrafico 配色"),
        new(UiTheme.GlassWire, "GlassWire 配色"),
        new(UiTheme.NetLimiter, "NetLimiter 配色")
    ];
    private readonly IReadOnlyList<AppNavItem> _navItems =
    [
        new(AppNavPage.Overview, "總覽", "⏱"),
        new(AppNavPage.Processes, "程式管理", "▦"),
        new(AppNavPage.Network, "網路監控", "↗"),
        new(AppNavPage.Limits, "速度限制", "◎"),
        new(AppNavPage.Settings, "設定", "⚙"),
        new(AppNavPage.History, "歷史記錄", "◷")
    ];

    private string _downloadHistoryPoints = "0,100";
    private string _uploadHistoryPoints = "0,100";
    private string _downloadAreaPoints = "0,100 320,100";
    private string _uploadAreaPoints = "0,100 320,100";
    private string _historyScaleText = "120";
    private string _historyWindowText = "最近 60 秒";
    private string _downloadPeakText = "0 B/s";
    private string _uploadPeakText = "0 B/s";
    private string _downloadChartMaxText = "120";
    private string _uploadChartMaxText = "120";
    private string _avg10SummaryText = "↓ 0  ↑ 0";
    private string _avg30SummaryText = "↓ 0  ↑ 0";
    private string _avg60SummaryText = "↓ 0  ↑ 0";
    private string _exportStatusText = "尚未匯出";
    private string _logCountText = "已累積 0 筆紀錄";
    private string _processStatusText = "正在讀取單一程式流量...";
    private string _limitEngineStatusText = string.Empty;
    private bool _isRunningAsAdmin;
    private string _selectedInterfaceLabel = "全部介面";
    private string _selectedInterfaceDetail = string.Empty;
    private string _localIpAddress = "—";
    private string _connectionStatusText = "檢查中";
    private bool _isNetworkConnected;
    private string _sessionDownloadText = "0 B";
    private string _sessionUploadText = "0 B";
    private string _sessionTotalText = "0 B";
    private string _todayDownloadText = "0 B";
    private string _todayUploadText = "0 B";
    private string _todayTotalText = "0 B";
    private string _yesterdayDownloadText = "0 B";
    private string _yesterdayUploadText = "0 B";
    private string _yesterdayTotalText = "0 B";
    private string _weekDownloadText = "0 B";
    private string _weekUploadText = "0 B";
    private string _weekTotalText = "0 B";
    private string _monthDownloadText = "0 B";
    private string _monthUploadText = "0 B";
    private string _monthTotalText = "0 B";
    private string _allTimeDownloadText = "0 B";
    private string _allTimeUploadText = "0 B";
    private string _allTimeTotalText = "0 B";
    private string _maxDownloadSpeedText = "0 B/s";
    private string _maxUploadSpeedText = "0 B/s";
    private string _runtimeText = "00:00:00";
    private double _sessionDownloadBytes;
    private double _sessionUploadBytes;
    private double _maxDownloadSpeed;
    private double _maxUploadSpeed;
    private DateTimeOffset? _sessionStartedAt;
    private bool _isExporting;
    private bool _isDisposed;
    private SortModeOption _selectedSortOption;
    private UiThemeOption _selectedUiTheme;
    private UiTheme _uiTheme = UiTheme.Integrated;
    private ThemePalette _palette = ThemePalette.For(UiTheme.Integrated);
    private NetworkInterfaceOption? _selectedInterface;
    private ObservableCollection<NetworkInterfaceOption> _networkInterfaces = [];
    private AppNavPage _selectedNavPage = AppNavPage.Overview;
    /// <summary>When true, next ApplyFilters re-sorts by current sort mode.</summary>
    private bool _processOrderDirty = true;
    /// <summary>Hold list membership/order so open limit ComboBoxes are not rebuilt.</summary>
    private DateTimeOffset _processListStableUntil = DateTimeOffset.MinValue;

    public MainWindowViewModel()
    {
        _birthdayEasterEgg = BirthdayEasterEgg.CreateFor(DateTime.Today);
        _csvExportService = new CsvExportService(AppContext.BaseDirectory);
        _networkMonitorService = new NetworkMonitorService();
        _trafficLimitService = new TrafficLimitService();
        _limitSettingsStore = new LimitSettingsStore(AppContext.BaseDirectory);
        _trafficStatsStore = new TrafficStatsStore(AppContext.BaseDirectory);
        _selectedSortOption = _sortModes[0];
        _selectedUiTheme = _uiThemes[0];
        _palette = ThemePalette.For(UiTheme.Integrated);
        _limitEngineStatusText = _trafficLimitService.CapabilityText;
        _isRunningAsAdmin = AdminElevation.IsElevated();
        _sessionStartedAt = DateTimeOffset.Now;
        Processes = new ObservableCollection<ProcessTrafficViewModel>();
        RefreshPeriodStatistics();
        RefreshNetworkInterfaces();

        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _timer.Tick += async (_, _) => await RefreshAsync();
        _timer.Start();

        _ = RefreshAsync();
    }

    public ObservableCollection<ProcessTrafficViewModel> Processes { get; }

    public IReadOnlyList<AppNavItem> NavItems => _navItems;

    public AppNavPage SelectedNavPage
    {
        get => _selectedNavPage;
        set
        {
            if (SetProperty(ref _selectedNavPage, value))
            {
                RaisePropertyChanged(nameof(IsOverviewPage));
                RaisePropertyChanged(nameof(IsProcessesPage));
                RaisePropertyChanged(nameof(IsNetworkPage));
                RaisePropertyChanged(nameof(IsLimitsPage));
                RaisePropertyChanged(nameof(IsSettingsPage));
                RaisePropertyChanged(nameof(IsHistoryPage));
                RaisePropertyChanged(nameof(PageTitle));
                RaisePropertyChanged(nameof(PageSubtitle));
                _processOrderDirty = true;
                ApplyFilters(forceResort: true);
            }
        }
    }

    public bool IsOverviewPage => SelectedNavPage == AppNavPage.Overview;
    public bool IsProcessesPage => SelectedNavPage is AppNavPage.Processes or AppNavPage.Limits;
    public bool IsNetworkPage => SelectedNavPage == AppNavPage.Network;
    public bool IsLimitsPage => SelectedNavPage == AppNavPage.Limits;
    public bool IsSettingsPage => SelectedNavPage == AppNavPage.Settings;
    public bool IsHistoryPage => SelectedNavPage == AppNavPage.History;

    public string PageTitle => SelectedNavPage switch
    {
        AppNavPage.Processes => "程式管理",
        AppNavPage.Network => "網路監控",
        AppNavPage.Limits => "速度限制",
        AppNavPage.Settings => "設定",
        AppNavPage.History => "歷史記錄",
        _ => "網路速度監控器"
    };

    public string PageSubtitle => SelectedNavPage switch
    {
        AppNavPage.Processes => "查看並管理各程式即時網路使用量",
        AppNavPage.Network => "即時頻寬曲線與介面狀態",
        AppNavPage.Limits => "限制單一程式下載 / 上傳速度",
        AppNavPage.Settings => "外觀、網卡與匯出設定",
        AppNavPage.History => "Session / 今日 / 週月累計流量",
        _ => "即時監控下載 / 上傳速度"
    };

    public ObservableCollection<NetworkInterfaceOption> NetworkInterfaces
    {
        get => _networkInterfaces;
        private set => SetProperty(ref _networkInterfaces, value);
    }

    public NetworkInterfaceOption? SelectedInterface
    {
        get => _selectedInterface;
        set
        {
            if (SetProperty(ref _selectedInterface, value) && value is not null)
            {
                _networkMonitorService.SetSelectedInterface(value.Id);
                SelectedInterfaceLabel = value.DisplayName;
            }
        }
    }

    public string SelectedInterfaceLabel
    {
        get => _selectedInterfaceLabel;
        private set => SetProperty(ref _selectedInterfaceLabel, value);
    }

    public string SelectedInterfaceDetail
    {
        get => _selectedInterfaceDetail;
        private set => SetProperty(ref _selectedInterfaceDetail, value);
    }

    public string LocalIpAddress
    {
        get => _localIpAddress;
        private set => SetProperty(ref _localIpAddress, value);
    }

    public string ConnectionStatusText
    {
        get => _connectionStatusText;
        private set
        {
            if (SetProperty(ref _connectionStatusText, value))
            {
                RaisePropertyChanged(nameof(NetworkLinkText));
            }
        }
    }

    public string NetworkLinkText =>
        IsNetworkConnected ? "網路已連線" : "網路未連線";

    public bool IsNetworkConnected
    {
        get => _isNetworkConnected;
        private set
        {
            if (SetProperty(ref _isNetworkConnected, value))
            {
                RaisePropertyChanged(nameof(ConnectionStatusBrush));
                RaisePropertyChanged(nameof(NetworkLinkText));
            }
        }
    }

    public IBrush ConnectionStatusBrush =>
        IsNetworkConnected
            ? new SolidColorBrush(Color.Parse("#22C55E"))
            : new SolidColorBrush(Color.Parse("#EF4444"));

    public IReadOnlyList<SortModeOption> SortModes => _sortModes;

    public IReadOnlyList<UiThemeOption> UiThemes => _uiThemes;

    public bool IsBirthdayEasterEggVisible => _birthdayEasterEgg is not null;

    public string BirthdayBadge => _birthdayEasterEgg?.Badge ?? string.Empty;

    public string BirthdayHeadline => _birthdayEasterEgg?.Headline ?? string.Empty;

    public string BirthdaySubheadline => _birthdayEasterEgg?.Subheadline ?? string.Empty;

    public string BirthdayHighlight => _birthdayEasterEgg?.Highlight ?? string.Empty;

    public string BirthdaySupportLine => _birthdayEasterEgg?.SupportLine ?? string.Empty;

    public string TotalDownloadSpeedText
    {
        get => _totalDownloadSpeedText;
        set => SetProperty(ref _totalDownloadSpeedText, value);
    }

    public string TotalUploadSpeedText
    {
        get => _totalUploadSpeedText;
        set => SetProperty(ref _totalUploadSpeedText, value);
    }

    public string LastUpdatedText
    {
        get => _lastUpdatedText;
        set => SetProperty(ref _lastUpdatedText, value);
    }

    public string DownloadHistoryPoints
    {
        get => _downloadHistoryPoints;
        set => SetProperty(ref _downloadHistoryPoints, value);
    }

    public string UploadHistoryPoints
    {
        get => _uploadHistoryPoints;
        set => SetProperty(ref _uploadHistoryPoints, value);
    }

    public string DownloadAreaPoints
    {
        get => _downloadAreaPoints;
        set => SetProperty(ref _downloadAreaPoints, value);
    }

    public string UploadAreaPoints
    {
        get => _uploadAreaPoints;
        set => SetProperty(ref _uploadAreaPoints, value);
    }

    public string HistoryScaleText
    {
        get => _historyScaleText;
        set => SetProperty(ref _historyScaleText, value);
    }

    public string HistoryWindowText
    {
        get => _historyWindowText;
        set => SetProperty(ref _historyWindowText, value);
    }

    public string DownloadPeakText
    {
        get => _downloadPeakText;
        set => SetProperty(ref _downloadPeakText, value);
    }

    public string UploadPeakText
    {
        get => _uploadPeakText;
        set => SetProperty(ref _uploadPeakText, value);
    }

    public string DownloadChartMaxText
    {
        get => _downloadChartMaxText;
        private set => SetProperty(ref _downloadChartMaxText, value);
    }

    public string UploadChartMaxText
    {
        get => _uploadChartMaxText;
        private set => SetProperty(ref _uploadChartMaxText, value);
    }

    public string Avg10SummaryText
    {
        get => _avg10SummaryText;
        set => SetProperty(ref _avg10SummaryText, value);
    }

    public string Avg30SummaryText
    {
        get => _avg30SummaryText;
        set => SetProperty(ref _avg30SummaryText, value);
    }

    public string Avg60SummaryText
    {
        get => _avg60SummaryText;
        set => SetProperty(ref _avg60SummaryText, value);
    }

    public string ExportStatusText
    {
        get => _exportStatusText;
        set => SetProperty(ref _exportStatusText, value);
    }

    public string LogCountText
    {
        get => _logCountText;
        set => SetProperty(ref _logCountText, value);
    }

    public string LimitEngineStatusText
    {
        get => _limitEngineStatusText;
        set => SetProperty(ref _limitEngineStatusText, value);
    }

    public bool IsRunningAsAdmin
    {
        get => _isRunningAsAdmin;
        private set
        {
            if (SetProperty(ref _isRunningAsAdmin, value))
            {
                RaisePropertyChanged(nameof(NeedsAdminForLimits));
                RaisePropertyChanged(nameof(AdminStatusText));
            }
        }
    }

    public bool NeedsAdminForLimits => OperatingSystem.IsWindows() && !IsRunningAsAdmin;

    public string AdminStatusText => IsRunningAsAdmin
        ? "已以系統管理員執行 · 可套用上傳限速 / Block"
        : "未以系統管理員執行 · 限速無法生效（請重新啟動並提權）";

    public string SessionDownloadText
    {
        get => _sessionDownloadText;
        private set => SetProperty(ref _sessionDownloadText, value);
    }

    public string SessionUploadText
    {
        get => _sessionUploadText;
        private set => SetProperty(ref _sessionUploadText, value);
    }

    public string SessionTotalText
    {
        get => _sessionTotalText;
        private set => SetProperty(ref _sessionTotalText, value);
    }

    public string TodayDownloadText
    {
        get => _todayDownloadText;
        private set => SetProperty(ref _todayDownloadText, value);
    }

    public string TodayUploadText
    {
        get => _todayUploadText;
        private set => SetProperty(ref _todayUploadText, value);
    }

    public string TodayTotalText
    {
        get => _todayTotalText;
        private set => SetProperty(ref _todayTotalText, value);
    }

    public string YesterdayDownloadText
    {
        get => _yesterdayDownloadText;
        private set => SetProperty(ref _yesterdayDownloadText, value);
    }

    public string YesterdayUploadText
    {
        get => _yesterdayUploadText;
        private set => SetProperty(ref _yesterdayUploadText, value);
    }

    public string YesterdayTotalText
    {
        get => _yesterdayTotalText;
        private set => SetProperty(ref _yesterdayTotalText, value);
    }

    public string WeekDownloadText
    {
        get => _weekDownloadText;
        private set => SetProperty(ref _weekDownloadText, value);
    }

    public string WeekUploadText
    {
        get => _weekUploadText;
        private set => SetProperty(ref _weekUploadText, value);
    }

    public string WeekTotalText
    {
        get => _weekTotalText;
        private set => SetProperty(ref _weekTotalText, value);
    }

    public string MonthDownloadText
    {
        get => _monthDownloadText;
        private set => SetProperty(ref _monthDownloadText, value);
    }

    public string MonthUploadText
    {
        get => _monthUploadText;
        private set => SetProperty(ref _monthUploadText, value);
    }

    public string MonthTotalText
    {
        get => _monthTotalText;
        private set => SetProperty(ref _monthTotalText, value);
    }

    public string AllTimeDownloadText
    {
        get => _allTimeDownloadText;
        private set => SetProperty(ref _allTimeDownloadText, value);
    }

    public string AllTimeUploadText
    {
        get => _allTimeUploadText;
        private set => SetProperty(ref _allTimeUploadText, value);
    }

    public string AllTimeTotalText
    {
        get => _allTimeTotalText;
        private set => SetProperty(ref _allTimeTotalText, value);
    }

    public string MaxDownloadSpeedText
    {
        get => _maxDownloadSpeedText;
        private set => SetProperty(ref _maxDownloadSpeedText, value);
    }

    public string MaxUploadSpeedText
    {
        get => _maxUploadSpeedText;
        private set => SetProperty(ref _maxUploadSpeedText, value);
    }

    public string RuntimeText
    {
        get => _runtimeText;
        private set => SetProperty(ref _runtimeText, value);
    }

    public string SessionStartedText =>
        _sessionStartedAt is null
            ? "—"
            : $"工作階段開始 {_sessionStartedAt:HH:mm:ss}";

    public string FooterInterfaceText
    {
        get
        {
            if (string.IsNullOrWhiteSpace(SelectedInterfaceDetail) ||
                SelectedInterfaceDetail == SelectedInterfaceLabel)
            {
                return SelectedInterfaceLabel;
            }

            return $"{SelectedInterfaceLabel} ({SelectedInterfaceDetail})";
        }
    }

    public bool IsExporting
    {
        get => _isExporting;
        set => SetProperty(ref _isExporting, value);
    }

    public string ProcessStatusText
    {
        get => _processStatusText;
        set => SetProperty(ref _processStatusText, value);
    }

    public bool HasNoProcesses => Processes.Count == 0;

    public string ProcessSummaryText =>
        HasNoProcesses
            ? ProcessStatusText
            : $"顯示 {Processes.Count} 個程式";

    public string ProcessDataSourceText => OperatingSystem.IsWindows()
        ? "資料來源：Windows ETW 網路事件 · 約每秒更新"
        : "單一程式流量僅支援 Windows ETW；macOS 版顯示總流量";

    public string IntegratedHelpText =>
        "上傳限速：Windows Policy-based QoS（需系統管理員，可實際生效）。" +
        " 下載限速：Windows 無法可靠限制入站，僅記錄設定。" +
        " 請對要限速的程式選擇「上傳限制」MB/s，並保持開關開啟。";

    public UiThemeOption SelectedUiTheme
    {
        get => _selectedUiTheme;
        set
        {
            if (value is null)
            {
                return;
            }

            if (SetProperty(ref _selectedUiTheme, value))
            {
                _uiTheme = value.Theme;
                _palette = ThemePalette.For(_uiTheme);
                RaisePaletteChanged();
            }
        }
    }

    public ThemePalette Palette => _palette;

    public IBrush WindowBackground => _palette.WindowBackground;
    public IBrush ChromeBackground => _palette.ChromeBackground;
    public IBrush SidebarBackground => _palette.SidebarBackground;
    public IBrush PanelBackground => _palette.PanelBackground;
    public IBrush PanelAltBackground => _palette.PanelAltBackground;
    public IBrush PanelBorder => _palette.PanelBorder;
    public IBrush HeaderBackground => _palette.HeaderBackground;
    public IBrush ChartBackground => _palette.ChartBackground;
    public IBrush MutedTextBrush => _palette.MutedText;
    public IBrush SecondaryTextBrush => _palette.SecondaryText;
    public IBrush DownloadAccentBrush => _palette.DownloadAccent;
    public IBrush UploadAccentBrush => _palette.UploadAccent;
    public IBrush DownloadFillBrush => _palette.DownloadFill;
    public IBrush UploadFillBrush => _palette.UploadFill;
    public IBrush DownloadCardBackground => _palette.DownloadCardBackground;
    public IBrush UploadCardBackground => _palette.UploadCardBackground;
    public IBrush AccentDotBrush => _palette.AccentDot;
    public IBrush NavActiveBackground => _palette.NavActiveBackground;
    public IBrush NavActiveForeground => _palette.NavActiveForeground;
    public IBrush SuccessTextBrush => _palette.SuccessText;
    public string DownloadStrokeColor => _palette.DownloadStroke;
    public string UploadStrokeColor => _palette.UploadStroke;

    public string ThemeSubtitle =>
        _uiTheme == UiTheme.Integrated
            ? "即時監控下載 / 上傳速度"
            : _palette.Subtitle;

    public string AppVersionText
    {
        get
        {
            var version = typeof(MainWindowViewModel).Assembly.GetName().Version;
            return version is null ? "v1.2.1" : $"v{version.Major}.{version.Minor}.{version.Build}";
        }
    }

    public string AppBuildInfoText
    {
        get
        {
            var admin = IsRunningAsAdmin ? "系統管理員" : "一般權限";
            var path = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var folder = Path.GetFileName(path);
            if (string.Equals(folder, "net8.0", StringComparison.OrdinalIgnoreCase))
            {
                folder = Path.GetFileName(Path.GetDirectoryName(path)) + "/" + folder;
            }

            return $"{AppVersionText} · {admin} · {folder}";
        }
    }

    private void RaisePaletteChanged()
    {
        RaisePropertyChanged(nameof(Palette));
        RaisePropertyChanged(nameof(WindowBackground));
        RaisePropertyChanged(nameof(ChromeBackground));
        RaisePropertyChanged(nameof(SidebarBackground));
        RaisePropertyChanged(nameof(PanelBackground));
        RaisePropertyChanged(nameof(PanelAltBackground));
        RaisePropertyChanged(nameof(PanelBorder));
        RaisePropertyChanged(nameof(HeaderBackground));
        RaisePropertyChanged(nameof(ChartBackground));
        RaisePropertyChanged(nameof(MutedTextBrush));
        RaisePropertyChanged(nameof(SecondaryTextBrush));
        RaisePropertyChanged(nameof(DownloadAccentBrush));
        RaisePropertyChanged(nameof(UploadAccentBrush));
        RaisePropertyChanged(nameof(DownloadFillBrush));
        RaisePropertyChanged(nameof(UploadFillBrush));
        RaisePropertyChanged(nameof(DownloadCardBackground));
        RaisePropertyChanged(nameof(UploadCardBackground));
        RaisePropertyChanged(nameof(AccentDotBrush));
        RaisePropertyChanged(nameof(NavActiveBackground));
        RaisePropertyChanged(nameof(NavActiveForeground));
        RaisePropertyChanged(nameof(SuccessTextBrush));
        RaisePropertyChanged(nameof(DownloadStrokeColor));
        RaisePropertyChanged(nameof(UploadStrokeColor));
        RaisePropertyChanged(nameof(ThemeSubtitle));
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                _processOrderDirty = true;
                ApplyFilters(forceResort: true);
            }
        }
    }

    public bool ShowOnlyActive
    {
        get => _showOnlyActive;
        set
        {
            if (SetProperty(ref _showOnlyActive, value))
            {
                _processOrderDirty = true;
                ApplyFilters(forceResort: true);
            }
        }
    }

    public SortModeOption SelectedSortOption
    {
        get => _selectedSortOption;
        set
        {
            if (SetProperty(ref _selectedSortOption, value))
            {
                _selectedSortMode = value.Mode;
                _processOrderDirty = true;
                ApplyFilters(forceResort: true);
            }
        }
    }

    public void SelectNav(AppNavPage page) => SelectedNavPage = page;

    public bool IsNavActive(AppNavPage page) => SelectedNavPage == page;

    public void RefreshNetworkInterfaces()
    {
        var interfaces = _networkMonitorService.GetInterfaces();
        NetworkInterfaces = new ObservableCollection<NetworkInterfaceOption>(interfaces);
        SelectedInterface ??= interfaces.FirstOrDefault();
        if (SelectedInterface is not null &&
            interfaces.All(i => i.Id != SelectedInterface.Id))
        {
            SelectedInterface = interfaces.FirstOrDefault();
        }
    }

    private async Task RefreshAsync()
    {
        var snapshot = await _networkMonitorService.CaptureAsync();

        TotalDownloadSpeedText = TrafficFormatter.FormatSpeed(snapshot.TotalDownloadBytesPerSecond);
        TotalUploadSpeedText = TrafficFormatter.FormatSpeed(snapshot.TotalUploadBytesPerSecond);
        LastUpdatedText = $"更新 {DateTime.Now:HH:mm:ss}";
        SelectedInterfaceLabel = snapshot.SelectedInterfaceName;
        SelectedInterfaceDetail = snapshot.SelectedInterfaceDetail;
        LocalIpAddress = snapshot.LocalIpAddress;
        IsNetworkConnected = snapshot.IsConnected;
        ConnectionStatusText = snapshot.IsConnected ? "良好" : "離線";
        RaisePropertyChanged(nameof(FooterInterfaceText));

        if (_sessionStartedAt is not null)
        {
            var elapsed = DateTimeOffset.Now - _sessionStartedAt.Value;
            RuntimeText = $"{(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}";
        }

        AppendHistory(_downloadHistory, snapshot.TotalDownloadBytesPerSecond);
        AppendHistory(_uploadHistory, snapshot.TotalUploadBytesPerSecond);
        AppendTrafficLog(snapshot);
        AccumulateTraffic(snapshot.TotalDownloadBytesPerSecond, snapshot.TotalUploadBytesPerSecond);
        UpdateHistoryChart();
        UpdateAverageSummaries();

        MergeProcesses(snapshot.Processes);
        ProcessStatusText = snapshot.ProcessStatusMessage;
        ApplyFilters();
    }

    private void AccumulateTraffic(double downloadBytesPerSecond, double uploadBytesPerSecond)
    {
        var downloadBytes = Math.Max(0, downloadBytesPerSecond);
        var uploadBytes = Math.Max(0, uploadBytesPerSecond);

        _sessionDownloadBytes += downloadBytes;
        _sessionUploadBytes += uploadBytes;
        SessionDownloadText = TrafficFormatter.FormatBytes(_sessionDownloadBytes);
        SessionUploadText = TrafficFormatter.FormatBytes(_sessionUploadBytes);
        SessionTotalText = TrafficFormatter.FormatBytes(_sessionDownloadBytes + _sessionUploadBytes);

        if (downloadBytesPerSecond > _maxDownloadSpeed)
        {
            _maxDownloadSpeed = downloadBytesPerSecond;
            MaxDownloadSpeedText = TrafficFormatter.FormatSpeed(_maxDownloadSpeed);
        }

        if (uploadBytesPerSecond > _maxUploadSpeed)
        {
            _maxUploadSpeed = uploadBytesPerSecond;
            MaxUploadSpeedText = TrafficFormatter.FormatSpeed(_maxUploadSpeed);
        }

        _trafficStatsStore.AddSample(DateOnly.FromDateTime(DateTime.Now), downloadBytes, uploadBytes);
        RefreshPeriodStatistics();
        RaisePropertyChanged(nameof(SessionStartedText));
    }

    private void RefreshPeriodStatistics()
    {
        var totals = _trafficStatsStore.GetPeriodTotals(DateOnly.FromDateTime(DateTime.Now));

        TodayDownloadText = TrafficFormatter.FormatBytes(totals.TodayDownloadBytes);
        TodayUploadText = TrafficFormatter.FormatBytes(totals.TodayUploadBytes);
        TodayTotalText = TrafficFormatter.FormatBytes(totals.TodayDownloadBytes + totals.TodayUploadBytes);

        YesterdayDownloadText = TrafficFormatter.FormatBytes(totals.YesterdayDownloadBytes);
        YesterdayUploadText = TrafficFormatter.FormatBytes(totals.YesterdayUploadBytes);
        YesterdayTotalText = TrafficFormatter.FormatBytes(totals.YesterdayDownloadBytes + totals.YesterdayUploadBytes);

        WeekDownloadText = TrafficFormatter.FormatBytes(totals.WeekDownloadBytes);
        WeekUploadText = TrafficFormatter.FormatBytes(totals.WeekUploadBytes);
        WeekTotalText = TrafficFormatter.FormatBytes(totals.WeekDownloadBytes + totals.WeekUploadBytes);

        MonthDownloadText = TrafficFormatter.FormatBytes(totals.MonthDownloadBytes);
        MonthUploadText = TrafficFormatter.FormatBytes(totals.MonthUploadBytes);
        MonthTotalText = TrafficFormatter.FormatBytes(totals.MonthDownloadBytes + totals.MonthUploadBytes);

        AllTimeDownloadText = TrafficFormatter.FormatBytes(totals.AllDownloadBytes);
        AllTimeUploadText = TrafficFormatter.FormatBytes(totals.AllUploadBytes);
        AllTimeTotalText = TrafficFormatter.FormatBytes(totals.AllDownloadBytes + totals.AllUploadBytes);
    }

    private void MergeProcesses(IReadOnlyList<ProcessTrafficSnapshot> snapshots)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var process in snapshots)
        {
            var key = process.ProcessName;
            seen.Add(key);

            if (!_processMap.TryGetValue(key, out var vm))
            {
                vm = new ProcessTrafficViewModel
                {
                    ProcessName = process.ProcessName
                };
                vm.LimitSettingsChanged += OnProcessLimitSettingsChangedAsync;
                vm.LoadLimitSettings(_limitSettingsStore.GetOrCreate(process.ProcessName));
                _processMap[key] = vm;
            }

            vm.UpdateTraffic(
                process.ProcessId,
                process.Description,
                process.DownloadBytesPerSecond,
                process.UploadBytesPerSecond);
        }

        foreach (var pair in _processMap.ToList())
        {
            if (seen.Contains(pair.Key))
            {
                continue;
            }

            if (pair.Value.HasActiveLimit)
            {
                pair.Value.UpdateTraffic(pair.Value.ProcessId, pair.Value.Description, 0, 0);
                continue;
            }

            pair.Value.LimitSettingsChanged -= OnProcessLimitSettingsChangedAsync;
            _processMap.Remove(pair.Key);
        }
    }

    private async Task OnProcessLimitSettingsChangedAsync(ProcessTrafficViewModel process)
    {
        // Keep process rows stable while the user is changing ComboBox / toggle values.
        FreezeProcessList(TimeSpan.FromSeconds(12));

        CancellationTokenSource cts;
        lock (_limitSync)
        {
            if (_limitDebounce.TryGetValue(process.ProcessName, out var existing))
            {
                existing.Cancel();
                existing.Dispose();
            }

            cts = new CancellationTokenSource();
            _limitDebounce[process.ProcessName] = cts;
        }

        try
        {
            await Task.Delay(450, cts.Token);
            await ApplyProcessLimitsAsync(process, cts.Token);
            // Stay stable a bit longer after apply so status text can be read.
            FreezeProcessList(TimeSpan.FromSeconds(6));
        }
        catch (OperationCanceledException)
        {
            // Newer edit supersedes this apply.
        }
    }

    private void FreezeProcessList(TimeSpan duration)
    {
        var until = DateTimeOffset.Now.Add(duration);
        if (until > _processListStableUntil)
        {
            _processListStableUntil = until;
        }
    }

    private async Task ApplyProcessLimitsAsync(ProcessTrafficViewModel process, CancellationToken cancellationToken)
    {
        var settings = process.ToLimitSettings();
        _limitSettingsStore.Upsert(process.ProcessName, settings);

        if (process.IsApplyingLimit)
        {
            return;
        }

        process.IsApplyingLimit = true;
        try
        {
            if (!settings.IsLimitControlEnabled)
            {
                var cleared = await _trafficLimitService.ApplyPriorityAsync(
                    process.ProcessName,
                    process.Description,
                    TrafficPriority.Normal,
                    0,
                    0,
                    cancellationToken);
                process.LimitStatusText = "已關閉控管 · " + cleared.Message;
                LimitEngineStatusText = process.LimitStatusText;
                return;
            }

            if (settings.Priority is not "Normal" ||
                settings.DownloadLimitEnabled ||
                settings.UploadLimitEnabled)
            {
                var priority = process.SelectedPriority.Priority;
                if (priority == TrafficPriority.Normal &&
                    (settings.DownloadLimitEnabled || settings.UploadLimitEnabled))
                {
                    priority = TrafficPriority.Limit;
                }

                var result = await _trafficLimitService.ApplyPriorityAsync(
                    process.ProcessName,
                    process.Description,
                    priority,
                    settings.DownloadLimitEnabled || priority is TrafficPriority.Limit or TrafficPriority.Low
                        ? settings.DownloadLimitKbps
                        : 0,
                    settings.UploadLimitEnabled || priority is TrafficPriority.Limit or TrafficPriority.Low
                        ? settings.UploadLimitKbps
                        : 0,
                    cancellationToken);

                process.LimitStatusText = result.Success
                    ? result.Message
                    : "失敗：" + result.Message;
                LimitEngineStatusText = process.LimitStatusText;
            }
            else
            {
                var clear = await _trafficLimitService.ApplyPriorityAsync(
                    process.ProcessName,
                    process.Description,
                    TrafficPriority.Normal,
                    0,
                    0,
                    cancellationToken);
                process.LimitStatusText = clear.Message;
                LimitEngineStatusText = clear.Message;
            }
        }
        finally
        {
            process.IsApplyingLimit = false;
        }
    }

    public async Task ExportTrafficHistoryAsync()
    {
        if (IsExporting)
        {
            return;
        }

        IsExporting = true;
        ExportStatusText = "匯出中...";

        try
        {
            var filePath = await _csvExportService.ExportTrafficHistoryAsync(_trafficLog);
            ExportStatusText = $"已匯出 {Path.GetFileName(filePath)}";
        }
        catch (Exception ex)
        {
            ExportStatusText = $"匯出失敗：{ex.Message}";
        }
        finally
        {
            IsExporting = false;
        }
    }

    public async Task ClearAllLimitsAsync()
    {
        await _trafficLimitService.RemoveAllAsync();
        foreach (var process in _processMap.Values)
        {
            process.LoadLimitSettings(new ProcessLimitSettings());
            _limitSettingsStore.Upsert(process.ProcessName, process.ToLimitSettings());
        }

        LimitEngineStatusText = NeedsAdminForLimits
            ? "已清除本機記錄。若先前以系統管理員套用過 QoS，請再以系統管理員清除。"
            : "已清除所有 NetWatcher 限速原則。";
    }

    public bool TryRestartAsAdministrator()
    {
        if (IsRunningAsAdmin)
        {
            LimitEngineStatusText = "已在系統管理員模式執行。";
            return false;
        }

        if (AdminElevation.TryRestartElevated())
        {
            LimitEngineStatusText = "正在以系統管理員重新啟動…";
            // Exit current non-elevated instance.
            if (Avalonia.Application.Current?.ApplicationLifetime is
                Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.Shutdown();
            }

            return true;
        }

        LimitEngineStatusText = "未能提權（可能取消了 UAC）。限速仍無法套用。";
        return false;
    }

    private void UpdateHistoryChart()
    {
        var downloadMax = Math.Max(_downloadHistory.DefaultIfEmpty(0).Max(), 1);
        var uploadMax = Math.Max(_uploadHistory.DefaultIfEmpty(0).Max(), 1);

        // Nice chart ceiling in MB/s domain, convert back for drawing scale.
        var downloadCeiling = NiceCeilingMBps(downloadMax);
        var uploadCeiling = NiceCeilingMBps(uploadMax);

        DownloadHistoryPoints = BuildPolylinePoints(_downloadHistory, downloadCeiling);
        UploadHistoryPoints = BuildPolylinePoints(_uploadHistory, uploadCeiling);
        DownloadAreaPoints = BuildAreaPolygonPoints(_downloadHistory, downloadCeiling);
        UploadAreaPoints = BuildAreaPolygonPoints(_uploadHistory, uploadCeiling);

        DownloadChartMaxText = FormatChartMaxLabel(downloadCeiling);
        UploadChartMaxText = FormatChartMaxLabel(uploadCeiling);
        HistoryScaleText = DownloadChartMaxText;
        HistoryWindowText = $"最近 {_downloadHistory.Count} 秒";
        DownloadPeakText = TrafficFormatter.FormatSpeed(_downloadHistory.DefaultIfEmpty(0).Max());
        UploadPeakText = TrafficFormatter.FormatSpeed(_uploadHistory.DefaultIfEmpty(0).Max());
    }

    private static string FormatChartMaxLabel(double bytesPerSecond)
    {
        var mbps = TrafficFormatter.BytesPerSecondToMBps(bytesPerSecond);
        if (mbps >= 1)
        {
            return mbps >= 10 ? $"{mbps:0} MB/s" : $"{mbps:0.#} MB/s";
        }

        var kbps = bytesPerSecond / 1024d;
        return kbps >= 1 ? $"{kbps:0.#} KB/s" : TrafficFormatter.FormatSpeed(bytesPerSecond);
    }

    private static double NiceCeilingMBps(double bytesPerSecond)
    {
        var mbps = Math.Max(TrafficFormatter.BytesPerSecondToMBps(bytesPerSecond), 0.01);
        double[] steps = [0.05, 0.1, 0.25, 0.5, 1, 2, 5, 10, 20, 50, 100, 200, 500];
        var ceiling = steps.FirstOrDefault(s => s >= mbps * 1.15);
        if (ceiling <= 0)
        {
            ceiling = Math.Ceiling(mbps / 50d) * 50d;
        }

        return ceiling * TrafficFormatter.BytesPerMBps;
    }

    private void UpdateAverageSummaries()
    {
        Avg10SummaryText = BuildAverageSummary(10);
        Avg30SummaryText = BuildAverageSummary(30);
        Avg60SummaryText = BuildAverageSummary(60);
    }

    private string BuildAverageSummary(int seconds)
    {
        var downloadAvg = AverageLast(_downloadHistory, seconds);
        var uploadAvg = AverageLast(_uploadHistory, seconds);
        return $"↓ {TrafficFormatter.FormatSpeed(downloadAvg)}  ↑ {TrafficFormatter.FormatSpeed(uploadAvg)}";
    }

    private static double AverageLast(Queue<double> history, int count)
    {
        if (history.Count == 0)
        {
            return 0;
        }

        var values = history.ToArray();
        var start = Math.Max(0, values.Length - count);
        var slice = values[start..];
        return slice.Average();
    }

    private static void AppendHistory(Queue<double> history, double value)
    {
        history.Enqueue(value);
        while (history.Count > HistoryCapacity)
        {
            history.Dequeue();
        }
    }

    private void AppendTrafficLog(NetworkSnapshot snapshot)
    {
        _trafficLog.Add(new TrafficLogEntry(
            DateTime.Now,
            snapshot.TotalDownloadBytesPerSecond,
            snapshot.TotalUploadBytesPerSecond));

        if (_trafficLog.Count > LogCapacity)
        {
            _trafficLog.RemoveRange(0, _trafficLog.Count - LogCapacity);
        }

        LogCountText = $"已累積 {_trafficLog.Count} 筆紀錄";
    }

    private static string BuildPolylinePoints(IEnumerable<double> samples, double maxValue)
    {
        var values = samples.ToArray();
        if (values.Length == 0)
        {
            return $"0,{SparkHeight}";
        }

        if (values.Length == 1)
        {
            var y = SparkHeight - (values[0] / maxValue * SparkHeight);
            return $"0,{y:0.##} {SparkWidth},{y:0.##}";
        }

        var step = SparkWidth / (values.Length - 1);
        return string.Join(
            " ",
            values.Select((value, index) =>
            {
                var x = index * step;
                var y = SparkHeight - (value / maxValue * SparkHeight);
                return $"{x:0.##},{y:0.##}";
            }));
    }

    private static string BuildAreaPolygonPoints(IEnumerable<double> samples, double maxValue)
    {
        var values = samples.ToArray();
        if (values.Length == 0)
        {
            return $"0,{SparkHeight} {SparkWidth},{SparkHeight}";
        }

        if (values.Length == 1)
        {
            var y = SparkHeight - (values[0] / maxValue * SparkHeight);
            return $"0,{SparkHeight} 0,{y:0.##} {SparkWidth},{y:0.##} {SparkWidth},{SparkHeight}";
        }

        var step = SparkWidth / (values.Length - 1);
        var line = string.Join(
            " ",
            values.Select((value, index) =>
            {
                var x = index * step;
                var y = SparkHeight - (value / maxValue * SparkHeight);
                return $"{x:0.##},{y:0.##}";
            }));

        var lastX = (values.Length - 1) * step;
        return $"0,{SparkHeight} {line} {lastX:0.##},{SparkHeight}";
    }

    private void ApplyFilters(bool forceResort = false)
    {
        IEnumerable<ProcessTrafficViewModel> query = _processMap.Values;

        if (SelectedNavPage == AppNavPage.Limits)
        {
            query = query.Where(x =>
                x.HasActiveLimit ||
                !x.SelectedDownloadLimit.IsUnlimited ||
                !x.SelectedUploadLimit.IsUnlimited);
        }
        else if (ShowOnlyActive)
        {
            query = query.Where(x =>
                x.DownloadBytesPerSecond > 0 ||
                x.UploadBytesPerSecond > 0 ||
                x.HasActiveLimit);
        }

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            query = query.Where(x =>
                x.ProcessName.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
                x.Description.Contains(SearchText, StringComparison.OrdinalIgnoreCase));
        }

        var filtered = query.ToList();
        var holdStable = DateTimeOffset.Now < _processListStableUntil;
        var shouldResort = (forceResort || _processOrderDirty || Processes.Count == 0) && !holdStable;

        List<ProcessTrafficViewModel> ordered;
        if (shouldResort)
        {
            ordered = SortProcesses(filtered).Take(100).ToList();
            _processOrderDirty = false;
        }
        else
        {
            // Keep existing visual order so open ComboBoxes are not destroyed by re-sort/rebuild.
            var filteredSet = new HashSet<ProcessTrafficViewModel>(filtered);
            ordered = Processes.Where(filteredSet.Contains).ToList();
            var alreadyListed = new HashSet<ProcessTrafficViewModel>(ordered);
            foreach (var process in SortProcesses(filtered.Where(p => !alreadyListed.Contains(p))))
            {
                ordered.Add(process);
            }

            if (ordered.Count > 100)
            {
                ordered = ordered.Take(100).ToList();
            }
        }

        SyncProcessList(ordered);

        RaisePropertyChanged(nameof(HasNoProcesses));
        RaisePropertyChanged(nameof(ProcessSummaryText));
    }

    private IOrderedEnumerable<ProcessTrafficViewModel> SortProcesses(IEnumerable<ProcessTrafficViewModel> source) =>
        _selectedSortMode switch
        {
            SortMode.Download => source.OrderByDescending(x => x.DownloadBytesPerSecond)
                .ThenBy(x => x.ProcessName, StringComparer.OrdinalIgnoreCase),
            SortMode.Upload => source.OrderByDescending(x => x.UploadBytesPerSecond)
                .ThenBy(x => x.ProcessName, StringComparer.OrdinalIgnoreCase),
            _ => source.OrderByDescending(x => x.TotalBytesPerSecond)
                .ThenBy(x => x.ProcessName, StringComparer.OrdinalIgnoreCase)
        };

    /// <summary>
    /// Update the bound list without Clear()+re-Add of every row (which tears down ComboBoxes).
    /// </summary>
    private void SyncProcessList(IReadOnlyList<ProcessTrafficViewModel> desired)
    {
        if (Processes.Count == desired.Count)
        {
            var identical = true;
            for (var i = 0; i < desired.Count; i++)
            {
                if (!ReferenceEquals(Processes[i], desired[i]))
                {
                    identical = false;
                    break;
                }
            }

            if (identical)
            {
                return;
            }
        }

        for (var i = Processes.Count - 1; i >= 0; i--)
        {
            var existing = Processes[i];
            var stillWanted = false;
            for (var j = 0; j < desired.Count; j++)
            {
                if (ReferenceEquals(desired[j], existing))
                {
                    stillWanted = true;
                    break;
                }
            }

            if (!stillWanted)
            {
                Processes.RemoveAt(i);
            }
        }

        for (var i = 0; i < desired.Count; i++)
        {
            var item = desired[i];
            var currentIndex = -1;
            for (var j = 0; j < Processes.Count; j++)
            {
                if (ReferenceEquals(Processes[j], item))
                {
                    currentIndex = j;
                    break;
                }
            }

            if (currentIndex == i)
            {
                continue;
            }

            if (currentIndex < 0)
            {
                Processes.Insert(Math.Min(i, Processes.Count), item);
            }
            else
            {
                Processes.Move(currentIndex, i);
            }
        }

        while (Processes.Count > desired.Count)
        {
            Processes.RemoveAt(Processes.Count - 1);
        }
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _timer.Stop();

        lock (_limitSync)
        {
            foreach (var cts in _limitDebounce.Values)
            {
                cts.Cancel();
                cts.Dispose();
            }

            _limitDebounce.Clear();
        }

        foreach (var process in _processMap.Values)
        {
            process.LimitSettingsChanged -= OnProcessLimitSettingsChangedAsync;
        }

        _trafficStatsStore.Flush();
        _trafficLimitService.Dispose();
        _networkMonitorService.Dispose();
    }
}

public sealed record BirthdayEasterEgg(
    string Badge,
    string Headline,
    string Subheadline,
    string Highlight,
    string SupportLine)
{
    public static BirthdayEasterEgg? CreateFor(DateTime today)
    {
        return (today.Month, today.Day) switch
        {
            (4, 3) => new BirthdayEasterEgg(
                "4 月 3 日彩蛋",
                "塗哥生日快樂特效已啟動",
                "今天頁面會自動送上生日彩蛋，主角是塗哥，旁邊同步帶出今彩539頭獎得主鋒兄。",
                "今彩539頭獎得主鋒兄",
                "塗哥生日快樂"),
            (11, 27) => new BirthdayEasterEgg(
                "11 月 27 日彩蛋",
                "鋒兄生日快樂特效已啟動",
                "每年 11 月 27 日自動切到鋒兄主場模式，並在頁面上同步顯示他的榜首稱號。",
                "高考三級資訊處理榜首鋒兄",
                "鋒兄生日快樂"),
            _ => null
        };
    }
}

public enum SortMode
{
    Total,
    Download,
    Upload
}

public enum UiTheme
{
    Integrated,
    NetBalancer,
    BWMeter,
    Eltrafico,
    GlassWire,
    NetLimiter
}

public sealed record SortModeOption(SortMode Mode, string Label)
{
    public override string ToString() => Label;
}

public sealed record UiThemeOption(UiTheme Theme, string Label)
{
    public override string ToString() => Label;
}
