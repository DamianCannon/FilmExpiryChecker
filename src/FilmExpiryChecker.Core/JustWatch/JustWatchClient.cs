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

    public async Task<List<Offer>?> GetOffersAsync(WatchlistFilm film, CancellationToken ct = default) =>
        (await LookUpAsync(film, ct)).Offers;

    /// <summary>The film's offers, and which JustWatch title they're from.</summary>
    public async Task<FilmLookup> LookUpAsync(WatchlistFilm film, CancellationToken ct = default)
    {
        // Check cache first
        if (_cache.TryGet(film.FilmId, out var cached))
            return new FilmLookup(cached!.Offers, cached.Match);

        // Rate limiting
        await Task.Delay(TimeSpan.FromMilliseconds(RequestDelayMs), _time, ct);

        var (offers, node) = await FetchOffersFromApiAsync(film, ct);
        Fetched++;
        if (offers is null) FetchedNotFound++;
        var match = node is null ? null : new TitleMatch(node.Id, node.Content?.Title ?? "", node.Content?.OriginalReleaseYear);

        // Only cache definite results — not nulls, so improved matching logic is retried next run
        if (offers is not null)
            _cache.Set(film.FilmId, offers, match);

        return new FilmLookup(offers, match);
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

    private async Task<(List<Offer>? Offers, TitleDetail? Match)> FetchOffersFromApiAsync(WatchlistFilm film, CancellationToken ct)
    {
        var edges = await SendSearchRequestAsync(film.Title, new { searchQuery = film.Title }, 10, ct);
        if (edges is null) return (null, null);

        var match = ExactMatch(film, edges) ?? await ConfirmedMatchAsync(film, edges, ct);
        if (match is null) return (null, null);
        if (match.Offers.Count == 0) match = TwinWithOffers(match, edges) ?? match;
        return (match.Offers.Count > 0 ? match.Offers : new List<Offer>(), match);
    }

    /// <summary>
    /// Not sure by title and year, the IMDb ID from Letterboxd decides (#48):
    /// <list type="bullet">
    /// <item>a result with the film's own IMDb ID is the film, whatever its title or year, and even if JustWatch
    /// calls it a series (Letterboxd lists miniseries such as "O.J.: Made in America" as films);</item>
    /// <item>a near match (a year out, or a longer title) is refused if its IMDb ID is another film's:
    /// "The Beast (2023)" isn't "Beast (2022)".</item>
    /// </list>
    /// Without an IMDb ID, the first near match is taken.
    /// </summary>
    private async Task<TitleDetail?> ConfirmedMatchAsync(WatchlistFilm film, List<TitleNode> edges, CancellationToken ct)
    {
        var near = NearMatches(film, edges).ToList();
        var imdbId = !string.IsNullOrEmpty(film.Slug) && _imdbIdResolver is not null
            ? await _imdbIdResolver(film.Slug, ct)
            : null;
        if (string.IsNullOrEmpty(imdbId)) return near.FirstOrDefault();

        var own = edges.Select(e => e.Node).OfType<TitleDetail>()
                       .FirstOrDefault(n => n.Content?.ExternalIds?.ImdbId == imdbId);
        return own ?? near.FirstOrDefault(n => n.Content?.ExternalIds?.ImdbId is not { Length: > 0 } theirs || theirs == imdbId);
    }

    /// <summary>
    /// JustWatch sometimes lists one title twice and puts the offers on the other entry: "O.J.: Made in America"
    /// is a film with no offers and a series (same title, same year) on Disney+; "Time Travel Is Dangerous" has
    /// the IMDb ID, its twin "…Dangerous!" the offers. A twin has the same title (punctuation aside) and is a film
    /// within a year, or a series of the same year.
    /// </summary>
    private static TitleDetail? TwinWithOffers(TitleDetail match, List<TitleNode> edges)
    {
        var title = NormaliseTitle(match.Content!.Title);
        var year = match.Content.OriginalReleaseYear;
        return edges.Select(e => e.Node).OfType<TitleDetail>().FirstOrDefault(n =>
            n != match && n.Offers.Count > 0 && n.Content is not null
            && NormaliseTitle(n.Content.Title) == title
            && (string.Equals(n.ObjectType, "MOVIE", StringComparison.OrdinalIgnoreCase)
                ? YearsAgree(year, n.Content.OriginalReleaseYear)
                : year is not null && n.Content.OriginalReleaseYear == year));
    }

    /// <summary>Only films: a TV series with the same title isn't the watchlist film.</summary>
    private static IEnumerable<TitleDetail> Movies(List<TitleNode> edges) =>
        edges.Select(e => e.Node)
             .OfType<TitleDetail>()
             .Where(n => n.Content is not null && string.Equals(n.ObjectType, "MOVIE", StringComparison.OrdinalIgnoreCase));

    /// <summary>Years agree if either is unknown, or they're at most a year apart (release years vary by country).</summary>
    private static bool YearsAgree(int? ours, int? theirs) =>
        ours is null || theirs is null || Math.Abs(ours.Value - theirs.Value) <= 1;

    private async Task<List<TitleNode>?> SendSearchRequestAsync(
        string filmTitle, object filter, int first, CancellationToken ct)
    {
        var request = new GraphQlRequest
        {
            Query = TitleOffersQuery,
            Variables = new { country = _region, language = "en", first, filter }
        };

        Searches++;
        try
        {
            var httpResponse = await _http.PostAsJsonAsync(ApiUrl, request, JsonOptions, ct);
            if (!httpResponse.IsSuccessStatusCode)
            {
                var body = await httpResponse.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("JustWatch API error for '{Title}': {Status} — {Body}",
                    filmTitle, httpResponse.StatusCode, body[..Math.Min(300, body.Length)]);
                return SearchFailed();
            }
            var response = await httpResponse.Content.ReadFromJsonAsync<FilmSearchResponse>(JsonOptions, ct);

            // A 200 with GraphQL errors, or without the results asked for, means JustWatch no longer understood the
            // question: a failure, not "no match" (an empty list of results is a real "no match").
            if (response?.Errors is { Count: > 0 } || response?.Data?.PopularTitles?.Edges is not { } edges)
            {
                _logger.LogWarning("JustWatch answered the search for '{Title}' without results{Errors}; it may have changed.",
                    filmTitle, response?.Errors is { Count: > 0 } errors ? $": {errors[0]}" : "");
                return SearchFailed();
            }

            ConsecutiveSearchErrors = 0;
            return edges;
        }
        // A cancelled run must stop, not be mistaken for "film not found". A timeout still counts as a failure.
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("JustWatch API error for '{Title}': {Message}", filmTitle, ex.Message);
            return SearchFailed();
        }
    }

    private List<TitleNode>? SearchFailed()
    {
        ConsecutiveSearchErrors++;
        SearchErrors++;
        return null;
    }

    /// <summary>
    /// Searches that failed in a row (an error, not "no match"). Each one looks like "not found", so a run can
    /// tell when JustWatch has stopped answering rather than save every film as missing.
    /// </summary>
    public int ConsecutiveSearchErrors { get; private set; }

    /// <summary>Searches sent to JustWatch so far (not counting films answered from the cache).</summary>
    public int Searches { get; private set; }

    /// <summary>Searches that failed so far, in a row or not: a high rate means JustWatch isn't answering properly.</summary>
    public int SearchErrors { get; private set; }

    /// <summary>Films looked up on JustWatch this time (not from the cache), and how many of those weren't found.</summary>
    public int Fetched { get; private set; }

    public int FetchedNotFound { get; private set; }

    /// <summary>A film with the same title and the same year: sure enough without asking for the IMDb ID.</summary>
    private static TitleDetail? ExactMatch(WatchlistFilm film, List<TitleNode> edges)
    {
        if (film.Year is null) return null;
        var normalised = NormaliseTitle(film.Title);
        return Movies(edges).FirstOrDefault(n =>
            NormaliseTitle(n.Content!.Title) == normalised && n.Content.OriginalReleaseYear == film.Year);
    }

    /// <summary>
    /// Films that might be it, best first: the same title within a year (or with a year unknown), then a longer
    /// title that starts with ours ("Dune" → "Dune: Part One") when the years agree. Further apart, "Nosferatu
    /// (1922)" isn't "Nosferatu (2024)" and "Alien (1979)" isn't "Alien: Romulus (2024)": only the IMDb ID can say.
    /// </summary>
    private static IEnumerable<TitleDetail> NearMatches(WatchlistFilm film, List<TitleNode> edges)
    {
        var normalised = NormaliseTitle(film.Title);
        var movies = Movies(edges).Where(n => YearsAgree(film.Year, n.Content!.OriginalReleaseYear)).ToList();
        return movies.Where(n => NormaliseTitle(n.Content!.Title) == normalised)
            .Concat(movies.Where(n =>
            {
                var theirs = NormaliseTitle(n.Content!.Title);
                return theirs.StartsWith(normalised + " ") || theirs.StartsWith(normalised + ":");
            }));
    }

    private static string NormaliseTitle(string title)
    {
        var s = title.ToLowerInvariant()
                     .Replace('–', '-')
                     .Replace('—', '-')
                     .Replace(":", "")
                     .Replace("'", "")
                     .Replace(",", "")
                     .Replace("!", "")
                     .Replace("?", "")
                     .Replace(".", "")
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
