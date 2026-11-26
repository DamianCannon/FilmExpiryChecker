using System.Collections.Concurrent;
using System.Globalization;
using FilmExpiryChecker.JustWatch;
using FilmExpiryChecker.Letterboxd;
using FilmExpiryChecker.Storage;

namespace FilmExpiryChecker.Web.Settings;

/// <summary>A streaming service you can tick, by name.</summary>
public sealed record StreamingService(string Id, string Name);

/// <summary>A country JustWatch covers, by name.</summary>
public sealed record Country(string Code, string Name);

/// <summary>
/// What the Settings page offers: the countries, each country's streaming services (from JustWatch, fetched once
/// per country while the app runs), and a check that a Letterboxd username is right.
/// </summary>
public sealed class ServiceCatalog(HttpClient http, DataFolder data, ILoggerFactory loggers)
{
    private readonly ConcurrentDictionary<string, Task<IReadOnlyList<StreamingService>>> _byRegion = new(StringComparer.OrdinalIgnoreCase);

    // The countries JustWatch serves that people are most likely to want, by name; the rest can be typed as codes.
    private static readonly string[] Codes =
    [
        "GB", "IE", "US", "CA", "AU", "NZ", "DE", "AT", "CH", "FR", "BE", "NL", "LU", "ES", "PT", "IT",
        "DK", "NO", "SE", "FI", "IS", "PL", "CZ", "HU", "GR", "TR", "ZA", "IN", "JP", "KR", "SG", "BR", "MX", "AR",
    ];

    public static IReadOnlyList<Country> Countries { get; } = Codes
        .Select(c => new Country(c, new RegionInfo(c).EnglishName))
        .OrderBy(c => c.Name, StringComparer.Ordinal)
        .ToList();

    /// <summary>The streaming services JustWatch lists for <paramref name="region"/>, by name. Empty if it can't be reached.</summary>
    public Task<IReadOnlyList<StreamingService>> ServicesIn(string region) =>
        _byRegion.GetOrAdd(region, Fetch).ContinueWith(t =>
        {
            if (t.Result.Count == 0) _byRegion.TryRemove(region, out _);   // try again next time
            return t.Result;
        }, TaskScheduler.Default);

    private async Task<IReadOnlyList<StreamingService>> Fetch(string region)
    {
        var cache = new JustWatchCache(data.JustWatchCacheFile, 7);
        var client = new JustWatchClient(http, region, cache, logger: loggers.CreateLogger<JustWatchClient>());
        try
        {
            return (await client.GetProvidersAsync())
                .Select(p => new StreamingService(p.PackageId.ToString(CultureInfo.InvariantCulture), p.ClearName))
                .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            loggers.CreateLogger<ServiceCatalog>().LogWarning(ex, "Couldn't fetch the services for {Region}.", region);
            return [];
        }
    }

    /// <summary>Checks that <paramref name="username"/> has a public watchlist with films on it.</summary>
    public Task<WatchlistCheck> CheckUsername(string username, CancellationToken ct = default) =>
        new LetterboxdScraper(http, username.Trim(), loggers.CreateLogger<LetterboxdScraper>()).CheckWatchlistAsync(ct);
}
