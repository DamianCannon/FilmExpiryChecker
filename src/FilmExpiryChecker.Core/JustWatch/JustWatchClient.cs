using System.Net.Http.Json;
using System.Text.Json;
using FilmExpiryChecker.Letterboxd;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FilmExpiryChecker.JustWatch;

public class JustWatchClient
{
    private readonly HttpClient _http;
    private readonly string _region;
    private readonly JustWatchCache _cache;
    private readonly Func<string, CancellationToken, Task<string?>>? _imdbIdResolver;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;

    private const string ApiUrl = "https://apis.justwatch.com/graphql";
    private const int RequestDelayMs = 500;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public JustWatchClient(
        HttpClient http,
        string region,
        JustWatchCache cache,
        Func<string, CancellationToken, Task<string?>>? imdbIdResolver = null,
        ILogger? logger = null,
        TimeProvider? time = null)
    {
        _http = http;
        _region = region;
        _cache = cache;
        _imdbIdResolver = imdbIdResolver;
        _logger = logger ?? NullLogger.Instance;
        _time = time ?? TimeProvider.System;
    }

    // ── Provider listing ─────────────────────────────────────────────────────

    public async Task<List<Package>> GetProvidersAsync(CancellationToken ct = default)
    {
        var byId = new Dictionary<int, Package>();

        foreach (var platform in new[] { "WEB", "IOS", "ANDROID", "TV", "TVOS" })
        {
            try
            {
                var pkgs = await FetchProvidersByPlatformAsync(platform, ct);
                foreach (var p in pkgs.Where(p => p.PackageId > 0))
                    byId.TryAdd(p.PackageId, p);
            }
            catch (HttpRequestException) { }
        }

        return byId.Values.ToList();
    }

    private async Task<List<Package>> FetchProvidersByPlatformAsync(string platform, CancellationToken ct)
    {
        var request = new GraphQlRequest
        {
            Query = """
                query GetPackages($country: Country!, $platform: Platform!) {
                    packages(country: $country, platform: $platform) {
                        id
                        packageId
                        clearName
                        technicalName
                        icon(profile: S100)
                    }
                }
                """,
            Variables = new { country = _region, platform }
        };

        var response = await PostAsync<ProvidersResponse>(request, ct);
        return response?.Data?.Packages ?? new List<Package>();
    }

    // ── Film enrichment ───────────────────────────────────────────────────────

    public async Task<List<Offer>?> GetOffersAsync(WatchlistFilm film, CancellationToken ct = default)
    {
        // Check cache first
        if (_cache.TryGet(film.FilmId, out var cached))
            return cached!.Offers;

        // Rate limiting
        await Task.Delay(TimeSpan.FromMilliseconds(RequestDelayMs), _time, ct);

        var offers = await FetchOffersFromApiAsync(film, ct);

        // Only cache definite results — not nulls, so improved matching logic is retried next run
        if (offers is not null)
            _cache.Set(film.FilmId, offers);

        return offers;
    }

    private static readonly string TitleOffersQuery = """
        query GetTitleOffers(
            $country: Country!
            $language: Language!
            $filter: TitleFilter
            $first: Int!
        ) {
            popularTitles(
                country: $country
                filter: $filter
                first: $first
            ) {
                edges {
                    node {
                        id
                        objectType
                        content(country: $country, language: $language) {
                            title
                            originalReleaseYear
                            externalIds {
                                imdbId
                            }
                        }
                        offers(country: $country, platform: WEB) {
                            monetizationType
                            package {
                                id
                                packageId
                            }
                            availableTo
                        }
                    }
                }
            }
        }
        """;

    private async Task<List<Offer>?> FetchOffersFromApiAsync(WatchlistFilm film, CancellationToken ct)
    {
        var edges = await SendSearchRequestAsync(film.Title, new { searchQuery = film.Title }, 10, ct);
        if (edges is null) return null;

        var match = FindBestMatch(film, edges);

        // Fallback: IMDb ID match within the same results (handles foreign/alternate titles)
        if (match is null && !string.IsNullOrEmpty(film.Slug) && _imdbIdResolver is not null)
        {
            var imdbId = await _imdbIdResolver(film.Slug, ct);
            if (!string.IsNullOrEmpty(imdbId))
                match = edges.Select(e => e.Node)
                             .FirstOrDefault(n => n?.Content?.ExternalIds?.ImdbId == imdbId);
        }

        if (match is null) return null;
        return match.Offers.Count > 0 ? match.Offers : new List<Offer>();
    }

    private async Task<List<TitleNode>?> SendSearchRequestAsync(
        string filmTitle, object filter, int first, CancellationToken ct)
    {
        var request = new GraphQlRequest
        {
            Query = TitleOffersQuery,
            Variables = new { country = _region, language = "en", first, filter }
        };

        try
        {
            var httpResponse = await _http.PostAsJsonAsync(ApiUrl, request, JsonOptions, ct);
            if (!httpResponse.IsSuccessStatusCode)
            {
                var body = await httpResponse.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("JustWatch API error for '{Title}': {Status} — {Body}",
                    filmTitle, httpResponse.StatusCode, body[..Math.Min(300, body.Length)]);
                return null;
            }
            var response = await httpResponse.Content.ReadFromJsonAsync<FilmSearchResponse>(JsonOptions, ct);
            return response?.Data?.PopularTitles?.Edges;
        }
        // A cancelled run must stop, not be mistaken for "film not found". A timeout still counts as a failure.
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("JustWatch API error for '{Title}': {Message}", filmTitle, ex.Message);
            return null;
        }
    }

    private static TitleDetail? FindBestMatch(WatchlistFilm film, List<TitleNode> edges)
    {
        if (edges.Count == 0) return null;

        var normalised = NormaliseTitle(film.Title);

        // Prefer exact title + year match
        foreach (var edge in edges)
        {
            var node = edge.Node;
            if (node?.Content is null) continue;

            if (NormaliseTitle(node.Content.Title) == normalised &&
                node.Content.OriginalReleaseYear == film.Year)
                return node;
        }

        // Fall back to title-only match
        foreach (var edge in edges)
        {
            var node = edge.Node;
            if (node?.Content is null) continue;

            if (NormaliseTitle(node.Content.Title) == normalised)
                return node;
        }

        // Final fallback: JustWatch title starts with ours (e.g. "…Part One" suffix)
        foreach (var edge in edges)
        {
            var node = edge.Node;
            if (node?.Content is null) continue;

            var jwNorm = NormaliseTitle(node.Content.Title);
            if (jwNorm.StartsWith(normalised + " ") || jwNorm.StartsWith(normalised + ":"))
                return node;
        }

        return null;
    }

    private static string NormaliseTitle(string title)
    {
        var s = title.ToLowerInvariant()
                     .Replace('–', '-')
                     .Replace('—', '-')
                     .Replace(":", "")
                     .Replace("'", "")
                     .Replace(",", "")
                     .Replace("  ", " ")
                     .Trim();
        if (s.StartsWith("the ")) s = s[4..];
        else if (s.StartsWith("a ")) s = s[2..];
        return s;
    }

    // ── HTTP helper ───────────────────────────────────────────────────────────

    private async Task<T?> PostAsync<T>(GraphQlRequest request, CancellationToken ct)
    {
        var httpRequest = new HttpRequestMessage(HttpMethod.Post, ApiUrl);
        httpRequest.Headers.Add("Accept", "application/json");
        httpRequest.Headers.Add("Accept-Language", "en-GB,en;q=0.9");
        httpRequest.Headers.Add("Origin", "https://www.justwatch.com");
        httpRequest.Headers.Add("Referer", "https://www.justwatch.com/");
        httpRequest.Headers.Add("device-uniqueid", Guid.NewGuid().ToString());

        httpRequest.Content = JsonContent.Create(request, options: JsonOptions);

        var response = await _http.SendAsync(httpRequest, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, ct);
    }
}
