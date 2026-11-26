using FilmExpiryChecker.Letterboxd;

namespace FilmExpiryChecker.Tests;

/// <summary>Reading a watchlist from Letterboxd's pages, using pages trimmed from the real site.</summary>
public class LetterboxdScraperSpecs
{
    private const string Watchlist = "https://letterboxd.com/example-user/watchlist/page/";

    private static (LetterboxdScraper Scraper, FakeHttp Http) ScraperWith(Func<string, HttpResponseMessage> pages)
    {
        var http = new FakeHttp(request => pages(request.RequestUri!.ToString()));
        return (new LetterboxdScraper(new HttpClient(http), "example-user"), http);
    }

    private static HttpResponseMessage TwoPages(string url) => url switch
    {
        Watchlist + "1/" => FakeHttp.Html(Fixture.Read("watchlist-page-1.html")),
        Watchlist + "2/" => FakeHttp.Html(Fixture.Read("watchlist-page-2.html")),
        _ => FakeHttp.NotFound(),
    };

    [Fact]
    public async Task Every_film_on_every_page_is_read_in_order()
    {
        var (scraper, _) = ScraperWith(TwoPages);

        var films = await scraper.GetWatchlistAsync();

        Assert.Equal(
            new[] { "kisapmata", "its-never-over-jeff-buckley", "socrates", "high-and-low", "away-from-her" },
            films.Select(f => f.Slug));
    }

    [Fact]
    public async Task Each_film_has_its_title_year_slug_and_Letterboxd_ID()
    {
        var (scraper, _) = ScraperWith(TwoPages);

        var film = (await scraper.GetWatchlistAsync()).Single(f => f.Slug == "high-and-low");

        Assert.Equal("High and Low", film.Title);
        Assert.Equal(1963, film.Year);
        Assert.Equal("44542", film.FilmId);
    }

    [Fact]
    public async Task HTML_entities_in_a_title_are_decoded()
    {
        var (scraper, _) = ScraperWith(TwoPages);

        var film = (await scraper.GetWatchlistAsync()).Single(f => f.Slug == "its-never-over-jeff-buckley");

        Assert.Equal("It's Never Over, Jeff Buckley", film.Title);
        Assert.Equal(2025, film.Year);
    }

    [Fact]
    public async Task A_film_with_no_year_keeps_its_whole_name_as_the_title()
    {
        var (scraper, _) = ScraperWith(TwoPages);

        var film = (await scraper.GetWatchlistAsync()).Single(f => f.Slug == "socrates");

        Assert.Equal("Socrates", film.Title);
        Assert.Null(film.Year);
        Assert.Equal("61097", film.FilmId);
    }

    [Fact]
    public async Task It_stops_at_the_page_without_a_next_link()
    {
        var (scraper, http) = ScraperWith(TwoPages);

        await scraper.GetWatchlistAsync();

        Assert.Equal(new[] { Watchlist + "1/", Watchlist + "2/" }, http.RequestedUrls);
    }

    [Fact]
    public async Task A_page_that_cannot_be_fetched_ends_the_list_with_the_films_so_far()
    {
        var (scraper, _) = ScraperWith(url =>
            url == Watchlist + "1/" ? FakeHttp.Html(Fixture.Read("watchlist-page-1.html")) : FakeHttp.NotFound());

        var films = await scraper.GetWatchlistAsync();

        Assert.Equal(3, films.Count);
    }

    [Fact]
    public async Task Not_reaching_Letterboxd_at_all_is_an_error_not_an_empty_watchlist()
    {
        var (scraper, _) = ScraperWith(_ => throw new HttpRequestException("No such host is known."));

        var failed = await Assert.ThrowsAsync<RunFailedException>(() => scraper.GetWatchlistAsync());

        Assert.Contains("Couldn't reach Letterboxd", failed.Message);
    }

    [Fact]
    public async Task Losing_the_connection_after_page_one_keeps_the_films_so_far()
    {
        var (scraper, _) = ScraperWith(url =>
            url == Watchlist + "1/" ? FakeHttp.Html(Fixture.Read("watchlist-page-1.html")) : throw new HttpRequestException("Connection reset."));

        Assert.Equal(3, (await scraper.GetWatchlistAsync()).Count);
    }

    [Fact]
    public async Task A_missing_watchlist_gives_no_films()
    {
        var (scraper, _) = ScraperWith(_ => FakeHttp.NotFound());

        Assert.Empty(await scraper.GetWatchlistAsync());
    }

    [Fact]
    public async Task The_IMDb_ID_is_read_from_the_films_page()
    {
        var (scraper, http) = ScraperWith(_ => FakeHttp.Html(
            """<p class="text-link"><a href="http://www.imdb.com/title/tt0057565/maindetails" data-track-action="IMDb">IMDb</a></p>"""));

        Assert.Equal("tt0057565", await scraper.FetchImdbIdAsync("high-and-low"));
        Assert.Equal("https://letterboxd.com/film/high-and-low/", http.RequestedUrls.Single());
    }

    [Fact]
    public async Task A_films_page_with_no_IMDb_link_gives_no_ID()
    {
        var (scraper, _) = ScraperWith(_ => FakeHttp.Html("<p>No links here</p>"));

        Assert.Null(await scraper.FetchImdbIdAsync("high-and-low"));
    }
}
