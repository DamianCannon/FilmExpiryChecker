using HtmlAgilityPack;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FilmExpiryChecker.Letterboxd;

/// <summary>One watchlist page read: its number, the films on it, and the films found so far.</summary>
public readonly record struct WatchlistPage(int Page, int FilmsOnPage, int FilmsSoFar);

/// <summary>What a quick look at a user's watchlist found, for checking a username before it's saved.</summary>
public enum WatchlistCheck
{
    /// <summary>A public watchlist with films on it.</summary>
    Found,

    /// <summary>No such user (Letterboxd answers 403 or 404).</summary>
    NoSuchUser,

    /// <summary>The user exists but no films show: the watchlist is private or empty.</summary>
    EmptyOrPrivate,

    /// <summary>Letterboxd couldn't be reached, or answered with an error.</summary>
    Unreachable,
}

public class LetterboxdScraper
{
    private readonly HttpClient _http;
    private readonly string _username;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;

    public LetterboxdScraper(HttpClient http, string username, ILogger? logger = null, TimeProvider? time = null)
    {
        _http = http;
        _username = username;
        _logger = logger ?? NullLogger.Instance;
        _time = time ?? TimeProvider.System;
    }

    public async Task<List<WatchlistFilm>> GetWatchlistAsync(
        IProgress<WatchlistPage>? progress = null, CancellationToken ct = default)
    {
        var films = new List<WatchlistFilm>();
        var page = 1;

        while (true)
        {
            var url = $"https://letterboxd.com/{_username}/watchlist/page/{page}/";

            // Failing to reach Letterboxd at all is not the same as an empty watchlist, so page 1 failing is an error.
            var html = await FetchPageAsync(url, ct, failLoudly: page == 1);
            if (html is null)
            {
                _logger.LogInformation("Watchlist page {Page} could not be fetched; stopping there.", page);
                break;
            }

            var pageFilms = ParseFilmsFromPage(html);

            if (pageFilms.Count == 0)
            {
                _logger.LogInformation("Watchlist page {Page} has no films; done.", page);
                break;
            }

            films.AddRange(pageFilms);
            progress?.Report(new WatchlistPage(page, pageFilms.Count, films.Count));

            // Check for a next page link rather than relying on film count
            if (!HasNextPage(html))
                break;

            page++;
            await Task.Delay(TimeSpan.FromMilliseconds(300), _time, ct);
        }

        return films;
    }

    /// <summary>Looks at the first page of the watchlist only, to check the username is right before saving it.</summary>
    public async Task<WatchlistCheck> CheckWatchlistAsync(CancellationToken ct = default)
    {
        HttpResponseMessage response;
        try
        {
            response = await _http.GetAsync($"https://letterboxd.com/{_username}/watchlist/page/1/", ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("Checking {User}'s watchlist: {Message}", _username, ex.Message);
            return WatchlistCheck.Unreachable;
        }

        if (response.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Forbidden)
            return WatchlistCheck.NoSuchUser;
        if (!response.IsSuccessStatusCode)
            return WatchlistCheck.Unreachable;

        var html = await response.Content.ReadAsStringAsync(ct);
        return ParseFilmsFromPage(html).Count > 0 ? WatchlistCheck.Found : WatchlistCheck.EmptyOrPrivate;
    }

    private static bool HasNextPage(string html)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        // Letterboxd pagination has a <a class="next" href="..."> link when there are more pages
        var nextLink = doc.DocumentNode
            .SelectSingleNode("//a[contains(@class,'next')]");

        return nextLink is not null;
    }
    
    public async Task<string?> FetchImdbIdAsync(string slug, CancellationToken ct = default)
    {
        var html = await FetchPageAsync($"https://letterboxd.com/film/{slug}/", ct);
        if (html is null) return null;
        var match = System.Text.RegularExpressions.Regex.Match(html, @"imdb\.com/title/(tt\d+)");
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>The page, or null if it doesn't exist (404) or, unless <paramref name="failLoudly"/>, couldn't be fetched.</summary>
    private async Task<string?> FetchPageAsync(string url, CancellationToken ct, bool failLoudly = false)
    {
        HttpResponseMessage response;
        try
        {
            response = await _http.GetAsync(url, ct);
        }
        // A cancelled run must stop, not look like the end of the watchlist. A timeout still counts as a failure.
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("Error fetching {Url}: {Message}", url, ex.Message);
            if (failLoudly)
                throw new RunFailedException("Couldn't reach Letterboxd. Check your internet connection and try again.", ex);
            return null;
        }

        // Letterboxd answers 403 (not 404) for a username that doesn't exist; both mean "no such page".
        if (response.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Forbidden)
        {
            _logger.LogInformation("{Url} answered {Status}.", url, (int)response.StatusCode);
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Error fetching {Url}: {Status}", url, (int)response.StatusCode);
            if (failLoudly)
                throw new RunFailedException(
                    $"Letterboxd isn't working properly right now (it answered {(int)response.StatusCode}). Try again later.");
            return null;
        }

        try
        {
            return await response.Content.ReadAsStringAsync(ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("Error fetching {Url}: {Message}", url, ex.Message);
            if (failLoudly)
                throw new RunFailedException("Couldn't reach Letterboxd. Check your internet connection and try again.", ex);
            return null;
        }
    }

    private static List<WatchlistFilm> ParseFilmsFromPage(string html)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var films = new List<WatchlistFilm>();

        // Each film is in <li class="griditem">
        // Inside is a <div class="react-component" data-component-class="LazyPoster" ...>
        var divs = doc.DocumentNode
            .SelectNodes("//li[contains(@class,'griditem')]//div[@data-component-class='LazyPoster']");

        if (divs is null)
            return films;

        foreach (var div in divs)
        {
            var slug = div.GetAttributeValue("data-item-slug", "");
            var itemName = div.GetAttributeValue("data-item-name", "");

            // data-postered-identifier is a JSON string like:
            // {"lid":"2aU2","uid":"film:51857","type":"film","typeName":"film"}
            var posteredJson = HtmlEntity.DeEntitize(
                div.GetAttributeValue("data-postered-identifier", ""));

            if (string.IsNullOrEmpty(slug) || string.IsNullOrEmpty(itemName))
                continue;

            // Parse title and year from "Trouble in Paradise (1932)"
            var (title, year) = ParseTitleAndYear(itemName);

            // Extract numeric film ID from uid "film:51857"
            var filmId = ExtractFilmId(posteredJson);

            if (string.IsNullOrEmpty(filmId))
                continue;

            films.Add(new WatchlistFilm
            {
                Title = HtmlEntity.DeEntitize(title),
                Year = year,
                Slug = slug,
                FilmId = filmId
            });
        }

        return films;
    }

    private static (string title, int? year) ParseTitleAndYear(string itemName)
    {
        // Format is "Title (Year)" e.g. "Trouble in Paradise (1932)"
        // Some films have no year e.g. "Untitled Film"
        var match = System.Text.RegularExpressions.Regex.Match(
            itemName, @"^(.+?)\s+\((\d{4})\)$");

        if (match.Success)
            return (match.Groups[1].Value, int.Parse(match.Groups[2].Value));

        return (itemName, null);
    }

    private static string ExtractFilmId(string posteredJson)
    {
        // uid is "film:51857" — we want the numeric part
        if (string.IsNullOrEmpty(posteredJson))
            return string.Empty;

        var match = System.Text.RegularExpressions.Regex.Match(
            posteredJson, @"""uid""\s*:\s*""film:(\d+)""");

        return match.Success ? match.Groups[1].Value : string.Empty;
    }
}