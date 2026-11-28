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
    private const string Watchlist = FakeSites.Watchlist;
    private const string JustWatchApi = FakeSites.JustWatchApi;

    private static readonly string[] FilmIds = FakeSites.FilmIds;

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

    private static FakeHttp Sites(Func<HttpRequestMessage, string, HttpResponseMessage?>? intercept = null) =>
        FakeSites.Create(intercept is null ? null : (request, body, _) => intercept(request, body));

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
    public async Task Saving_a_run_tidies_earlier_weeks_down_to_their_latest_and_keeps_the_new_one()
    {
        var store = new RunStore(Data);
        SavedRun Old(int day, int hour) => store.Save(new SavedRun { RanAt = new DateTimeOffset(2026, 1, day, hour, 0, 0, TimeSpan.Zero) });
        Old(5, 9); Old(7, 9); var newestThatWeek = Old(11, 20);   // Mon 5 – Sun 11 Jan 2026
        var onlyOneThatWeek = Old(14, 9);                          // the next week

        var result = await new CheckRunner(new HttpClient(Sites()), Data, time: InstantWaits.Instance).RunAsync(Settings());

        var kept = store.List().Select(e => e.Id).ToArray();
        Assert.Equal(new[] { result.Run!.Id, onlyOneThatWeek.Id, newestThatWeek.Id }, kept);
        Assert.Equal(result.Run.Id, store.Latest()!.Id);
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
    public async Task No_services_from_JustWatch_fails_the_run_in_plain_words()
    {
        var http = Sites((request, body) =>
            body.Contains("GetPackages") ? throw new HttpRequestException("No such host is known.") : null);

        var failed = await Assert.ThrowsAsync<RunFailedException>(() =>
            new CheckRunner(new HttpClient(http), Data, time: InstantWaits.Instance).RunAsync(Settings()));

        Assert.Contains("Couldn't reach JustWatch", failed.Message);
        Assert.Empty(new RunStore(Data).List());
    }

    [Fact]
    public async Task JustWatch_failing_search_after_search_fails_the_run_instead_of_saving_every_film_as_not_found()
    {
        // The first film is found; every search after it fails, as when JustWatch goes down mid-run.
        var watchlist = LongWatchlist(10).Replace("Made Up Film 1 (2000)", "Kisapmata (1981)");
        var longHttp = Sites((request, body) =>
            request.RequestUri!.ToString() == Watchlist + "1/" ? FakeHttp.Html(watchlist)
            : body.Contains("GetTitleOffers") && !body.Contains("Kisapmata") ? throw new HttpRequestException("Service unavailable")
            : null);

        var failed = await Assert.ThrowsAsync<RunFailedException>(() =>
            new CheckRunner(new HttpClient(longHttp), Data, time: InstantWaits.Instance).RunAsync(Settings()));

        Assert.Contains("JustWatch stopped answering", failed.Message);
        Assert.Empty(new RunStore(Data).List());
        Assert.True(new JustWatchCache(Data.JustWatchCacheFile, 7).TryGet("1001", out _), "the film found before the failures was not kept");
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

    // ── Never a silently broken run (#45) ───────────────────────────────────────

    private const string GraphQlError = """{"errors":[{"message":"Cannot query field \"popularTitles\" on type \"Query\"."}],"data":null}""";

    private FakeHttp SitesWithLongWatchlistAnd(int count, Func<string, HttpResponseMessage?> search) => Sites((request, body) =>
        request.RequestUri!.ToString() == Watchlist + "1/" ? FakeHttp.Html(LongWatchlist(count))
        : body.Contains("GetTitleOffers") ? search(body)
        : null);

    [Fact]
    public async Task JustWatch_answering_with_GraphQL_errors_fails_the_run_instead_of_saving_everything_as_not_found()
    {
        var http = SitesWithLongWatchlistAnd(30, _ => FakeHttp.Json(GraphQlError));

        var failed = await Assert.ThrowsAsync<RunFailedException>(() =>
            new CheckRunner(new HttpClient(http), Data, time: InstantWaits.Instance).RunAsync(Settings()));

        Assert.Contains("JustWatch stopped answering properly", failed.Message);
        Assert.Empty(new RunStore(Data).List());
    }

    [Fact]
    public async Task Failures_scattered_through_a_run_fail_it_too_not_only_failures_in_a_row()
    {
        // Every other search fails, so there are never five in a row, but half of them fail.
        var n = 0;
        var http = SitesWithLongWatchlistAnd(40, body => Interlocked.Increment(ref n) % 2 == 0
            ? new HttpResponseMessage(System.Net.HttpStatusCode.TooManyRequests) : null);

        var failed = await Assert.ThrowsAsync<RunFailedException>(() =>
            new CheckRunner(new HttpClient(http), Data, time: InstantWaits.Instance).RunAsync(Settings()));

        Assert.Contains("JustWatch stopped answering properly", failed.Message);
    }

    [Fact]
    public async Task Too_many_films_not_found_this_time_keeps_the_previous_results()
    {
        var previous = new RunStore(Data).Save(new SavedRun { RanAt = DateTimeOffset.UtcNow.AddDays(-1), Username = "example-user" });
        // Answers properly, but with no match for any film: as when JustWatch changes what its titles look like.
        var http = SitesWithLongWatchlistAnd(30, _ => FakeHttp.Json("""{"data":{"popularTitles":{"edges":[]}}}"""));

        var failed = await Assert.ThrowsAsync<RunFailedException>(() =>
            new CheckRunner(new HttpClient(http), Data, time: InstantWaits.Instance).RunAsync(Settings()));

        Assert.StartsWith("JustWatch found only 0 of the 30 films it was asked about", failed.Message);
        Assert.Equal(previous.Id, new RunStore(Data).Latest()!.Id);
    }

    /// <summary>A previous run of 40 made-up films: the first 25 found (and cached), the last 15 not found.</summary>
    private void PreviousRunWith25FoundAnd15NotFound(bool cacheTheFound)
    {
        var cache = new JustWatchCache(Data.JustWatchCacheFile, 7);
        new RunStore(Data).Save(new SavedRun
        {
            RanAt = DateTimeOffset.UtcNow.AddDays(-1),
            Username = "example-user",
            Films = Enumerable.Range(1, 40).Select(i => new SavedFilm
            {
                Film = new WatchlistFilm { Title = $"Made Up Film {i}", FilmId = $"{1000 + i}" },
                Offers = i <= 25 ? [] : null,
            }).ToList(),
        });
        if (cacheTheFound)
            foreach (var i in Enumerable.Range(1, 25)) cache.Set($"{1000 + i}", []);
        cache.Save();
    }

    [Fact]
    public async Task An_ordinary_refresh_that_only_asks_again_about_films_not_found_before_is_kept()
    {
        // As on the owner's real data: the found films come from the cache, so the only films asked are the ones
        // not found last time (15 here, 22 on the owner's), and they're still not found. That's no sign of trouble
        // (found by hand, #45).
        PreviousRunWith25FoundAnd15NotFound(cacheTheFound: true);
        var http = SitesWithLongWatchlistAnd(40, _ => FakeHttp.Json("""{"data":{"popularTitles":{"edges":[]}}}"""));

        var result = await new CheckRunner(new HttpClient(http), Data, time: InstantWaits.Instance).RunAsync(Settings());

        Assert.Equal(result.Run!.Id, new RunStore(Data).Latest()!.Id);
    }

    [Fact]
    public async Task Films_found_last_time_now_mostly_not_found_keeps_the_previous_results()
    {
        PreviousRunWith25FoundAnd15NotFound(cacheTheFound: false);
        var previous = new RunStore(Data).Latest()!;
        var http = SitesWithLongWatchlistAnd(40, _ => FakeHttp.Json("""{"data":{"popularTitles":{"edges":[]}}}"""));

        var failed = await Assert.ThrowsAsync<RunFailedException>(() =>
            new CheckRunner(new HttpClient(http), Data, time: InstantWaits.Instance).RunAsync(Settings()));

        Assert.StartsWith("JustWatch found only 0 of the 25 films it was asked about that it found last time", failed.Message);
        Assert.Equal(previous.Id, new RunStore(Data).Latest()!.Id);
    }

    [Fact]
    public async Task A_watchlist_much_smaller_than_last_time_is_held_back_once_then_accepted()
    {
        var store = new RunStore(Data);
        var previous = store.Save(new SavedRun
        {
            RanAt = DateTimeOffset.UtcNow.AddDays(-1),
            Username = "example-user",
            Films = Enumerable.Range(1, 40).Select(i => new SavedFilm { Film = new WatchlistFilm { Title = $"Film {i}", FilmId = $"{2000 + i}" }, Offers = [] }).ToList(),
        });
        var runner = new CheckRunner(new HttpClient(Sites()), Data, time: InstantWaits.Instance);

        var failed = await Assert.ThrowsAsync<RunFailedException>(() => runner.RunAsync(Settings()));
        Assert.StartsWith("Your Letterboxd watchlist came back with 5 films, down from 40 last time", failed.Message);
        Assert.Equal(previous.Id, store.Latest()!.Id);

        var second = await runner.RunAsync(Settings());

        Assert.Equal(second.Run!.Id, store.Latest()!.Id);
        Assert.Equal(5, store.Latest()!.Films.Count);
    }

    [Fact]
    public async Task A_film_page_that_wont_load_isnt_remembered_as_having_no_IMDb_ID()
    {
        // No title matches, so the IMDb ID is looked up; the first time the film page fails, the second it loads.
        var filmPageTries = 0;
        FakeHttp Http() => Sites((request, body) =>
            request.RequestUri!.AbsolutePath.StartsWith("/film/")
                ? (++filmPageTries == 1 ? throw new HttpRequestException("Connection reset.") : FakeHttp.Html("<p>no imdb link</p>"))
            : body.Contains("GetTitleOffers") ? FakeHttp.Json("""{"data":{"popularTitles":{"edges":[]}}}""")
            : null);

        await new CheckRunner(new HttpClient(Http()), Data, time: InstantWaits.Instance).RunAsync(Settings());
        var afterFailure = new ImdbIdCache(Data.ImdbCacheFile);

        Assert.False(afterFailure.TryGet("kisapmata", out _), "a failed fetch was cached as 'no IMDb ID'");
        Assert.True(afterFailure.TryGet("its-never-over-jeff-buckley", out var none) && none is null, "a page that loaded wasn't cached");
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
