using System.Globalization;

namespace XpilotAudioEqualizer.Config;

/// <summary>
/// Minimal camelCase JSON reader/writer for <see cref="NormalizerSettings"/>.
/// System.Text.Json's serializer core is trimmed out of xPilot's single-file
/// bundle (only metadata shells remain), so config I/O must not touch it.
/// Flat object of doubles/bool/string only; unknown keys ignored, all values
/// pass through Clamp().
/// </summary>
internal static class NormalizerJson
{
    public static NormalizerSettings? TryParse(string json)
    {
        try
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            int i = 0;
            SkipWs(json, ref i);
            if (!Take(json, ref i, '{')) return null;
            while (true)
            {
                SkipWs(json, ref i);
                if (Take(json, ref i, '}')) break;
                var key = ParseString(json, ref i);
                if (key is null) return null;
                SkipWs(json, ref i);
                if (!Take(json, ref i, ':')) return null;
                var value = ParseValue(json, ref i);
                if (value is null) return null;
                values[key] = value;
                SkipWs(json, ref i);
                if (Take(json, ref i, ',')) continue;
                SkipWs(json, ref i);
                if (Take(json, ref i, '}')) break;
                return null;
            }
            var s = new NormalizerSettings();
            if (values.TryGetValue("enabled", out var en) && bool.TryParse(en, out var b)) s = s with { Enabled = b };
            if (values.TryGetValue("presetName", out var pn)) s = s with { PresetName = Unquote(pn) };
            s = SetDouble(values, s, "targetDbFs", static (x, v) => x with { TargetDbFs = v });
            s = SetDouble(values, s, "maxBoostDb", static (x, v) => x with { MaxBoostDb = v });
            s = SetDouble(values, s, "maxCutDb", static (x, v) => x with { MaxCutDb = v });
            s = SetDouble(values, s, "compressionRatio", static (x, v) => x with { CompressionRatio = v });
            s = SetDouble(values, s, "attackMs", static (x, v) => x with { AttackMs = v });
            s = SetDouble(values, s, "releaseMs", static (x, v) => x with { ReleaseMs = v });
            s = SetDouble(values, s, "gateOpenDb", static (x, v) => x with { GateOpenDb = v });
            s = SetDouble(values, s, "gateCloseDb", static (x, v) => x with { GateCloseDb = v });
            s = SetDouble(values, s, "noiseFloorDb", static (x, v) => x with { NoiseFloorDb = v });
            s = SetDouble(values, s, "limiterCeilingDb", static (x, v) => x with { LimiterCeilingDb = v });
            return s;
        }
        catch
        {
            return null;
        }
    }

    public static string Serialize(NormalizerSettings s)
    {
        static string N(double v) => v.ToString("G17", CultureInfo.InvariantCulture);
        return "{\n"
            + $"  \"enabled\": {(s.Enabled ? "true" : "false")},\n"
            + $"  \"presetName\": \"{Escape(s.PresetName)}\",\n"
            + $"  \"targetDbFs\": {N(s.TargetDbFs)},\n"
            + $"  \"maxBoostDb\": {N(s.MaxBoostDb)},\n"
            + $"  \"maxCutDb\": {N(s.MaxCutDb)},\n"
            + $"  \"compressionRatio\": {N(s.CompressionRatio)},\n"
            + $"  \"attackMs\": {N(s.AttackMs)},\n"
            + $"  \"releaseMs\": {N(s.ReleaseMs)},\n"
            + $"  \"gateOpenDb\": {N(s.GateOpenDb)},\n"
            + $"  \"gateCloseDb\": {N(s.GateCloseDb)},\n"
            + $"  \"noiseFloorDb\": {N(s.NoiseFloorDb)},\n"
            + $"  \"limiterCeilingDb\": {N(s.LimiterCeilingDb)}\n"
            + "}";
    }

    private static NormalizerSettings SetDouble(
        Dictionary<string, string> values, NormalizerSettings s, string key,
        Func<NormalizerSettings, double, NormalizerSettings> set)
    {
        if (values.TryGetValue(key, out var raw)
            && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
        {
            return set(s, v);
        }
        return s;
    }

    private static void SkipWs(string json, ref int i)
    {
        while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
    }

    private static bool Take(string json, ref int i, char c)
    {
        if (i < json.Length && json[i] == c) { i++; return true; }
        return false;
    }

    private static string? ParseString(string json, ref int i)
    {
        SkipWs(json, ref i);
        if (i >= json.Length || json[i] != '"') return null;
        i++;
        var sb = new System.Text.StringBuilder();
        while (i < json.Length)
        {
            char c = json[i++];
            if (c == '"') return sb.ToString();
            if (c == '\\' && i < json.Length)
            {
                char e = json[i++];
                sb.Append(e switch { '"' => '"', '\\' => '\\', 'n' => '\n', 'r' => '\r', 't' => '\t', _ => e });
            }
            else sb.Append(c);
        }
        return null;
    }

    private static string? ParseValue(string json, ref int i)
    {
        SkipWs(json, ref i);
        if (i >= json.Length) return null;
        if (json[i] == '"') { var s = ParseString(json, ref i); return s is null ? null : "\"" + s + "\""; }
        int start = i;
        while (i < json.Length && ",}".IndexOf(json[i]) < 0) i++;
        var token = json[start..i].Trim();
        return token.Length == 0 ? null : token;
    }

    private static string Unquote(string v) =>
        v.Length >= 2 && v[0] == '"' ? ParseEscapes(v[1..^1]) : v;

    private static string ParseEscapes(string s)
    {
        if (s.IndexOf('\\') < 0) return s;
        var sb = new System.Text.StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && i + 1 < s.Length)
            {
                i++;
                sb.Append(s[i] switch { '"' => '"', '\\' => '\\', 'n' => '\n', 'r' => '\r', 't' => '\t', _ => s[i] });
            }
            else sb.Append(s[i]);
        }
        return sb.ToString();
    }

    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
}

public sealed class ConfigStore : IDisposable
{
    private readonly object _sync = new();
    private readonly string _filePath;
    private readonly Timer _saveTimer;
    private readonly Timer _pollTimer;
    private NormalizerSettings _current = new();
    private long _lastSelfWriteTicks;
    private string _lastSeenContent = string.Empty;
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
        _lastSeenContent = ReadAllTextSafe(_filePath) ?? string.Empty;
        // NOTE: xPilot's self-contained host trims FileSystemWatcher AND
        // File.GetLastWriteTimeUtc/FileInfo, so mtime polling is impossible.
        // Poll by content compare instead — File.OpenRead + ReadAllBytes exist.
        _pollTimer = new Timer(PollForExternalChange, null, 1000, 1000);
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
            var json = ReadAllTextSafe(_filePath);
            if (json is null)
            {
                Volatile.Write(ref _current, new NormalizerSettings().Clamp());
                return;
            }
            var parsed = NormalizerJson.TryParse(json);
            Volatile.Write(ref _current, (parsed ?? new NormalizerSettings()).Clamp());
        }
        catch
        {
            Volatile.Write(ref _current, new NormalizerSettings().Clamp());
        }
    }

    private void PollForExternalChange(object? state)
    {
        try
        {
            if (_disposed)
            {
                return;
            }
            var json = ReadAllTextSafe(_filePath);
            if (json is null || string.Equals(json, _lastSeenContent, StringComparison.Ordinal))
            {
                return;
            }
            _lastSeenContent = json;
            // Ignore our own debounced writes within 300 ms.
            // NOTE: no Volatile — the long overload is trimmed from the host runtime.
            var elapsedMs = (DateTime.UtcNow.Ticks - _lastSelfWriteTicks) / (double)TimeSpan.TicksPerMillisecond;
            if (elapsedMs < 300)
            {
                return;
            }
            NormalizerSettings? parsed;
            try { parsed = NormalizerJson.TryParse(json); }
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

    // File.ReadAllText / File.ReadAllBytes(str) / StreamReader.ReadToEnd are all
    // trimmed from the host; File.OpenRead + FileStream.Read are present.
    private static string? ReadAllTextSafe(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }
            using var fs = File.OpenRead(path);
            var buf = new byte[fs.Length];
            int read = 0;
            while (read < buf.Length)
            {
                int n = fs.Read(buf, read, buf.Length - read);
                if (n <= 0)
                {
                    break;
                }
                read += n;
            }
            return System.Text.Encoding.UTF8.GetString(buf, 0, read);
        }
        catch
        {
            return null;
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
        var json = NormalizerJson.Serialize(settings);
        var tmp = _filePath + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(json);
            fs.Write(bytes, 0, bytes.Length);
        }
        if (File.Exists(_filePath))
        {
            File.Delete(_filePath);
        }
        // File.Move is trimmed; rename via stream copy + delete.
        using (var src = new FileStream(tmp, FileMode.Open, FileAccess.Read, FileShare.None))
        using (var dst = new FileStream(_filePath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            src.CopyTo(dst);
        }
        File.Delete(tmp);
        // NOTE: no Volatile — the long overload is trimmed from the host runtime.
        _lastSelfWriteTicks = DateTime.UtcNow.Ticks;
        _lastSeenContent = json;
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
        try { _pollTimer.Change(Timeout.Infinite, Timeout.Infinite); } catch { }
        try { _pollTimer.Dispose(); } catch { }
        try
        {
            _saveTimer.Change(Timeout.Infinite, Timeout.Infinite);
            SaveNow(null);
            _saveTimer.Dispose();
        }
        catch { }
    }
}
