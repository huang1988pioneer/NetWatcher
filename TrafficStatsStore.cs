using System.Text.Json;

namespace NetWatcher.App;

/// <summary>
/// Persistent daily traffic counters for BWMeter-style period statistics.
/// </summary>
public sealed class TrafficStatsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _filePath;
    private readonly object _sync = new();
    private readonly Dictionary<string, DailyTrafficTotals> _days = new(StringComparer.Ordinal);
    private int _dirtySamples;

    public TrafficStatsStore(string baseDirectory)
    {
        var folder = Path.Combine(baseDirectory, "settings");
        Directory.CreateDirectory(folder);
        _filePath = Path.Combine(folder, "traffic-stats.json");
        Load();
    }

    public void AddSample(DateOnly date, double downloadBytes, double uploadBytes)
    {
        if (downloadBytes <= 0 && uploadBytes <= 0)
        {
            return;
        }

        lock (_sync)
        {
            var key = date.ToString("yyyy-MM-dd");
            if (!_days.TryGetValue(key, out var day))
            {
                day = new DailyTrafficTotals { Date = key };
                _days[key] = day;
            }

            day.DownloadBytes += Math.Max(0, downloadBytes);
            day.UploadBytes += Math.Max(0, uploadBytes);
            _dirtySamples++;
            if (_dirtySamples >= 10)
            {
                Save();
                _dirtySamples = 0;
            }
        }
    }

    public void Flush()
    {
        lock (_sync)
        {
            if (_dirtySamples <= 0)
            {
                return;
            }

            Save();
            _dirtySamples = 0;
        }
    }

    public TrafficPeriodTotals GetPeriodTotals(DateOnly today)
    {
        lock (_sync)
        {
            double todayDl = 0, todayUl = 0;
            double weekDl = 0, weekUl = 0;
            double monthDl = 0, monthUl = 0;
            double allDl = 0, allUl = 0;
            double yesterdayDl = 0, yesterdayUl = 0;

            var weekStart = today.AddDays(-6);
            var yesterday = today.AddDays(-1);

            foreach (var day in _days.Values)
            {
                if (!DateOnly.TryParse(day.Date, out var date))
                {
                    continue;
                }

                allDl += day.DownloadBytes;
                allUl += day.UploadBytes;

                if (date == today)
                {
                    todayDl += day.DownloadBytes;
                    todayUl += day.UploadBytes;
                }

                if (date == yesterday)
                {
                    yesterdayDl += day.DownloadBytes;
                    yesterdayUl += day.UploadBytes;
                }

                if (date >= weekStart && date <= today)
                {
                    weekDl += day.DownloadBytes;
                    weekUl += day.UploadBytes;
                }

                if (date.Year == today.Year && date.Month == today.Month)
                {
                    monthDl += day.DownloadBytes;
                    monthUl += day.UploadBytes;
                }
            }

            return new TrafficPeriodTotals(
                todayDl, todayUl,
                yesterdayDl, yesterdayUl,
                weekDl, weekUl,
                monthDl, monthUl,
                allDl, allUl);
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return;
            }

            var json = File.ReadAllText(_filePath);
            var data = JsonSerializer.Deserialize<Dictionary<string, DailyTrafficTotals>>(json, JsonOptions);
            if (data is null)
            {
                return;
            }

            foreach (var pair in data)
            {
                _days[pair.Key] = pair.Value;
            }
        }
        catch
        {
            // Ignore corrupt stats file.
        }
    }

    private void Save()
    {
        try
        {
            // Keep about one year of daily rows.
            if (_days.Count > 400)
            {
                var keep = _days.Values
                    .OrderByDescending(d => d.Date)
                    .Take(366)
                    .ToDictionary(d => d.Date, d => d, StringComparer.Ordinal);
                _days.Clear();
                foreach (var pair in keep)
                {
                    _days[pair.Key] = pair.Value;
                }
            }

            File.WriteAllText(_filePath, JsonSerializer.Serialize(_days, JsonOptions));
        }
        catch
        {
            // Best-effort persistence.
        }
    }
}

public sealed class DailyTrafficTotals
{
    public string Date { get; set; } = string.Empty;

    public double DownloadBytes { get; set; }

    public double UploadBytes { get; set; }
}

public sealed record TrafficPeriodTotals(
    double TodayDownloadBytes,
    double TodayUploadBytes,
    double YesterdayDownloadBytes,
    double YesterdayUploadBytes,
    double WeekDownloadBytes,
    double WeekUploadBytes,
    double MonthDownloadBytes,
    double MonthUploadBytes,
    double AllDownloadBytes,
    double AllUploadBytes);
