using FilmExpiryChecker.Letterboxd;

namespace FilmExpiryChecker.Tests;

/// <summary>Reading a watchlist from Letterboxd's pages, using pages trimmed from the real site.</summary>
public class LetterboxdScraperSpecs
{
    private const string Watchlist = "https://letterboxd.com/example-user/watchlist/page/";

    private static (LetterboxdScraper Scraper, FakeHttp Http) ScraperWith(Func<string, HttpResponseMessage> pages)
    {
        var http = new FakeHttp(request => pages(request.RequestUri!.ToString()));
        return (new LetterboxdScraper(new HttpClient(http), "example-user", time: InstantWaits.Instance), http);
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
    public async Task A_later_page_that_wont_load_fails_the_whole_watchlist_after_three_tries()
    {
        // Page 1 says there's a page 2, which then can't be had: silently stopping would leave films out (#45).
        var (scraper, http) = ScraperWith(url =>
            url == Watchlist + "1/" ? FakeHttp.Html(Fixture.Read("watchlist-page-1.html")) : FakeHttp.NotFound());

        var failed = await Assert.ThrowsAsync<RunFailedException>(() => scraper.GetWatchlistAsync());

        Assert.StartsWith("Couldn't read all of your Letterboxd watchlist: page 2 wouldn't load.", failed.Message);
        Assert.Equal(3, http.RequestedUrls.Count(u => u == Watchlist + "2/"));
    }

    [Fact]
    public async Task A_page_that_fails_once_is_tried_again_and_the_watchlist_is_complete()
    {
        var tries = 0;
        var (scraper, _) = ScraperWith(url =>
            url == Watchlist + "2/" && ++tries == 1 ? throw new HttpRequestException("Connection reset.") : TwoPages(url));

        Assert.Equal(5, (await scraper.GetWatchlistAsync()).Count);
    }

    [Fact]
    public async Task Not_reaching_Letterboxd_at_all_is_an_error_not_an_empty_watchlist()
    {
        var (scraper, _) = ScraperWith(_ => throw new HttpRequestException("No such host is known."));

        var failed = await Assert.ThrowsAsync<RunFailedException>(() => scraper.GetWatchlistAsync());

        Assert.Contains("Couldn't reach Letterboxd", failed.Message);
    }

    [Fact]
    public async Task An_unknown_username_answered_with_forbidden_is_an_empty_watchlist_not_an_outage()
    {
        // What letterboxd.com really answers for a user that doesn't exist (seen 26 Nov 2026).
        var (scraper, _) = ScraperWith(_ => new HttpResponseMessage(System.Net.HttpStatusCode.Forbidden));

        Assert.Empty(await scraper.GetWatchlistAsync());
    }

    [Fact]
    public async Task Letterboxd_answering_with_a_server_error_says_so()
    {
        var (scraper, _) = ScraperWith(_ => new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable));

        var failed = await Assert.ThrowsAsync<RunFailedException>(() => scraper.GetWatchlistAsync());

        Assert.Equal("Letterboxd isn't working properly right now (it answered 503). Try again later.", failed.Message);
    }

    [Fact]
    public async Task Losing_the_connection_after_page_one_fails_the_watchlist()
    {
        var (scraper, _) = ScraperWith(url =>
            url == Watchlist + "1/" ? FakeHttp.Html(Fixture.Read("watchlist-page-1.html")) : throw new HttpRequestException("Connection reset."));

        await Assert.ThrowsAsync<RunFailedException>(() => scraper.GetWatchlistAsync());
    }

    [Fact]
    public async Task A_film_page_that_loads_without_an_IMDb_link_is_an_answer()
    {
        var (scraper, _) = ScraperWith(_ => FakeHttp.Html("<p>No links here</p>"));

        Assert.Equal((true, (string?)null), await scraper.TryFetchImdbIdAsync("some-film"));
    }

    [Fact]
    public async Task A_film_page_that_wont_load_is_not_an_answer()
    {
        var (scraper, _) = ScraperWith(_ => throw new HttpRequestException("Connection reset."));

        Assert.Equal((false, (string?)null), await scraper.TryFetchImdbIdAsync("some-film"));
    }

    [Fact]
    public async Task A_missing_watchlist_gives_no_films()
    {
        var (scraper, _) = ScraperWith(_ => FakeHttp.NotFound());

        Assert.Empty(await scraper.GetWatchlistAsync());
    }

    [Fact]
    public async Task Checking_a_public_watchlist_finds_it()
    {
        var (scraper, http) = ScraperWith(TwoPages);

        Assert.Equal(WatchlistCheck.Found, await scraper.CheckWatchlistAsync());
        Assert.Equal(new[] { Watchlist + "1/" }, http.RequestedUrls);
    }

    [Fact]
    public async Task Checking_an_unknown_user_says_so()
    {
        var (scraper, _) = ScraperWith(_ => new HttpResponseMessage(System.Net.HttpStatusCode.Forbidden));

        Assert.Equal(WatchlistCheck.NoSuchUser, await scraper.CheckWatchlistAsync());
    }

    [Fact]
    public async Task Checking_a_watchlist_with_no_films_showing_says_empty_or_private()
    {
        var (scraper, _) = ScraperWith(_ => FakeHttp.Html("<html><body><p>This watchlist is private.</p></body></html>"));

        Assert.Equal(WatchlistCheck.EmptyOrPrivate, await scraper.CheckWatchlistAsync());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Checking_when_Letterboxd_cant_be_reached_or_errs_says_unreachable(bool offline)
    {
        var (scraper, _) = ScraperWith(_ => offline
            ? throw new HttpRequestException("No such host is known.")
            : new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable));

        Assert.Equal(WatchlistCheck.Unreachable, await scraper.CheckWatchlistAsync());
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
