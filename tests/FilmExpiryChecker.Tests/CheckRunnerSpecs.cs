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

        await new CheckRunner(new HttpClient(Sites()), Data, time: InstantWaits.Instance).RunAsync(Settings(), progress);

        var steps = progress.Reports.Select(p => p.Step).Distinct();
        Assert.Equal(new[] { RunStep.ReadingWatchlist, RunStep.FetchingServices, RunStep.CheckingFilms, RunStep.Saving }, steps);
    }

    [Fact]
    public async Task Reading_the_watchlist_reports_each_page_and_the_films_found_so_far()
    {
        var progress = new RecordedProgress<RunProgress>();

        await new CheckRunner(new HttpClient(Sites()), Data, time: InstantWaits.Instance).RunAsync(Settings(), progress);

        var pages = Steps(progress, RunStep.ReadingWatchlist);
        Assert.Equal(new[] { 0, 1, 2 }, pages.Select(p => p.Done));
        Assert.Equal(new[] { 0, 3, 5 }, pages.Select(p => p.Films));
    }

    [Fact]
    public async Task Checking_films_counts_up_to_the_total()
    {
        var progress = new RecordedProgress<RunProgress>();

        await new CheckRunner(new HttpClient(Sites()), Data, time: InstantWaits.Instance).RunAsync(Settings(), progress);

        var checking = Steps(progress, RunStep.CheckingFilms);
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5 }, checking.Select(p => p.Done));
        Assert.All(checking, p => Assert.Equal(5, p.Total));
    }

    [Fact]
    public async Task A_completed_run_returns_every_film_grouped()
    {
        var result = await new CheckRunner(new HttpClient(Sites()), Data, time: InstantWaits.Instance).RunAsync(Settings());

        Assert.Equal(RunOutcome.Completed, result.Outcome);
        Assert.Equal(5, result.Films.Count);
        Assert.All(result.Films, f => Assert.Equal(FilmCategory.AvailableMyServices, f.Category));
        Assert.Equal(1, result.ServiceCount);
    }

    [Fact]
    public async Task A_completed_run_saves_what_it_fetched_to_the_cache()
    {
        await new CheckRunner(new HttpClient(Sites()), Data, time: InstantWaits.Instance).RunAsync(Settings());

        var cache = new JustWatchCache(Data.JustWatchCacheFile, expiryDays: 7);
        Assert.All(FilmIds, id => Assert.True(cache.TryGet(id, out _), $"film {id} not cached"));
    }

    [Fact]
    public async Task A_completed_run_is_saved_with_every_film_and_its_offers()
    {
        var result = await new CheckRunner(new HttpClient(Sites()), Data, time: InstantWaits.Instance).RunAsync(Settings());

        var saved = new RunStore(Data).Latest();
        Assert.NotNull(saved);
        Assert.Equal(result.Run!.Id, saved.Id);
        Assert.Equal(5, saved.Films.Count);
        Assert.All(saved.Films, f => Assert.Equal(8, f.Offers!.Single().PackageId));
        Assert.Equal("example-user", saved.Username);
    }

    [Fact]
    public async Task A_film_on_two_pages_is_listed_once()
    {
        // As happens when a film is added while the pages are read: page 2 starts with page 1's last film.
        var page1 = Fixture.Read("watchlist-page-1.html");
        var lastOfPage1 = System.Text.RegularExpressions.Regex.Matches(page1, """<li class="griditem">[\s\S]*?</li>""")[^1].Value;
        var page2 = Fixture.Read("watchlist-page-2.html").Replace("""<ul class="grid -p125 -scaled128">""", """<ul class="grid -p125 -scaled128">""" + lastOfPage1);
        var http = Sites((request, _) => request.RequestUri!.ToString() == Watchlist + "2/" ? FakeHttp.Html(page2) : null);

        var progress = new RecordedProgress<RunProgress>();

        await new CheckRunner(new HttpClient(http), Data, time: InstantWaits.Instance).RunAsync(Settings(), progress);

        var saved = new RunStore(Data).Latest()!;
        Assert.Equal(6, Steps(progress, RunStep.ReadingWatchlist).Last().Films);
        Assert.Equal(5, saved.Films.Count);
        Assert.Single(saved.Films, f => f.Film.Slug == "socrates");
        Assert.All(Steps(progress, RunStep.CheckingFilms), p => Assert.Equal(5, p.Total));
    }

    [Fact]
    public async Task A_cancelled_run_is_not_saved()
    {
        var progress = new RecordedProgress<RunProgress>(p =>
        {
            if (p.Step == RunStep.CheckingFilms && p.Done == 3) _cancel.Cancel();
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new CheckRunner(new HttpClient(Sites()), Data, time: InstantWaits.Instance).RunAsync(Settings(), progress, _cancel.Token));

        Assert.Empty(new RunStore(Data).List());
    }

    [Fact]
    public async Task A_run_that_fails_is_not_saved()
    {
        // A failed search counts as "not found", but a failure fetching the services stops the run.
        var http = Sites((request, body) => body.Contains("GetPackages") ? throw new InvalidOperationException("broken") : null);

        await Assert.ThrowsAnyAsync<Exception>(() =>
            new CheckRunner(new HttpClient(http), Data, time: InstantWaits.Instance).RunAsync(Settings()));

        Assert.Empty(new RunStore(Data).List());
    }

    [Fact]
    public async Task An_empty_watchlist_is_its_own_outcome_and_asks_JustWatch_nothing()
    {
        var http = Sites((request, _) =>
            request.RequestUri!.Host == "letterboxd.com" ? FakeHttp.NotFound() : null);

        var result = await new CheckRunner(new HttpClient(http), Data, time: InstantWaits.Instance).RunAsync(Settings());

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
            new CheckRunner(new HttpClient(http), Data, time: InstantWaits.Instance).RunAsync(Settings(), progress, _cancel.Token));

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
            new CheckRunner(new HttpClient(http), Data, time: InstantWaits.Instance).RunAsync(Settings(), progress, _cancel.Token));

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
            new CheckRunner(new HttpClient(http), Data, time: InstantWaits.Instance).RunAsync(Settings(), progress, _cancel.Token));

        Assert.Equal(requestsAtCancel, http.RequestedUrls.Count);
    }

    // ── Saving as it goes ───────────────────────────────────────────────────────

    /// <summary>One watchlist page of <paramref name="count"/> made-up films, IDs 1001 upwards, and no next page.</summary>
    private static string LongWatchlist(int count) =>
        "<html><body><ul>" +
        string.Concat(Enumerable.Range(1, count).Select(i =>
            $"""<li class="griditem"><div class="react-component" data-component-class="LazyPoster" data-item-name="Made Up Film {i} (2000)" data-item-slug="made-up-film-{i}" data-postered-identifier='{"{"}&quot;uid&quot;:&quot;film:{1000 + i}&quot;{"}"}'></div></li>""")) +
        "</ul></body></html>";

    private FakeHttp SitesWithLongWatchlist(int count) => Sites((request, _) =>
        request.RequestUri!.ToString() == Watchlist + "1/" ? FakeHttp.Html(LongWatchlist(count)) : null);

    private int FilmsInSavedCache() =>
        Enumerable.Range(1001, 40).Count(id => new JustWatchCache(Data.JustWatchCacheFile, 7).TryGet(id.ToString(), out _));

    [Fact]
    public async Task The_cache_is_saved_every_25_films_during_a_run()
    {
        var savedPartWay = -1;
        var progress = new RecordedProgress<RunProgress>(p =>
        {
            if (p.Step == RunStep.CheckingFilms && p.Done == 30) savedPartWay = FilmsInSavedCache();
        });

        await new CheckRunner(new HttpClient(SitesWithLongWatchlist(40)), Data, time: InstantWaits.Instance)
            .RunAsync(Settings(), progress);

        Assert.True(savedPartWay >= 25, $"only {savedPartWay} films were saved by film 30");
    }

    [Fact]
    public async Task A_run_cancelled_after_30_films_keeps_them_and_the_next_run_skips_them()
    {
        var progress = new RecordedProgress<RunProgress>(p =>
        {
            if (p.Step == RunStep.CheckingFilms && p.Done == 30) _cancel.Cancel();
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new CheckRunner(new HttpClient(SitesWithLongWatchlist(40)), Data, time: InstantWaits.Instance)
                .RunAsync(Settings(), progress, _cancel.Token));

        Assert.Equal(30, FilmsInSavedCache());

        var http = SitesWithLongWatchlist(40);
        await new CheckRunner(new HttpClient(http), Data, time: InstantWaits.Instance).RunAsync(Settings());
        var searches = http.RequestBodies.Count(b => b.Contains("GetTitleOffers"));
        Assert.Equal(10, searches);
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
