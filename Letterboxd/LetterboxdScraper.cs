using HtmlAgilityPack;

namespace FilmExpiryChecker.Letterboxd;

public class LetterboxdScraper
{
    private readonly HttpClient _http;
    private readonly string _username;

    // Letterboxd shows 100 (20 rows * 5 films) films per watchlist page
    private const int FilmsPerPage = 100;

    public LetterboxdScraper(HttpClient http, string username)
    {
        _http = http;
        _username = username;
    }

    public async Task<List<WatchlistFilm>> GetWatchlistAsync(CancellationToken ct = default)
    {
        var films = new List<WatchlistFilm>();
        var page = 1;

        Console.WriteLine($"Fetching Letterboxd watchlist for {_username}...");

        while (true)
        {
            var url = $"https://letterboxd.com/{_username}/watchlist/page/{page}/";
            Console.Write($"  Page {page}... ");

            var html = await FetchPageAsync(url, ct);
            if (html is null)
            {
                Console.WriteLine("failed to fetch, stopping.");
                break;
            }

            var pageFilms = ParseFilmsFromPage(html);

            if (pageFilms.Count == 0)
            {
                Console.WriteLine("no films found, done.");
                break;
            }

            Console.WriteLine($"{pageFilms.Count} films.");
            films.AddRange(pageFilms);

            // Check for a next page link rather than relying on film count
            if (!HasNextPage(html))
                break;

            page++;
            await Task.Delay(300, ct);
        }

        Console.WriteLine($"Total films in watchlist: {films.Count}");
        return films;
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

    private async Task<string?> FetchPageAsync(string url, CancellationToken ct)
    {
        try
        {
            var response = await _http.GetAsync(url, ct);

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                return null;

            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync(ct);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error fetching {url}: {ex.Message}");
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