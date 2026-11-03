using System.Text.Json;
using System.Text.Json.Serialization;

namespace XpilotAudioEqualizer.Config;

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
[JsonSerializable(typeof(NormalizerSettings))]
internal partial class NormalizerJsonContext : JsonSerializerContext;

public sealed class ConfigStore : IDisposable
{
    private readonly object _sync = new();
    private readonly string _filePath;
    private readonly FileSystemWatcher? _watcher;
    private readonly Timer _saveTimer;
    private NormalizerSettings _current = new();
    private long _lastSelfWriteTicks;
    private bool _disposed;

    public event Action<NormalizerSettings>? SettingsChanged;

    public NormalizerSettings Current
    {
        get
        {
            var snapshot = Volatile.Read(ref _current);
            return snapshot;
        }
    }

    public string FilePath => _filePath;

    public ConfigStore(string? pluginDirectory = null)
    {
        var dir = pluginDirectory
            ?? Path.GetDirectoryName(typeof(ConfigStore).Assembly.Location)
            ?? AppContext.BaseDirectory;
        _filePath = Path.Combine(dir, "normalizer-config.json");

        _saveTimer = new Timer(SaveNow, null, Timeout.Infinite, Timeout.Infinite);
        LoadOrCreate();

        try
        {
            var watchDir = Path.GetDirectoryName(_filePath);
            if (watchDir is not null && Directory.Exists(watchDir))
            {
                _watcher = new FileSystemWatcher(watchDir, Path.GetFileName(_filePath))
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                    EnableRaisingEvents = true,
                };
                _watcher.Changed += OnFileChanged;
                _watcher.Created += OnFileChanged;
                _watcher.Renamed += OnFileChanged;
            }
        }
        catch
        {
            // Hot-reload is best-effort; never crash the host.
        }
    }

    public NormalizerSettings Update(Func<NormalizerSettings, NormalizerSettings> mutator, bool saveToDisk = true)
    {
        NormalizerSettings next;
        lock (_sync)
        {
            next = mutator(Volatile.Read(ref _current)).Clamp();
            Volatile.Write(ref _current, next);
        }
        SettingsChanged?.Invoke(next);
        if (saveToDisk)
        {
            _saveTimer.Change(250, Timeout.Infinite);
        }
        return next;
    }

    private void LoadOrCreate()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                var defaults = new NormalizerSettings().Clamp();
                Volatile.Write(ref _current, defaults);
                WriteFile(defaults);
                return;
            }
            var json = File.ReadAllText(_filePath);
            var parsed = JsonSerializer.Deserialize(json, NormalizerJsonContext.Default.NormalizerSettings);
            Volatile.Write(ref _current, (parsed ?? new NormalizerSettings()).Clamp());
        }
        catch
        {
            Volatile.Write(ref _current, new NormalizerSettings().Clamp());
        }
    }

    private void OnFileChanged(object? sender, FileSystemEventArgs e)
    {
        try
        {
            // Ignore our own debounced writes within 300 ms.
            var elapsedMs = (DateTime.UtcNow.Ticks - Volatile.Read(ref _lastSelfWriteTicks)) / (double)TimeSpan.TicksPerMillisecond;
            if (elapsedMs < 300)
            {
                return;
            }
            string json;
            try { json = File.ReadAllText(_filePath); }
            catch { return; }
            NormalizerSettings? parsed;
            try { parsed = JsonSerializer.Deserialize(json, NormalizerJsonContext.Default.NormalizerSettings); }
            catch { return; }
            if (parsed is null)
            {
                return;
            }
            var clamped = parsed.Clamp();
            NormalizerSettings before;
            lock (_sync)
            {
                before = Volatile.Read(ref _current);
                if (SettingsEqual(before, clamped))
                {
                    return;
                }
                Volatile.Write(ref _current, clamped);
            }
            SettingsChanged?.Invoke(clamped);
        }
        catch
        {
            // Never crash the host on hot-reload.
        }
    }

    private void SaveNow(object? state)
    {
        try
        {
            var snapshot = Volatile.Read(ref _current);
            WriteFile(snapshot);
        }
        catch
        {
            // Best-effort persistence.
        }
    }

    private void WriteFile(NormalizerSettings settings)
    {
        var json = JsonSerializer.Serialize(settings, NormalizerJsonContext.Default.NormalizerSettings);
        var tmp = _filePath + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, _filePath, overwrite: true);
        Volatile.Write(ref _lastSelfWriteTicks, DateTime.UtcNow.Ticks);
    }

    private static bool SettingsEqual(NormalizerSettings a, NormalizerSettings b) =>
        a.Enabled == b.Enabled &&
        string.Equals(a.PresetName, b.PresetName, StringComparison.Ordinal) &&
        a.TargetDbFs == b.TargetDbFs &&
        a.MaxBoostDb == b.MaxBoostDb &&
        a.MaxCutDb == b.MaxCutDb &&
        a.CompressionRatio == b.CompressionRatio &&
        a.AttackMs == b.AttackMs &&
        a.ReleaseMs == b.ReleaseMs &&
        a.GateOpenDb == b.GateOpenDb &&
        a.GateCloseDb == b.GateCloseDb &&
        a.NoiseFloorDb == b.NoiseFloorDb &&
        a.LimiterCeilingDb == b.LimiterCeilingDb;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        try { _watcher?.Dispose(); } catch { }
        try
        {
            _saveTimer.Change(Timeout.Infinite, Timeout.Infinite);
            SaveNow(null);
            _saveTimer.Dispose();
        }
        catch { }
    }
}
