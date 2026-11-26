using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using FilmExpiryChecker.Config;
using FilmExpiryChecker.JustWatch;
using FilmExpiryChecker.Letterboxd;
using FilmExpiryChecker.Reporting;
using FilmExpiryChecker.Running;
using FilmExpiryChecker.Storage;

namespace FilmExpiryChecker.Tests;

/// <summary>
/// A whole check, against a fake Letterboxd (two real-shaped watchlist pages, 5 films) and a fake JustWatch
/// that offers every film on Netflix (package 8), which is one of my services.
/// </summary>
public sealed class CheckRunnerSpecs : IDisposable
{
    private const string Watchlist = "https://letterboxd.com/example-user/watchlist/page/";
    private const string JustWatchApi = "https://apis.justwatch.com/graphql";

    // The five films on the fixture pages, in order: slug → Letterboxd ID.
    private static readonly string[] FilmIds = { "154091", "1284043", "61097", "44542", "50686" };

    private readonly TempFolder _temp = new();
    private readonly CancellationTokenSource _cancel = new();

    private DataFolder Data => new(_temp.Path);

    public void Dispose()
    {
        _cancel.Dispose();
        _temp.Dispose();
    }

    private AppSettings Settings() => new()
    {
        Letterboxd = { Username = "example-user" },
        JustWatch = { Region = "GB", MyServices = { "8" }, ExpiringSoonDays = 60 },
        Cache = { ExpiryDays = 7 },
    };

    /// <summary>Answers like the real sites. <paramref name="intercept"/> may answer a request first (or throw).</summary>
    private FakeHttp Sites(Func<HttpRequestMessage, string, HttpResponseMessage?>? intercept = null) => new(request =>
    {
        var url = request.RequestUri!.ToString();
        var body = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? string.Empty;

        if (intercept?.Invoke(request, body) is { } answered) return answered;

        if (url == Watchlist + "1/") return FakeHttp.Html(Fixture.Read("watchlist-page-1.html"));
        if (url == Watchlist + "2/") return FakeHttp.Html(Fixture.Read("watchlist-page-2.html"));
        if (url == JustWatchApi && body.Contains("GetPackages"))
            return FakeHttp.Json("""{"data":{"packages":[{"id":"p8","packageId":8,"clearName":"Netflix","technicalName":"netflix","icon":""}]}}""");
        if (url == JustWatchApi) return FakeHttp.Json(OnNetflix(SearchedTitle(body)));
        return FakeHttp.NotFound();
    });

    private static string SearchedTitle(string body)
    {
        using var json = JsonDocument.Parse(body);
        return json.RootElement.GetProperty("variables").GetProperty("filter").GetProperty("searchQuery").GetString()!;
    }

    private static string OnNetflix(string title) => JsonSerializer.Serialize(new
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

    private static List<RunProgress> Steps(RecordedProgress<RunProgress> progress, RunStep step) =>
        progress.Reports.Where(p => p.Step == step).ToList();

    // ── A full run ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_run_reports_its_steps_in_order()
    {
        var progress = new RecordedProgress<RunProgress>();

        await new CheckRunner(new HttpClient(Sites()), Data).RunAsync(Settings(), progress);

        var steps = progress.Reports.Select(p => p.Step).Distinct();
        Assert.Equal(new[] { RunStep.ReadingWatchlist, RunStep.FetchingServices, RunStep.CheckingFilms, RunStep.Saving }, steps);
    }

    [Fact]
    public async Task Reading_the_watchlist_reports_each_page_and_the_films_found_so_far()
    {
        var progress = new RecordedProgress<RunProgress>();

        await new CheckRunner(new HttpClient(Sites()), Data).RunAsync(Settings(), progress);

        var pages = Steps(progress, RunStep.ReadingWatchlist);
        Assert.Equal(new[] { 0, 1, 2 }, pages.Select(p => p.Done));
        Assert.Equal(new[] { 0, 3, 5 }, pages.Select(p => p.Films));
    }

    [Fact]
    public async Task Checking_films_counts_up_to_the_total()
    {
        var progress = new RecordedProgress<RunProgress>();

        await new CheckRunner(new HttpClient(Sites()), Data).RunAsync(Settings(), progress);

        var checking = Steps(progress, RunStep.CheckingFilms);
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5 }, checking.Select(p => p.Done));
        Assert.All(checking, p => Assert.Equal(5, p.Total));
    }

    [Fact]
    public async Task A_completed_run_returns_every_film_grouped()
    {
        var result = await new CheckRunner(new HttpClient(Sites()), Data).RunAsync(Settings());

        Assert.Equal(RunOutcome.Completed, result.Outcome);
        Assert.Equal(5, result.Films.Count);
        Assert.All(result.Films, f => Assert.Equal(FilmCategory.AvailableMyServices, f.Category));
        Assert.Equal(1, result.ServiceCount);
    }

    [Fact]
    public async Task A_completed_run_saves_what_it_fetched_to_the_cache()
    {
        await new CheckRunner(new HttpClient(Sites()), Data).RunAsync(Settings());

        var cache = new JustWatchCache(Data.JustWatchCacheFile, expiryDays: 7);
        Assert.All(FilmIds, id => Assert.True(cache.TryGet(id, out _), $"film {id} not cached"));
    }

    [Fact]
    public async Task An_empty_watchlist_is_its_own_outcome_and_asks_JustWatch_nothing()
    {
        var http = Sites((request, _) =>
            request.RequestUri!.Host == "letterboxd.com" ? FakeHttp.NotFound() : null);

        var result = await new CheckRunner(new HttpClient(http), Data).RunAsync(Settings());

        Assert.Equal(RunOutcome.EmptyWatchlist, result.Outcome);
        Assert.DoesNotContain(http.RequestedUrls, u => u.StartsWith(JustWatchApi));
    }

    // ── Cancelling ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Cancelling_while_a_watchlist_page_is_fetched_stops_the_run_there()
    {
        var progress = new RecordedProgress<RunProgress>();
        var http = Sites((request, _) =>
        {
            if (request.RequestUri!.ToString() != Watchlist + "2/") return null;
            _cancel.Cancel();
            throw new TaskCanceledException("The request was cancelled.");
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new CheckRunner(new HttpClient(http), Data).RunAsync(Settings(), progress, _cancel.Token));

        Assert.Empty(Steps(progress, RunStep.FetchingServices));
    }

    [Fact]
    public async Task Cancelling_while_a_film_is_searched_stops_the_run_there()
    {
        // Every other film is already cached, so a run that wrongly carried on would race to the end.
        var cache = new JustWatchCache(Data.JustWatchCacheFile, expiryDays: 7);
        foreach (var id in FilmIds.Where(id => id != "1284043"))
            cache.Set(id, new List<Offer>());
        cache.Save();
        var imdb = new ImdbIdCache(Data.ImdbCacheFile);
        imdb.Set("its-never-over-jeff-buckley", null);
        imdb.Save();

        var progress = new RecordedProgress<RunProgress>();
        var http = Sites((request, body) =>
        {
            if (!body.Contains("Never Over")) return null;
            _cancel.Cancel();
            throw new TaskCanceledException("The request was cancelled.");
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new CheckRunner(new HttpClient(http), Data).RunAsync(Settings(), progress, _cancel.Token));

        Assert.Equal(1, Steps(progress, RunStep.CheckingFilms).Max(p => p.Done));
        Assert.Empty(Steps(progress, RunStep.Saving));
    }

    [Fact]
    public async Task Nothing_more_is_requested_after_a_cancel()
    {
        var requestsAtCancel = -1;
        FakeHttp? http = null;
        var progress = new RecordedProgress<RunProgress>(p =>
        {
            if (p.Step == RunStep.CheckingFilms && p.Done == 2)
            {
                requestsAtCancel = http!.RequestedUrls.Count;
                _cancel.Cancel();
            }
        });
        http = Sites();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new CheckRunner(new HttpClient(http), Data).RunAsync(Settings(), progress, _cancel.Token));

        Assert.Equal(requestsAtCancel, http.RequestedUrls.Count);
    }

    // ── No console in Core ──────────────────────────────────────────────────────

    [Fact]
    public void Core_never_writes_to_the_console()
    {
        var path = typeof(CheckRunner).Assembly.Location;
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader();

        var consoleReferences = metadata.TypeReferences
            .Select(metadata.GetTypeReference)
            .Where(t => metadata.GetString(t.Namespace) == "System" && metadata.GetString(t.Name) == "Console");

        Assert.Empty(consoleReferences);
    }
}
