using FilmExpiryChecker.Running;
using FilmExpiryChecker.Web.Refresh;

namespace FilmExpiryChecker.Tests;

/// <summary>The wording of the status bar, for every state, in a zone where the UTC and local dates can differ.</summary>
public class StatusTextSpecs
{
    // Ten hours ahead of UTC, so local midnight is 14:00 UTC: plenty of moments where the two dates disagree.
    private static readonly TimeZoneInfo Sydneyish = TimeZoneInfo.CreateCustomTimeZone("Test+10", TimeSpan.FromHours(10), "Test+10", "Test+10");

    // 26 Nov 2026, 17:00 local.
    private static readonly DateTimeOffset Now = new(2026, 11, 26, 7, 0, 0, TimeSpan.Zero);

    private static DateTimeOffset Local(int month, int day, int hour, int minute, int year = 2026) =>
        new DateTimeOffset(year, month, day, hour, minute, 0, TimeSpan.FromHours(10)).ToUniversalTime();

    private static ResultsSummary Shown(DateTimeOffset ranAt, int films = 482, int leavingSoon = 9) => new(ranAt, films, leavingSoon);

    private static StatusLine Idle(ResultsSummary? shown, bool usernameSet = true, DateTimeOffset? now = null) =>
        StatusText.For(new RefreshState.Idle(shown?.RanAt), shown, usernameSet, now ?? Now, Sydneyish);

    // ── Up to date ──────────────────────────────────────────────────────────────

    [Fact]
    public void Results_from_today_say_today_with_their_time_and_counts()
    {
        Assert.Equal("Updated today at 07:12 · 482 films · 9 leaving soon", Idle(Shown(Local(11, 26, 7, 12))).Text);
    }

    [Fact]
    public void Results_from_yesterday_say_yesterday()
    {
        Assert.Equal("Updated yesterday at 21:40 · 482 films · 9 leaving soon", Idle(Shown(Local(11, 25, 21, 40))).Text);
    }

    [Fact]
    public void Older_results_give_the_day_and_month()
    {
        Assert.Equal("Updated on 20 Sep at 18:17 · 482 films · 9 leaving soon", Idle(Shown(Local(9, 20, 18, 17))).Text);
    }

    [Fact]
    public void Results_from_another_year_give_the_year_too()
    {
        Assert.Equal("Updated on 30 Dec 2025 at 10:00 · 482 films · 9 leaving soon", Idle(Shown(Local(12, 30, 10, 0, 2025))).Text);
    }

    [Fact]
    public void One_film_is_not_plural()
    {
        Assert.Equal("Updated today at 07:12 · 1 film · 0 leaving soon", Idle(Shown(Local(11, 26, 7, 12), 1, 0)).Text);
    }

    [Fact]
    public void Up_to_date_shows_no_bar_retry_or_settings_link()
    {
        var line = Idle(Shown(Local(11, 26, 7, 12)));

        Assert.Equal((null, false, false), (line.Progress, line.OfferRetry, line.PointToSettings));
    }

    // ── Around midnight ─────────────────────────────────────────────────────────

    [Fact]
    public void Just_before_local_midnight_is_yesterday_once_midnight_has_passed_even_on_the_same_UTC_day()
    {
        // 23:30 on the 25th local is 13:30 UTC on the 25th; 00:30 on the 26th local is 14:30 UTC, still the 25th.
        var line = Idle(Shown(Local(11, 25, 23, 30)), now: Local(11, 26, 0, 30));

        Assert.StartsWith("Updated yesterday at 23:30", line.Text);
    }

    [Fact]
    public void Just_after_local_midnight_is_today_even_though_it_was_the_day_before_in_UTC()
    {
        // 00:05 on the 26th local is 14:05 UTC on the 25th.
        var line = Idle(Shown(Local(11, 26, 0, 5)), now: Local(11, 26, 23, 0));

        Assert.StartsWith("Updated today at 00:05", line.Text);
    }

    [Fact]
    public void A_time_from_summer_time_is_shown_as_it_was_on_the_clock_then()
    {
        var london = TimeZoneInfo.FindSystemTimeZoneById("GMT Standard Time");
        var july = new DateTimeOffset(2026, 7, 1, 8, 0, 0, TimeSpan.Zero);   // 09:00 BST

        var line = StatusText.For(new RefreshState.Idle(july), Shown(july), true, Now, london);

        Assert.StartsWith("Updated on 1 Jul at 09:00", line.Text);
    }

    // ── Refreshing ──────────────────────────────────────────────────────────────

    private static StatusLine Running(RunStep step, int done, int? total) =>
        StatusText.For(new RefreshState.Running(step, done, total, Now, null), null, true, Now, Sydneyish);

    [Fact]
    public void Checking_films_counts_them_with_a_bar_that_fills()
    {
        var line = Running(RunStep.CheckingFilms, 120, 480);

        Assert.Equal("Checking films 120 / 480…", line.Text);
        Assert.Equal(0.25, line.Progress);
    }

    [Fact]
    public void Reading_the_watchlist_names_the_page_with_a_moving_bar()
    {
        Assert.Equal("Reading your Letterboxd watchlist…", Running(RunStep.ReadingWatchlist, 0, null).Text);

        var line = Running(RunStep.ReadingWatchlist, 3, null);
        Assert.Equal("Reading your Letterboxd watchlist… page 3", line.Text);
        Assert.True(double.IsNaN(line.Progress!.Value));
    }

    [Fact]
    public void Fetching_services_has_a_moving_bar()
    {
        var line = Running(RunStep.FetchingServices, 0, null);

        Assert.Equal("Fetching the list of streaming services…", line.Text);
        Assert.True(double.IsNaN(line.Progress!.Value));
    }

    [Fact]
    public void Refreshing_offers_no_retry()
    {
        Assert.False(Running(RunStep.CheckingFilms, 1, 2).OfferRetry);
    }

    // ── Failed ──────────────────────────────────────────────────────────────────

    private const string Offline = "Couldn't reach Letterboxd. Check your internet connection and try again.";

    [Fact]
    public void A_failure_says_why_and_which_results_are_still_shown_and_offers_retry()
    {
        var shown = Shown(Local(11, 20, 19, 46));

        var line = StatusText.For(new RefreshState.Failed(Offline, Now, shown.RanAt), shown, true, Now, Sydneyish);

        Assert.Equal("Last refresh failed: Couldn't reach Letterboxd. Check your internet connection and try again. Showing results from 20 Nov.", line.Text);
        Assert.True(line.OfferRetry);
    }

    [Fact]
    public void A_failure_with_no_results_yet_just_says_why()
    {
        var line = StatusText.For(new RefreshState.Failed(Offline, Now, null), null, true, Now, Sydneyish);

        Assert.Equal($"Last refresh failed: {Offline}", line.Text);
    }

    [Fact]
    public void A_failure_that_needs_a_setting_points_to_settings_instead_of_retry()
    {
        var line = StatusText.For(new RefreshState.Failed("No Letterboxd username is set yet. Add yours in Settings.", Now, null), null, false, Now, Sydneyish);

        Assert.True(line.PointToSettings);
        Assert.False(line.OfferRetry);
    }

    // ── Never run ───────────────────────────────────────────────────────────────

    [Fact]
    public void With_no_results_and_no_username_it_points_to_settings()
    {
        var line = Idle(null, usernameSet: false);

        Assert.Equal("No results yet. Set your Letterboxd username in Settings.", line.Text);
        Assert.True(line.PointToSettings);
    }

    [Fact]
    public void With_no_results_but_a_username_it_suggests_refreshing()
    {
        Assert.Equal("No results yet. Press Refresh to check your watchlist.", Idle(null).Text);
    }
}
