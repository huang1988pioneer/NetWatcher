using System.Text.Json;

namespace NetWatcher.App;

public sealed class LimitSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _filePath;
    private readonly Dictionary<string, ProcessLimitSettings> _settings = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _sync = new();

    public LimitSettingsStore(string baseDirectory)
    {
        var folder = Path.Combine(baseDirectory, "settings");
        Directory.CreateDirectory(folder);
        _filePath = Path.Combine(folder, "limits.json");
        Load();
    }

    public ProcessLimitSettings GetOrCreate(string processName)
    {
        lock (_sync)
        {
            if (_settings.TryGetValue(processName, out var existing))
            {
                return existing;
            }

            var created = new ProcessLimitSettings();
            _settings[processName] = created;
            return created;
        }
    }

    public IReadOnlyDictionary<string, ProcessLimitSettings> Snapshot()
    {
        lock (_sync)
        {
            return _settings.ToDictionary(
                pair => pair.Key,
                pair => pair.Value,
                StringComparer.OrdinalIgnoreCase);
        }
    }

    public void Upsert(string processName, ProcessLimitSettings settings)
    {
        lock (_sync)
        {
            _settings[processName] = settings;
            Save();
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
            var data = JsonSerializer.Deserialize<Dictionary<string, ProcessLimitSettings>>(json, JsonOptions);
            if (data is null)
            {
                return;
            }

            foreach (var pair in data)
            {
                _settings[pair.Key] = pair.Value;
            }
        }
        catch
        {
            // Ignore corrupt settings; start clean.
        }
    }

    private void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(_settings, JsonOptions);
            File.WriteAllText(_filePath, json);
        }
        catch
        {
            // Best-effort persistence.
        }
    }
}
