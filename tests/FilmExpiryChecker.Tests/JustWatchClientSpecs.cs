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
        Typed(results.Select(r => (r.Title, r.Year, r.PackageId, r.Imdb, "MOVIE")).ToArray());

    /// <summary>As <see cref="Results"/>, with each result's JustWatch type ("MOVIE", "SHOW").</summary>
    private static string Typed(params (string Title, int Year, int PackageId, string? Imdb, string Type)[] results) =>
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
                            objectType = r.Type,
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
    public async Task With_no_exact_year_the_same_title_a_year_out_is_taken()
    {
        var (client, _) = ClientAnswering(Results(("Something Else", 1999, 1, null), ("Cloud", 2024, 2, null), ("Cloud", 2000, 3, null)));

        Assert.Equal(3, MatchedPackage(await client.GetOffersAsync(Film("Cloud", 1999))));
    }

    [Fact]
    public async Task The_same_title_from_another_era_isnt_taken_without_its_IMDb_ID()
    {
        var (client, _) = ClientAnswering(Results(("Nosferatu", 2024, 2, "tt5040012")), (_, _) => Task.FromResult<string?>("tt0013442"));

        Assert.Null(await client.GetOffersAsync(Film("Nosferatu", 1922)));
    }

    [Fact]
    public async Task The_same_title_from_another_era_is_taken_when_its_IMDb_ID_agrees()
    {
        var (client, _) = ClientAnswering(Results(("Nosferatu", 2024, 2, "tt5040012"), ("Nosferatu", 1979, 3, "tt0079641")),
            (_, _) => Task.FromResult<string?>("tt0079641"));

        Assert.Equal(3, MatchedPackage(await client.GetOffersAsync(Film("Nosferatu", 1922))));
    }

    [Fact]
    public async Task A_TV_series_with_the_same_title_isnt_the_film()
    {
        var (client, _) = ClientAnswering(Typed(("Fargo", 1996, 1, null, "SHOW"), ("Fargo", 1996, 2, null, "MOVIE")));

        Assert.Equal(2, MatchedPackage(await client.GetOffersAsync(Film("Fargo", 1996))));
    }

    [Fact]
    public async Task A_series_with_the_films_own_IMDb_ID_is_the_film()
    {
        // Letterboxd lists some miniseries as films; JustWatch calls them shows.
        var (client, _) = ClientAnswering(Typed(("O.J.: Made in America", 2016, 4, "tt5275892", "SHOW")),
            (_, _) => Task.FromResult<string?>("tt5275892"));

        Assert.Equal(4, MatchedPackage(await client.GetOffersAsync(Film("O.J.: Made in America", 2016))));
    }

    [Fact]
    public async Task A_title_a_year_out_whose_IMDb_ID_is_another_films_isnt_taken()
    {
        var (client, _) = ClientAnswering(Results(("Beast", 2022, 2, "tt13207736")), (_, _) => Task.FromResult<string?>("tt13028258"));

        Assert.Null(await client.GetOffersAsync(Film("The Beast", 2023)));
    }

    [Fact]
    public async Task A_longer_title_whose_IMDb_ID_is_another_films_isnt_taken()
    {
        var (client, _) = ClientAnswering(
            Typed(("The Beatles: Get Back - The Rooftop Concert", 2022, 3, "tt16378298", "MOVIE"), ("The Beatles: Get Back", 2021, 4, "tt9735318", "SHOW")),
            (_, _) => Task.FromResult<string?>("tt9735318"));

        Assert.Equal(4, MatchedPackage(await client.GetOffersAsync(Film("The Beatles: Get Back", 2021))));
    }

    /// <summary>As <see cref="Typed"/>, but the first result has no offers.</summary>
    private static string FirstWithoutOffers(params (string Title, int Year, int PackageId, string? Imdb, string Type)[] results)
    {
        var json = System.Text.Json.Nodes.JsonNode.Parse(Typed(results))!;
        json["data"]!["popularTitles"]!["edges"]![0]!["node"]!["offers"] = new System.Text.Json.Nodes.JsonArray();
        return json.ToJsonString();
    }

    [Fact]
    public async Task A_film_with_no_offers_takes_them_from_its_series_twin_of_the_same_year()
    {
        var (client, _) = ClientAnswering(
            FirstWithoutOffers(("O.J.: Made in America", 2016, 1, "tt5275892", "MOVIE"), ("O.J.: Made in America", 2016, 4, "tt13320728", "SHOW")),
            (_, _) => Task.FromResult<string?>("tt5275892"));

        var found = await client.LookUpAsync(Film("O.J.: Made in America", 2016));

        Assert.Equal(4, MatchedPackage(found.Offers));
        Assert.Equal("tm4", found.Match!.JustWatchId);
    }

    [Fact]
    public async Task A_film_with_no_offers_takes_them_from_a_twin_listing_differing_only_in_punctuation()
    {
        var (client, _) = ClientAnswering(
            FirstWithoutOffers(("Time Travel Is Dangerous", 2025, 1, "tt1111111", "MOVIE"), ("Time Travel Is Dangerous!", 2025, 6, null, "MOVIE")),
            (_, _) => Task.FromResult<string?>("tt1111111"));

        Assert.Equal(6, MatchedPackage(await client.GetOffersAsync(Film("Time Travel Is Dangerous!", 2024))));
    }

    [Fact]
    public async Task A_film_with_no_offers_doesnt_take_a_series_from_another_year()
    {
        var (client, _) = ClientAnswering(
            FirstWithoutOffers(("After Life", 1998, 1, null, "MOVIE"), ("After Life", 2019, 4, null, "SHOW")));

        Assert.Empty((await client.GetOffersAsync(Film("After Life", 1998)))!);
    }

    [Fact]
    public async Task Only_a_TV_series_with_the_title_finds_nothing()
    {
        var (client, _) = ClientAnswering(Typed(("Shogun", 1980, 1, null, "SHOW")));

        Assert.Null(await client.GetOffersAsync(Film("Shogun", 1980)));
    }

    [Fact]
    public async Task A_longer_title_from_another_year_isnt_a_match()
    {
        var (client, _) = ClientAnswering(Results(("Alien: Romulus", 2024, 5, null)));

        Assert.Null(await client.GetOffersAsync(Film("Alien", 1979)));
    }

    [Fact]
    public async Task The_JustWatch_title_matched_is_given_and_kept_in_the_cache()
    {
        var (client, _) = ClientAnswering(Results(("High and Low", 1963, 2, null)));

        var first = await client.LookUpAsync(Film("High and Low", 1963));
        var again = await client.LookUpAsync(Film("High and Low", 1963));

        Assert.Equal(new TitleMatch("tm2", "High and Low", 1963), first.Match);
        Assert.Equal(first.Match, again.Match);
        Assert.Equal(1, client.Fetched);
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
