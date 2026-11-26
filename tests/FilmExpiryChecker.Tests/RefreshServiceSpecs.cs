using System.Diagnostics;
using FilmExpiryChecker.JustWatch;
using FilmExpiryChecker.Running;
using FilmExpiryChecker.Storage;
using FilmExpiryChecker.Web.Refresh;
using Microsoft.Extensions.DependencyInjection;

namespace FilmExpiryChecker.Tests;

/// <summary>The background refresh, run inside the web app against the fake sites.</summary>
public sealed class RefreshServiceSpecs : IDisposable
{
    private readonly TempFolder _data = new();

    public void Dispose() => _data.Dispose();

    private TestApp App(FakeHttp network) => new TestApp(network, _data.Path).WithUser();

    private static RefreshService Refresh(TestApp app) => app.Services.GetRequiredService<RefreshService>();

    private static async Task WaitFor(Func<bool> condition, string what)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.Elapsed > TimeSpan.FromSeconds(15)) throw new TimeoutException($"Timed out waiting for {what}.");
            await Task.Delay(20);
        }
    }

    private static Task Finished(RefreshService refresh) =>
        WaitFor(() => refresh.State is not RefreshState.Running, "the refresh to finish");

    // ── A refresh ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_refresh_runs_with_rising_progress_and_ends_idle_with_a_new_last_success()
    {
        using var app = App(FakeSites.Create());
        var refresh = Refresh(app);
        var seen = new List<RefreshState>();
        refresh.Changed += s => { lock (seen) seen.Add(s); };
        Assert.Null(refresh.State.LastSuccess);

        Assert.True(refresh.RequestRefresh());
        await Finished(refresh);

        var idle = Assert.IsType<RefreshState.Idle>(refresh.State);
        Assert.NotNull(idle.LastSuccess);
        lock (seen)
        {
            Assert.IsType<RefreshState.Running>(seen[0]);
            var checking = seen.OfType<RefreshState.Running>().Where(s => s.Step == RunStep.CheckingFilms).Select(s => s.Done).ToList();
            Assert.Equal(new[] { 0, 1, 2, 3, 4, 5 }, checking);
        }
    }

    [Fact]
    public async Task A_finished_refresh_becomes_the_latest_results()
    {
        using var app = App(FakeSites.Create());
        var refresh = Refresh(app);
        var told = 0;
        app.Services.GetRequiredService<LatestResults>().Changed += () => Interlocked.Increment(ref told);

        refresh.RequestRefresh();
        await Finished(refresh);

        var latest = app.Services.GetRequiredService<LatestResults>().Run;
        Assert.NotNull(latest);
        Assert.Equal(5, latest.Films.Count);
        Assert.Equal(new RunStore(app.Data).Latest()!.Id, latest.Id);
        Assert.Equal(1, told);
    }

    [Fact]
    public async Task Asking_again_during_a_refresh_starts_nothing_more()
    {
        using var gate = new ManualResetEventSlim();
        var network = FakeSites.Create((request, _, ct) =>
        {
            if (request.RequestUri!.ToString() == FakeSites.Watchlist + "1/") gate.Wait(ct);
            return null;
        });
        using var app = App(network);
        var refresh = Refresh(app);

        var first = refresh.RequestRefresh();
        var second = refresh.RequestRefresh();
        gate.Set();
        await Finished(refresh);

        Assert.True(first);
        Assert.False(second);
        Assert.Single(network.RequestedUrls, u => u == FakeSites.Watchlist + "1/");
        Assert.Single(new RunStore(app.Data).List());
    }

    // ── When it fails ───────────────────────────────────────────────────────────

    [Fact]
    public async Task A_failed_refresh_says_why_in_plain_words_and_keeps_the_previous_results()
    {
        var offline = false;
        using var app = App(FakeSites.Create((request, _, _) =>
            offline && request.RequestUri!.Host == "letterboxd.com" ? throw new HttpRequestException("No such host is known.") : null));
        var refresh = Refresh(app);
        refresh.RequestRefresh();
        await Finished(refresh);
        var good = app.Services.GetRequiredService<LatestResults>().Run!;

        offline = true;
        refresh.RequestRefresh();
        await Finished(refresh);

        var failed = Assert.IsType<RefreshState.Failed>(refresh.State);
        Assert.Equal("Couldn't reach Letterboxd. Check your internet connection and try again.", failed.Reason);
        Assert.Equal(good.RanAt, failed.LastSuccess);
        Assert.Equal(good.Id, app.Services.GetRequiredService<LatestResults>().Run!.Id);
        Assert.Equal(good.Id, new RunStore(app.Data).Latest()!.Id);
    }

    [Fact]
    public async Task An_empty_or_private_watchlist_is_explained()
    {
        using var app = App(FakeSites.Create((request, _, _) =>
            request.RequestUri!.Host == "letterboxd.com" ? FakeHttp.NotFound() : null));
        var refresh = Refresh(app);

        refresh.RequestRefresh();
        await Finished(refresh);

        var failed = Assert.IsType<RefreshState.Failed>(refresh.State);
        Assert.StartsWith("Letterboxd shows no films on example-user's watchlist.", failed.Reason);
    }

    [Fact]
    public async Task With_no_username_set_it_asks_for_one_and_fetches_nothing()
    {
        var network = FakeSites.Create();
        using var app = new TestApp(network, _data.Path);
        var refresh = Refresh(app);

        refresh.RequestRefresh();
        await Finished(refresh);

        var failed = Assert.IsType<RefreshState.Failed>(refresh.State);
        Assert.Equal("No Letterboxd username is set yet. Add yours in Settings.", failed.Reason);
        Assert.Empty(network.RequestedUrls);
    }

    [Fact]
    public async Task An_unexpected_error_is_reported_without_its_technical_details()
    {
        using var app = App(FakeSites.Create((_, body, _) =>
            body.Contains("GetPackages") ? throw new InvalidOperationException("Sequence contains no elements at Foo.Bar()") : null));
        var refresh = Refresh(app);

        refresh.RequestRefresh();
        await Finished(refresh);

        var failed = Assert.IsType<RefreshState.Failed>(refresh.State);
        Assert.Equal("Something went wrong while refreshing. The details are in the log.", failed.Reason);
        Assert.Contains("Sequence contains no elements", File.ReadAllText(Directory.GetFiles(app.Data.LogsFolder).Single()));
    }

    // ── Closing the app ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Closing_the_app_mid_refresh_stops_it_promptly_and_keeps_the_films_done()
    {
        using var reached = new ManualResetEventSlim();
        var network = FakeSites.Create((_, body, ct) =>
        {
            if (!body.Contains("\"Socrates\"")) return null;
            reached.Set();
            ct.WaitHandle.WaitOne();       // hang, as a slow request would, until the app gives up on it
            throw new TaskCanceledException();
        });
        var app = App(network);
        var refresh = Refresh(app);

        refresh.RequestRefresh();
        await WaitFor(() => reached.IsSet, "the third film's search");
        var clock = Stopwatch.StartNew();
        await app.DisposeAsync();

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30), $"took {clock.Elapsed}");
        var cache = new JustWatchCache(new DataFolder(_data.Path).JustWatchCacheFile, 7);
        Assert.True(cache.TryGet("154091", out _) && cache.TryGet("1284043", out _), "the two films checked were not kept");
        Assert.Empty(new RunStore(new DataFolder(_data.Path)).List());
    }
}
