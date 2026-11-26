using System.Text.Json;
using FilmExpiryChecker.Storage;

namespace FilmExpiryChecker.Letterboxd;

/// <summary>
/// Persistent cache of Letterboxd slug → IMDb ID (or null if tried and not found).
/// A cached null means "we tried and found nothing" — we don't retry on every run.
/// </summary>
public class ImdbIdCache
{
    private readonly string _filePath;
    private Dictionary<string, string?> _store = new();

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    public ImdbIdCache(string filePath)
    {
        _filePath = filePath;
        Load();
    }

    private void Load()
    {
        SafeFile.DiscardLeftover(_filePath);
        if (!File.Exists(_filePath)) return;
        try
        {
            var json = File.ReadAllText(_filePath);
            _store = JsonSerializer.Deserialize<Dictionary<string, string?>>(json, JsonOptions) ?? new();
        }
        catch { }
    }

    public void Save() => SafeFile.WriteAllText(_filePath, JsonSerializer.Serialize(_store, JsonOptions));

    /// <summary>Returns true if we have a result for this slug (even if it is null).</summary>
    public bool TryGet(string slug, out string? imdbId) => _store.TryGetValue(slug, out imdbId);

    public void Set(string slug, string? imdbId) => _store[slug] = imdbId;

    public int Count => _store.Count;
}
