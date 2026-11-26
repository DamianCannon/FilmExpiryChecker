using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FilmExpiryChecker.JustWatch;

public class JustWatchCache
{
    private readonly string _filePath;
    private readonly int _expiryDays;
    private readonly ILogger _logger;
    private Dictionary<string, CachedFilmData> _store = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true
    };

    public JustWatchCache(string filePath, int expiryDays, ILogger? logger = null)
    {
        _filePath = filePath;
        _expiryDays = expiryDays;
        _logger = logger ?? NullLogger.Instance;
        Load();
    }

    private void Load()
    {
        if (!File.Exists(_filePath))
        {
            _store = new Dictionary<string, CachedFilmData>();
            return;
        }

        try
        {
            var json = File.ReadAllText(_filePath);
            _store = JsonSerializer.Deserialize<Dictionary<string, CachedFilmData>>(json, JsonOptions)
                     ?? new Dictionary<string, CachedFilmData>();
        }
        catch
        {
            _logger.LogWarning("Could not read cache file at {Path}, starting fresh.", _filePath);
            _store = new Dictionary<string, CachedFilmData>();
        }
    }

    public void Save()
    {
        var dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(_store, JsonOptions);
        File.WriteAllText(_filePath, json);
    }

    /// <summary>Returns true if a valid, non-expired entry exists for this film ID.</summary>
    public bool TryGet(string filmId, out CachedFilmData? data)
    {
        if (_store.TryGetValue(filmId, out data))
        {
            if ((DateTime.UtcNow - data.CachedAt).TotalDays < _expiryDays)
                return true;
        }

        data = null;
        return false;
    }

    public void Set(string filmId, List<Offer>? offers)
    {
        _store[filmId] = new CachedFilmData
        {
            CachedAt = DateTime.UtcNow,
            Offers = offers
        };
    }

    public int Count => _store.Count;
    public int ExpiredCount => _store.Count(kv => (DateTime.UtcNow - kv.Value.CachedAt).TotalDays >= _expiryDays);
}
