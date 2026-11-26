using System.Text.Json;
using FilmExpiryChecker.JustWatch;
using FilmExpiryChecker.Letterboxd;

namespace FilmExpiryChecker.Tests;

/// <summary>Which JustWatch search result is taken as the watchlist film, and what is cached.</summary>
public sealed class JustWatchClientSpecs : IDisposable
{
    private readonly TempFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    private (JustWatchClient Client, FakeHttp Http) ClientAnswering(
        string json, Func<string, CancellationToken, Task<string?>>? imdbResolver = null)
    {
        var http = new FakeHttp(_ => FakeHttp.Json(json));
        var cache = new JustWatchCache(_temp.File("cache.json"), expiryDays: 7);
        return (new JustWatchClient(new HttpClient(http), "GB", cache, imdbResolver), http);
    }

    private static WatchlistFilm Film(string title, int? year, string slug = "a-film") =>
        new() { Title = title, Year = year, Slug = slug, FilmId = "42" };

    /// <summary>A search response shaped like JustWatch's GraphQL; each result's one offer is on its own package.</summary>
    private static string Results(params (string Title, int Year, int PackageId, string? Imdb)[] results) =>
        JsonSerializer.Serialize(new
        {
            data = new
            {
                popularTitles = new
                {
                    edges = results.Select(r => new
                    {
                        node = new
                        {
                            id = "tm" + r.PackageId,
                            objectType = "MOVIE",
                            content = new
                            {
                                title = r.Title,
                                originalReleaseYear = r.Year,
                                externalIds = new { imdbId = r.Imdb },
                            },
                            offers = new[]
                            {
                                new
                                {
                                    monetizationType = "FLATRATE",
                                    package = new { id = "p" + r.PackageId, packageId = r.PackageId },
                                    availableTo = (string?)null,
                                },
                            },
                        },
                    }),
                },
            },
        });

    private static int? MatchedPackage(List<Offer>? offers) => offers?.Single().PackageId;

    // ── A real response ─────────────────────────────────────────────────────────

    [Fact]
    public async Task A_recorded_search_response_gives_the_films_offers_with_their_end_dates()
    {
        var (client, _) = ClientAnswering(Fixture.Read("justwatch-search-high-and-low.json"));

        var offers = await client.GetOffersAsync(Film("High and Low", 1963));

        Assert.NotNull(offers);
        Assert.Equal(12, offers.Count);
        Assert.Contains(offers, o => o.PackageId == 224 && o.ExpiryDate == new DateTime(2027, 2, 7, 0, 0, 0, DateTimeKind.Utc));
    }

    // ── Choosing the match ──────────────────────────────────────────────────────

    [Fact]
    public async Task Title_and_year_beat_an_earlier_result_with_the_same_title()
    {
        var (client, _) = ClientAnswering(Results(("High and Low", 2025, 1, null), ("High and Low", 1963, 2, null)));

        Assert.Equal(2, MatchedPackage(await client.GetOffersAsync(Film("High and Low", 1963))));
    }

    [Fact]
    public async Task With_no_year_match_the_first_title_match_is_taken()
    {
        var (client, _) = ClientAnswering(Results(("Something Else", 1999, 1, null), ("Cloud", 2024, 2, null), ("Cloud", 2020, 3, null)));

        Assert.Equal(2, MatchedPackage(await client.GetOffersAsync(Film("Cloud", 1999))));
    }

    [Theory]
    [InlineData("The Prize of Peril", "Prize of Peril")]
    [InlineData("A Ghost Story", "Ghost Story")]
    [InlineData("Predator: Killer of Killers", "Predator Killer of Killers")]
    [InlineData("It's Never Over, Jeff Buckley", "Its Never Over Jeff Buckley")]
    [InlineData("HIGH AND LOW", "High and Low")]
    public async Task Titles_match_ignoring_case_a_leading_article_and_punctuation(string ours, string theirs)
    {
        var (client, _) = ClientAnswering(Results((theirs, 2000, 7, null)));

        Assert.Equal(7, MatchedPackage(await client.GetOffersAsync(Film(ours, 2000))));
    }

    [Fact]
    public async Task A_longer_JustWatch_title_that_starts_with_ours_is_a_match()
    {
        var (client, _) = ClientAnswering(Results(("Dune: Part One", 2021, 5, null)));

        Assert.Equal(5, MatchedPackage(await client.GetOffersAsync(Film("Dune", 2021))));
    }

    [Fact]
    public async Task A_title_that_only_shares_a_first_word_is_not_a_match()
    {
        var (client, _) = ClientAnswering(Results(("Duneland", 2021, 5, null)));

        Assert.Null(await client.GetOffersAsync(Film("Dune", 2021)));
    }

    [Fact]
    public async Task When_no_title_matches_the_IMDb_ID_from_Letterboxd_finds_the_film()
    {
        var (client, _) = ClientAnswering(
            Results(("Wrong Film", 2025, 1, "tt0000001"), ("Sore – Wife from the Future", 2025, 2, "tt1234567")),
            (_, _) => Task.FromResult<string?>("tt1234567"));

        Assert.Equal(2, MatchedPackage(await client.GetOffersAsync(Film("Sore: A Wife from the Future", 2025))));
    }

    [Fact]
    public async Task An_IMDb_ID_that_matches_no_result_finds_nothing()
    {
        var (client, _) = ClientAnswering(
            Results(("Wrong Film", 2025, 1, "tt0000001")),
            (_, _) => Task.FromResult<string?>("tt9999999"));

        Assert.Null(await client.GetOffersAsync(Film("Sore: A Wife from the Future", 2025)));
    }

    [Fact]
    public async Task A_matched_film_with_no_offers_gives_an_empty_list_not_null()
    {
        const string json = """
            {"data":{"popularTitles":{"edges":[{"node":{"id":"tm1","objectType":"MOVIE",
              "content":{"title":"Cloud","originalReleaseYear":2024,"externalIds":{"imdbId":null}},
              "offers":[]}}]}}}
            """;
        var (client, _) = ClientAnswering(json);

        var offers = await client.GetOffersAsync(Film("Cloud", 2024));

        Assert.NotNull(offers);
        Assert.Empty(offers);
    }

    // ── Caching ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_found_film_is_answered_from_the_cache_the_second_time()
    {
        var (client, http) = ClientAnswering(Results(("Cloud", 2024, 3, null)));

        await client.GetOffersAsync(Film("Cloud", 2024));
        var second = await client.GetOffersAsync(Film("Cloud", 2024));

        Assert.Single(http.RequestedUrls);
        Assert.Equal(3, MatchedPackage(second));
    }

    [Fact]
    public async Task A_film_not_found_is_not_cached_so_it_is_searched_again()
    {
        var (client, http) = ClientAnswering(Results(("Something Else", 2024, 3, null)));

        await client.GetOffersAsync(Film("Cloud", 2024));
        await client.GetOffersAsync(Film("Cloud", 2024));

        Assert.Equal(2, http.RequestedUrls.Count);
    }

    [Fact]
    public async Task The_search_asks_for_the_films_title_in_the_region()
    {
        var (client, http) = ClientAnswering(Results(("Cloud", 2024, 3, null)));

        await client.GetOffersAsync(Film("Cloud", 2024));

        using var body = JsonDocument.Parse(http.RequestBodies.Single());
        var variables = body.RootElement.GetProperty("variables");
        Assert.Equal("Cloud", variables.GetProperty("filter").GetProperty("searchQuery").GetString());
        Assert.Equal("GB", variables.GetProperty("country").GetString());
    }
}
