using System.Diagnostics;
using Bunit;
using FilmExpiryChecker.JustWatch;
using FilmExpiryChecker.Letterboxd;
using FilmExpiryChecker.Storage;
using FilmExpiryChecker.Web.Components.Layout;
using FilmExpiryChecker.Web.Refresh;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace FilmExpiryChecker.Tests;

/// <summary>Refreshing by itself, once a day.</summary>
public sealed class AutoRefreshSpecs : IDisposable
{
    // Ten hours ahead of UTC, so the local and UTC dates disagree for part of every day.
    private static readonly TimeZoneInfo Plus10 = TimeZoneInfo.CreateCustomTimeZone("Test+10", TimeSpan.FromHours(10), "Test+10", "Test+10");

    private static DateTimeOffset Local(int day, int hour, int minute) =>
        new DateTimeOffset(2026, 11, day, hour, minute, 0, TimeSpan.FromHours(10)).ToUniversalTime();

    private readonly TempFolder _data = new();

    public void Dispose() => _data.Dispose();

    // ── The rule ────────────────────────────────────────────────────────────────

    [Fact]
    public void Never_refreshed_means_refresh() =>
        Assert.True(AutoRefresh.ShouldStart(null, Local(26, 9, 0), Plus10, usernameSet: true));

    [Fact]
    public void Refreshed_earlier_today_means_leave_it() =>
        Assert.False(AutoRefresh.ShouldStart(Local(26, 7, 12), Local(26, 21, 0), Plus10, usernameSet: true));

    [Fact]
    public void Refreshed_at_23_59_yesterday_means_refresh_at_00_01() =>
        Assert.True(AutoRefresh.ShouldStart(Local(25, 23, 59), Local(26, 0, 1), Plus10, usernameSet: true));

    [Fact]
    public void Last_success_yesterday_still_means_refresh_after_a_failed_attempt_today()
    {
        // A failed refresh leaves the last success where it was, so the state's LastSuccess is still yesterday's.
        var afterFailure = new RefreshState.Failed("Couldn't reach Letterboxd.", Local(26, 8, 0), Local(25, 19, 46));

        Assert.True(AutoRefresh.ShouldStart(afterFailure.LastSuccess, Local(26, 9, 0), Plus10, usernameSet: true));
    }

    [Fact]
    public void With_no_username_it_never_refreshes_by_itself() =>
        Assert.False(AutoRefresh.ShouldStart(null, Local(26, 9, 0), Plus10, usernameSet: false));

    [Fact]
    public void Today_is_the_users_today_not_UTCs()
    {
        // 00:30 on the 26th local is 14:30 UTC on the 25th; a run at 23:00 on the 25th local is 13:00 UTC on the 25th.
        // The same UTC day, but yesterday for the user: refresh.
        Assert.True(AutoRefresh.ShouldStart(Local(25, 23, 0), Local(26, 0, 30), Plus10, usernameSet: true));
    }

    // ── On starting the app ─────────────────────────────────────────────────────

    private TestApp StartedWithRun(DateTimeOffset ranAt, FakeHttp network, DateTimeOffset now)
    {
        var data = new DataFolder(_data.Path);
        data.Prepare([]);
        new RunStore(data).Save(new SavedRun
        {
            RanAt = ranAt,
            MyServices = { "8" },
            ExpiringSoonDays = 60,
            Services = { new Package { PackageId = 8, ClearName = "Netflix" } },
            Films = { new SavedFilm { Film = new WatchlistFilm { Title = "Old Film", FilmId = "1" }, Offers = [] } },
        });
        var app = new TestApp(network, _data.Path, new FixedClock(now, Plus10, instantWaits: true), autoRefreshOnStart: true).WithUser();
        _ = app.Services;   // starts the app
        return app;
    }

    private static async Task Settle(RefreshService refresh)
    {
        var clock = Stopwatch.StartNew();
        while (refresh.State is RefreshState.Running && clock.Elapsed < TimeSpan.FromSeconds(15))
            await Task.Delay(20);
        await Task.Delay(200);   // anything that was going to start has had time to
    }

    [Fact]
    public async Task Starting_the_app_after_todays_refresh_fetches_nothing()
    {
        var network = FakeSites.Create();
        using var app = StartedWithRun(Local(26, 7, 12), network, now: Local(26, 18, 0));

        await Settle(app.Services.GetRequiredService<RefreshService>());

        Assert.Empty(network.RequestedUrls);
        Assert.IsType<RefreshState.Idle>(app.Services.GetRequiredService<RefreshService>().State);
    }

    [Fact]
    public async Task Starting_the_app_after_yesterdays_refresh_refreshes_once()
    {
        var network = FakeSites.Create();
        using var app = StartedWithRun(Local(25, 19, 46), network, now: Local(26, 8, 0));
        var refresh = app.Services.GetRequiredService<RefreshService>();

        await Settle(refresh);

        Assert.Single(network.RequestedUrls, u => u == FakeSites.Watchlist + "1/");
        Assert.Equal(2, new RunStore(app.Data).List().Count);
    }

    // ── On opening a page ───────────────────────────────────────────────────────

    /// <summary>Renders the page-open check, as live (or not), with the last success given; returns refreshes asked for.</summary>
    private async Task<int> OpenPage(DateTimeOffset lastSuccess, DateTimeOffset now, bool live)
    {
        using var context = new BunitContext();
        var data = new DataFolder(_data.Path);
        data.Prepare([]);
        var settings = SettingsStore.Load(data.SettingsFile);
        settings.Letterboxd.Username = "example-user";
        SettingsStore.Save(data.SettingsFile, settings);
        var refresh = new FakeRefreshStatus();
        await refresh.Push(new RefreshState.Idle(lastSuccess));
        context.Services.AddSingleton<IRefreshStatus>(refresh);
        context.Services.AddSingleton(new AutoRefresh(refresh, data, new FixedClock(now, Plus10)));
        context.Renderer.SetRendererInfo(new RendererInfo(live ? "Server" : "Static", isInteractive: live));

        context.Render<AutoRefreshOnOpen>();

        return refresh.Requests;
    }

    [Fact]
    public async Task Opening_a_page_the_next_day_refreshes_if_the_app_was_left_running() =>
        Assert.Equal(1, await OpenPage(lastSuccess: Local(25, 19, 46), now: Local(26, 8, 0), live: true));

    [Fact]
    public async Task Opening_a_page_again_the_same_day_does_not_refresh() =>
        Assert.Equal(0, await OpenPage(lastSuccess: Local(26, 7, 12), now: Local(26, 18, 0), live: true));

    [Fact]
    public async Task The_first_HTML_sent_before_the_page_is_live_does_not_refresh() =>
        // Prerendering runs before the page is live; only the live render counts as opening the page.
        Assert.Equal(0, await OpenPage(lastSuccess: Local(25, 19, 46), now: Local(26, 8, 0), live: false));
}
