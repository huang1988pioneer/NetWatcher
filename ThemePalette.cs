using Avalonia.Media;

namespace NetWatcher.App;

/// <summary>
/// Color skin for the NetWatcher dashboard shell.
/// Default palette matches the 網路速度監控器 reference UI.
/// </summary>
public sealed class ThemePalette
{
    public required IBrush WindowBackground { get; init; }
    public required IBrush ChromeBackground { get; init; }
    public required IBrush SidebarBackground { get; init; }
    public required IBrush PanelBackground { get; init; }
    public required IBrush PanelAltBackground { get; init; }
    public required IBrush PanelBorder { get; init; }
    public required IBrush HeaderBackground { get; init; }
    public required IBrush ChartBackground { get; init; }
    public required IBrush MutedText { get; init; }
    public required IBrush SecondaryText { get; init; }
    public required IBrush DownloadAccent { get; init; }
    public required IBrush UploadAccent { get; init; }
    public required IBrush DownloadFill { get; init; }
    public required IBrush UploadFill { get; init; }
    public required IBrush DownloadCardBackground { get; init; }
    public required IBrush UploadCardBackground { get; init; }
    public required IBrush AccentDot { get; init; }
    public required IBrush NavActiveBackground { get; init; }
    public required IBrush NavActiveForeground { get; init; }
    public required IBrush SuccessText { get; init; }
    public required string Subtitle { get; init; }
    public required string DownloadStroke { get; init; }
    public required string UploadStroke { get; init; }

    public static ThemePalette For(UiTheme theme) => theme switch
    {
        UiTheme.GlassWire => new ThemePalette
        {
            WindowBackground = Brush("#080B14"),
            ChromeBackground = Brush("#0D1220"),
            SidebarBackground = Brush("#0A0F1C"),
            PanelBackground = Brush("#0E1428"),
            PanelAltBackground = Brush("#12182C"),
            PanelBorder = Brush("#243056"),
            HeaderBackground = Brush("#151D32"),
            ChartBackground = Brush("#0A0F1E"),
            MutedText = Brush("#5E6F98"),
            SecondaryText = Brush("#8FA0C8"),
            DownloadAccent = Brush("#3B82F6"),
            UploadAccent = Brush("#22C55E"),
            DownloadFill = Brush("#403B82F6"),
            UploadFill = Brush("#4022C55E"),
            DownloadCardBackground = Brush("#0C1528"),
            UploadCardBackground = Brush("#0C1A14"),
            AccentDot = Brush("#3B82F6"),
            NavActiveBackground = Brush("#1E3A5F"),
            NavActiveForeground = Brush("#E8EEF5"),
            SuccessText = Brush("#22C55E"),
            Subtitle = "整合介面 · GlassWire 配色",
            DownloadStroke = "#3B82F6",
            UploadStroke = "#22C55E"
        },
        UiTheme.BWMeter => new ThemePalette
        {
            WindowBackground = Brush("#0A0F0A"),
            ChromeBackground = Brush("#0F1A0F"),
            SidebarBackground = Brush("#081208"),
            PanelBackground = Brush("#0A120A"),
            PanelAltBackground = Brush("#101810"),
            PanelBorder = Brush("#1E3A1E"),
            HeaderBackground = Brush("#122012"),
            ChartBackground = Brush("#020602"),
            MutedText = Brush("#4A7A4A"),
            SecondaryText = Brush("#9FD49F"),
            DownloadAccent = Brush("#39FF14"),
            UploadAccent = Brush("#FFD700"),
            DownloadFill = Brush("#4039FF14"),
            UploadFill = Brush("#40FFD700"),
            DownloadCardBackground = Brush("#081208"),
            UploadCardBackground = Brush("#120F08"),
            AccentDot = Brush("#39FF14"),
            NavActiveBackground = Brush("#1A3A1A"),
            NavActiveForeground = Brush("#E8F5E8"),
            SuccessText = Brush("#39FF14"),
            Subtitle = "整合介面 · BWMeter 配色",
            DownloadStroke = "#39FF14",
            UploadStroke = "#FFD700"
        },
        UiTheme.Eltrafico => new ThemePalette
        {
            WindowBackground = Brush("#1C1C1C"),
            ChromeBackground = Brush("#161616"),
            SidebarBackground = Brush("#141414"),
            PanelBackground = Brush("#1E1E1E"),
            PanelAltBackground = Brush("#242424"),
            PanelBorder = Brush("#333333"),
            HeaderBackground = Brush("#2A2A2A"),
            ChartBackground = Brush("#141414"),
            MutedText = Brush("#777777"),
            SecondaryText = Brush("#9AA0A6"),
            DownloadAccent = Brush("#58A6FF"),
            UploadAccent = Brush("#7DCEA0"),
            DownloadFill = Brush("#3058A6FF"),
            UploadFill = Brush("#307DCEA0"),
            DownloadCardBackground = Brush("#1B2230"),
            UploadCardBackground = Brush("#1B2A1F"),
            AccentDot = Brush("#58A6FF"),
            NavActiveBackground = Brush("#2A3A4A"),
            NavActiveForeground = Brush("#E8EEF5"),
            SuccessText = Brush("#7DCEA0"),
            Subtitle = "整合介面 · Eltrafico 配色",
            DownloadStroke = "#58A6FF",
            UploadStroke = "#7DCEA0"
        },
        UiTheme.NetLimiter => new ThemePalette
        {
            WindowBackground = Brush("#0F1419"),
            ChromeBackground = Brush("#1A222D"),
            SidebarBackground = Brush("#121820"),
            PanelBackground = Brush("#0C1727"),
            PanelAltBackground = Brush("#12233A"),
            PanelBorder = Brush("#2A3340"),
            HeaderBackground = Brush("#1A222D"),
            ChartBackground = Brush("#08111D"),
            MutedText = Brush("#6A7688"),
            SecondaryText = Brush("#8A96A8"),
            DownloadAccent = Brush("#4ADE80"),
            UploadAccent = Brush("#F59E0B"),
            DownloadFill = Brush("#404ADE80"),
            UploadFill = Brush("#40F59E0B"),
            DownloadCardBackground = Brush("#102018"),
            UploadCardBackground = Brush("#201610"),
            AccentDot = Brush("#2B6CB0"),
            NavActiveBackground = Brush("#1E3A5F"),
            NavActiveForeground = Brush("#E8EEF5"),
            SuccessText = Brush("#4ADE80"),
            Subtitle = "整合介面 · NetLimiter 配色",
            DownloadStroke = "#4ADE80",
            UploadStroke = "#F59E0B"
        },
        // Default / Integrated / NetBalancer — matches the reference dashboard image
        _ => new ThemePalette
        {
            WindowBackground = Brush("#0B1220"),
            ChromeBackground = Brush("#0D1526"),
            SidebarBackground = Brush("#0A101C"),
            PanelBackground = Brush("#111B2E"),
            PanelAltBackground = Brush("#162238"),
            PanelBorder = Brush("#243454"),
            HeaderBackground = Brush("#152038"),
            ChartBackground = Brush("#0C1528"),
            MutedText = Brush("#6B7C99"),
            SecondaryText = Brush("#9DB0CC"),
            DownloadAccent = Brush("#3B82F6"),
            UploadAccent = Brush("#22C55E"),
            DownloadFill = Brush("#403B82F6"),
            UploadFill = Brush("#4022C55E"),
            DownloadCardBackground = Brush("#0F1A30"),
            UploadCardBackground = Brush("#0F1F1A"),
            AccentDot = Brush("#3B82F6"),
            NavActiveBackground = Brush("#1A3A6A"),
            NavActiveForeground = Brush("#F0F6FF"),
            SuccessText = Brush("#22C55E"),
            Subtitle = "即時監控下載 / 上傳速度",
            DownloadStroke = "#3B82F6",
            UploadStroke = "#22C55E"
        }
    };

    private static IBrush Brush(string hex) => new SolidColorBrush(Color.Parse(hex));
}
