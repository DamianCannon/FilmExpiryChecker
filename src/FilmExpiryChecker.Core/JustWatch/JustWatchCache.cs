using System.Text.Json;
using Microsoft.Extensions.Logging;
using FilmExpiryChecker.Reporting;
using FilmExpiryChecker.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace FilmExpiryChecker.JustWatch;

public class JustWatchCache
{
    private readonly string _filePath;
    private readonly int _expiryDays;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly HashSet<int> _myServices;
    private Dictionary<string, CachedFilmData> _store = new();

    /// <summary>A film on one of my services (or free) is checked again after this long, whatever the cache days,
    /// so a newly announced leaving date is noticed within two days (#46).</summary>
    public static readonly TimeSpan MineCheckedEvery = TimeSpan.FromDays(2);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true
    };

    /// <param name="myServices">The services counted as mine, whose films are checked again more often.</param>
    public JustWatchCache(string filePath, int expiryDays, ILogger? logger = null, TimeProvider? time = null,
        IEnumerable<string>? myServices = null)
    {
        _filePath = filePath;
        _expiryDays = expiryDays;
        _logger = logger ?? NullLogger.Instance;
        _time = time ?? TimeProvider.System;
        _myServices = (myServices ?? []).Select(id => int.TryParse(id, out var n) ? n : -1).ToHashSet();
        Load();
    }

    private void Load()
    {
        SafeFile.DiscardLeftover(_filePath);
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

    public void Save() => SafeFile.WriteAllText(_filePath, JsonSerializer.Serialize(_store, JsonOptions));

    /// <summary>Returns true if a valid, non-expired entry exists for this film ID.</summary>
    public bool TryGet(string filmId, out CachedFilmData? data)
    {
        if (_store.TryGetValue(filmId, out data) && IsFresh(data))
            return true;

        data = null;
        return false;
    }

    /// <summary>
    /// Fresh until the cache days pass; or, sooner, until any of its listings reaches its leaving day (so a film
    /// that has left is noticed, #46); or, for a film on one of my services or free, after two days.
    /// </summary>
    private bool IsFresh(CachedFilmData entry)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var age = now - entry.CachedAt;
        if (age.TotalDays >= _expiryDays) return false;

        var offers = entry.Offers ?? [];
        if (offers.Any(o => o.ExpiryDate is { } last && last.Date <= now.Date)) return false;

        var mine = offers.Any(o => _myServices.Contains(o.PackageId) || OfferKinds.Of(o.MonetizationType) == OfferKind.Free);
        return !mine || age < MineCheckedEvery;
    }

    public void Set(string filmId, List<Offer>? offers)
    {
        _store[filmId] = new CachedFilmData
        {
            CachedAt = _time.GetUtcNow().UtcDateTime,
            Offers = offers
        };
    }

    public int Count => _store.Count;
    public int ExpiredCount => _store.Count(kv => !IsFresh(kv.Value));
}
