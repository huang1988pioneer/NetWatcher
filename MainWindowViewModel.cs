using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Threading;

namespace NetWatcher.App;

public sealed class MainWindowViewModel : ObservableObject, IDisposable
{
    private const int HistoryCapacity = 60;
    private const int LogCapacity = 3600;
    private const double WideLayoutBreakpoint = 1380;
    private const double ChartWidth = 720;
    private const double ChartHeight = 180;

    private readonly BirthdayEasterEgg? _birthdayEasterEgg;
    private readonly CsvExportService _csvExportService;
    private readonly NetworkMonitorService _networkMonitorService;
    private readonly DispatcherTimer _timer;
    private readonly Queue<double> _downloadHistory = new();
    private readonly Queue<double> _uploadHistory = new();
    private readonly List<TrafficLogEntry> _trafficLog = [];
    private List<ProcessTrafficViewModel> _allProcesses = [];
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
    private string _downloadHistoryPoints = "0,180";
    private string _uploadHistoryPoints = "0,180";
    private string _historyScaleText = "刻度 0 B/s";
    private string _historyWindowText = "最近 60 秒";
    private string _downloadPeakText = "峰值 0 B/s";
    private string _uploadPeakText = "峰值 0 B/s";
    private string _avg10SummaryText = "下行 0 B/s · 上行 0 B/s";
    private string _avg30SummaryText = "下行 0 B/s · 上行 0 B/s";
    private string _avg60SummaryText = "下行 0 B/s · 上行 0 B/s";
    private string _exportStatusText = "尚未匯出";
    private string _logCountText = "已累積 0 筆紀錄";
    private string _processStatusText = "正在讀取單一程式流量...";
    private bool _isExporting;
    private bool _isDisposed;
    private bool _isProcessSectionExpanded = true;
    private bool _isWideLayout;
    private SortModeOption _selectedSortOption;

    public MainWindowViewModel()
    {
        _birthdayEasterEgg = BirthdayEasterEgg.CreateFor(DateTime.Today);
        _csvExportService = new CsvExportService(AppContext.BaseDirectory);
        _networkMonitorService = new NetworkMonitorService();
        _selectedSortOption = _sortModes[0];
        Processes = new ObservableCollection<ProcessTrafficViewModel>();

        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _timer.Tick += async (_, _) => await RefreshAsync();
        _timer.Start();

        _ = RefreshAsync();
    }

    public ObservableCollection<ProcessTrafficViewModel> Processes { get; }

    public IReadOnlyList<SortModeOption> SortModes => _sortModes;

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
            : $"顯示 {Processes.Count} 個有流量的程式";

    public bool IsProcessSectionExpanded
    {
        get => _isProcessSectionExpanded;
        set
        {
            if (SetProperty(ref _isProcessSectionExpanded, value))
            {
                RaisePropertyChanged(nameof(ProcessSectionActionText));
            }
        }
    }

    public string ProcessSectionActionText => IsProcessSectionExpanded ? "收合" : "展開";

    public string ProcessDataSourceText => OperatingSystem.IsWindows()
        ? "資料來源：Windows ETW 網路事件，約每秒更新一次"
        : "單一程式流量僅支援 Windows ETW；macOS 版顯示總流量監控";

    public bool IsWideLayout
    {
        get => _isWideLayout;
        private set
        {
            if (SetProperty(ref _isWideLayout, value))
            {
                RaiseResponsiveLayoutChanged();
            }
        }
    }

    public string RootMargin => IsWideLayout ? "24" : "18";

    public string HeroPadding => IsWideLayout ? "26" : "22";

    public string HeroLayoutColumns => IsWideLayout ? "1.35*,1*" : "*";

    public string HeroLayoutRows => IsWideLayout ? "*" : "Auto,Auto";

    public int SummarySectionRow => 0;

    public int SummarySectionColumn => 0;

    public int HistorySectionRow => IsWideLayout ? 0 : 1;

    public int HistorySectionColumn => IsWideLayout ? 1 : 0;

    public string TitleColumns => IsWideLayout ? "*,Auto" : "*";

    public int LastUpdatedRow => IsWideLayout ? 0 : 1;

    public int LastUpdatedColumn => IsWideLayout ? 1 : 0;

    public string LastUpdatedMargin => IsWideLayout ? "0" : "0,12,0,0";

    public string OverviewMetricItemWidth => IsWideLayout ? "280" : "250";

    public string OverviewMetricItemHeight => IsWideLayout ? "108" : "100";

    public double HeroTitleFontSize => IsWideLayout ? 38 : 34;

    public string HistoryHeaderColumns => IsWideLayout ? "*,Auto" : "*";

    public int HistoryScaleRow => IsWideLayout ? 0 : 1;

    public int HistoryScaleColumn => IsWideLayout ? 1 : 0;

    public string HistoryScaleMargin => IsWideLayout ? "0" : "0,10,0,0";

    public string ExportColumns => IsWideLayout ? "Auto,*,Auto" : "*";

    public int ExportStatusRow => IsWideLayout ? 0 : 1;

    public int ExportStatusColumn => IsWideLayout ? 1 : 0;

    public int ExportHintRow => IsWideLayout ? 0 : 2;

    public int ExportHintColumn => 0;

    public string ExportHintMargin => IsWideLayout ? "0" : "0,2,0,0";

    public string SectionHeaderColumns => IsWideLayout ? "*,Auto" : "*";

    public int SectionMetaRow => IsWideLayout ? 0 : 1;

    public int SectionMetaColumn => 0;

    public string SectionMetaMargin => IsWideLayout ? "0" : "0,6,0,0";

    public string ProcessHeaderColumns => IsWideLayout ? "*,Auto" : "*";

    public int ProcessToggleRow => IsWideLayout ? 0 : 1;

    public int ProcessToggleColumn => IsWideLayout ? 1 : 0;

    public string ProcessToggleMargin => IsWideLayout ? "0" : "0,10,0,0";

    public double SearchBoxWidth => IsWideLayout ? 360 : 320;

    public double ChartViewboxHeight => IsWideLayout ? 190 : 170;

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                ApplyFilters();
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
                ApplyFilters();
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
                ApplyFilters();
            }
        }
    }

    public void UpdateLayoutForWidth(double width)
    {
        IsWideLayout = width >= WideLayoutBreakpoint;
    }

    private async Task RefreshAsync()
    {
        var snapshot = await _networkMonitorService.CaptureAsync();

        TotalDownloadSpeedText = TrafficFormatter.FormatBytesPerSecond(snapshot.TotalDownloadBytesPerSecond);
        TotalUploadSpeedText = TrafficFormatter.FormatBytesPerSecond(snapshot.TotalUploadBytesPerSecond);
        LastUpdatedText = $"更新時間 {DateTime.Now:HH:mm:ss}";
        AppendHistory(_downloadHistory, snapshot.TotalDownloadBytesPerSecond);
        AppendHistory(_uploadHistory, snapshot.TotalUploadBytesPerSecond);
        AppendTrafficLog(snapshot);
        UpdateHistoryChart();
        UpdateAverageSummaries();

        _allProcesses = snapshot.Processes
            .Select(process => new ProcessTrafficViewModel
            {
                ProcessName = process.ProcessName,
                Description = process.Description,
                DownloadBytesPerSecond = process.DownloadBytesPerSecond,
                UploadBytesPerSecond = process.UploadBytesPerSecond,
                DownloadSpeedText = TrafficFormatter.FormatBytesPerSecond(process.DownloadBytesPerSecond),
                UploadSpeedText = TrafficFormatter.FormatBytesPerSecond(process.UploadBytesPerSecond),
                ProcessId = process.ProcessId
            })
            .ToList();
        ProcessStatusText = snapshot.ProcessStatusMessage;

        ApplyFilters();
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

    private void UpdateHistoryChart()
    {
        var maxValue = Math.Max(
            Math.Max(_downloadHistory.DefaultIfEmpty(0).Max(), _uploadHistory.DefaultIfEmpty(0).Max()),
            1);

        DownloadHistoryPoints = BuildPolylinePoints(_downloadHistory, maxValue);
        UploadHistoryPoints = BuildPolylinePoints(_uploadHistory, maxValue);
        HistoryScaleText = $"刻度 {TrafficFormatter.FormatBytesPerSecond(maxValue)}";
        HistoryWindowText = $"最近 {_downloadHistory.Count} 秒";
        DownloadPeakText = $"下載峰值 {TrafficFormatter.FormatBytesPerSecond(_downloadHistory.DefaultIfEmpty(0).Max())}";
        UploadPeakText = $"上傳峰值 {TrafficFormatter.FormatBytesPerSecond(_uploadHistory.DefaultIfEmpty(0).Max())}";
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
        return $"下行 {TrafficFormatter.FormatBytesPerSecond(downloadAvg)} · 上行 {TrafficFormatter.FormatBytesPerSecond(uploadAvg)}";
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
            return $"0,{ChartHeight}";
        }

        if (values.Length == 1)
        {
            var y = ChartHeight - (values[0] / maxValue * ChartHeight);
            return $"0,{y:0.##} {ChartWidth},{y:0.##}";
        }

        var step = ChartWidth / (values.Length - 1);
        return string.Join(
            " ",
            values.Select((value, index) =>
            {
                var x = index * step;
                var y = ChartHeight - (value / maxValue * ChartHeight);
                return $"{x:0.##},{y:0.##}";
            }));
    }

    private void ApplyFilters()
    {
        IEnumerable<ProcessTrafficViewModel> query = _allProcesses;

        if (ShowOnlyActive)
        {
            query = query.Where(x => x.DownloadBytesPerSecond > 0 || x.UploadBytesPerSecond > 0);
        }

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            query = query.Where(x =>
                x.ProcessName.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
                x.Description.Contains(SearchText, StringComparison.OrdinalIgnoreCase));
        }

        query = _selectedSortMode switch
        {
            SortMode.Download => query.OrderByDescending(x => x.DownloadBytesPerSecond),
            SortMode.Upload => query.OrderByDescending(x => x.UploadBytesPerSecond),
            _ => query.OrderByDescending(x => x.TotalBytesPerSecond)
        };

        var ordered = query.Take(50).ToList();

        Processes.Clear();
        foreach (var process in ordered)
        {
            Processes.Add(process);
        }

        RaisePropertyChanged(nameof(HasNoProcesses));
        RaisePropertyChanged(nameof(ProcessSummaryText));
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _timer.Stop();
        _networkMonitorService.Dispose();
    }

    private void RaiseResponsiveLayoutChanged()
    {
        RaisePropertyChanged(nameof(RootMargin));
        RaisePropertyChanged(nameof(HeroPadding));
        RaisePropertyChanged(nameof(HeroLayoutColumns));
        RaisePropertyChanged(nameof(HeroLayoutRows));
        RaisePropertyChanged(nameof(SummarySectionRow));
        RaisePropertyChanged(nameof(SummarySectionColumn));
        RaisePropertyChanged(nameof(HistorySectionRow));
        RaisePropertyChanged(nameof(HistorySectionColumn));
        RaisePropertyChanged(nameof(TitleColumns));
        RaisePropertyChanged(nameof(LastUpdatedRow));
        RaisePropertyChanged(nameof(LastUpdatedColumn));
        RaisePropertyChanged(nameof(LastUpdatedMargin));
        RaisePropertyChanged(nameof(OverviewMetricItemWidth));
        RaisePropertyChanged(nameof(OverviewMetricItemHeight));
        RaisePropertyChanged(nameof(HeroTitleFontSize));
        RaisePropertyChanged(nameof(HistoryHeaderColumns));
        RaisePropertyChanged(nameof(HistoryScaleRow));
        RaisePropertyChanged(nameof(HistoryScaleColumn));
        RaisePropertyChanged(nameof(HistoryScaleMargin));
        RaisePropertyChanged(nameof(ExportColumns));
        RaisePropertyChanged(nameof(ExportStatusRow));
        RaisePropertyChanged(nameof(ExportStatusColumn));
        RaisePropertyChanged(nameof(ExportHintRow));
        RaisePropertyChanged(nameof(ExportHintColumn));
        RaisePropertyChanged(nameof(ExportHintMargin));
        RaisePropertyChanged(nameof(SectionHeaderColumns));
        RaisePropertyChanged(nameof(SectionMetaRow));
        RaisePropertyChanged(nameof(SectionMetaColumn));
        RaisePropertyChanged(nameof(SectionMetaMargin));
        RaisePropertyChanged(nameof(ProcessHeaderColumns));
        RaisePropertyChanged(nameof(ProcessToggleRow));
        RaisePropertyChanged(nameof(ProcessToggleColumn));
        RaisePropertyChanged(nameof(ProcessToggleMargin));
        RaisePropertyChanged(nameof(SearchBoxWidth));
        RaisePropertyChanged(nameof(ChartViewboxHeight));
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

public sealed record SortModeOption(SortMode Mode, string Label)
{
    public override string ToString() => Label;
}
