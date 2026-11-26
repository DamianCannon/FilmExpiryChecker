using System.Text.Json;

namespace FilmExpiryChecker.Tests;

/// <summary>
/// A fake Letterboxd and JustWatch together: two real-shaped watchlist pages of 5 films for "example-user", one
/// service (Netflix, package 8), and every film found on Netflix.
/// </summary>
internal static class FakeSites
{
    public const string Watchlist = "https://letterboxd.com/example-user/watchlist/page/";
    public const string JustWatchApi = "https://apis.justwatch.com/graphql";

    /// <summary>The five films' Letterboxd IDs, in watchlist order.</summary>
    public static readonly string[] FilmIds = { "154091", "1284043", "61097", "44542", "50686" };

    /// <summary>Answers like the real sites. <paramref name="intercept"/> may answer a request first (or throw).</summary>
    public static FakeHttp Create(Func<HttpRequestMessage, string, CancellationToken, HttpResponseMessage?>? intercept = null) =>
        new((request, ct) =>
        {
            var url = request.RequestUri!.ToString();
            var body = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? string.Empty;

            if (intercept?.Invoke(request, body, ct) is { } answered) return answered;

            if (url == Watchlist + "1/") return FakeHttp.Html(Fixture.Read("watchlist-page-1.html"));
            if (url == Watchlist + "2/") return FakeHttp.Html(Fixture.Read("watchlist-page-2.html"));
            if (url == JustWatchApi && body.Contains("GetPackages"))
                return FakeHttp.Json("""{"data":{"packages":[{"id":"p8","packageId":8,"clearName":"Netflix","technicalName":"netflix","icon":""}]}}""");
            if (url == JustWatchApi) return FakeHttp.Json(OnNetflix(SearchedTitle(body)));
            return FakeHttp.NotFound();
        });

    public static string SearchedTitle(string body)
    {
        using var json = JsonDocument.Parse(body);
        return json.RootElement.GetProperty("variables").GetProperty("filter").GetProperty("searchQuery").GetString()!;
    }

    public static string OnNetflix(string title) => JsonSerializer.Serialize(new
    {
        data = new
        {
            popularTitles = new
            {
                edges = new[]
                {
                    new
                    {
                        node = new
                        {
                            id = "tm1",
                            objectType = "MOVIE",
                            content = new { title, originalReleaseYear = (int?)null, externalIds = new { imdbId = (string?)null } },
                            offers = new[] { new { monetizationType = "FLATRATE", package = new { id = "p8", packageId = 8 }, availableTo = (string?)null } },
                        },
                    },
                },
            },
        },
    });
}
