using System.Globalization;
using Bunit;
using FilmExpiryChecker.JustWatch;
using FilmExpiryChecker.Letterboxd;
using FilmExpiryChecker.Reporting;
using FilmExpiryChecker.Storage;
using FilmExpiryChecker.Web.Components.Results;
using Microsoft.Extensions.DependencyInjection;

namespace FilmExpiryChecker.Tests;

/// <summary>Leaving dates as JustWatch really gives them (a date only), right in every season and never stale (#46).</summary>
public sealed class LeavingDateSpecs : IDisposable
{
    private static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("GMT Standard Time");
    private static readonly TimeZoneInfo Plus10 = TimeZoneInfo.CreateCustomTimeZone("Test+10", TimeSpan.FromHours(10), "Test+10", "Test+10");
    private const int Netflix = 8, Disney = 337;

    private readonly TempFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    private static readonly Package[] Providers =
    [
        new() { PackageId = Netflix, ClearName = "Netflix" },
        new() { PackageId = Disney, ClearName = "Disney Plus" },
    ];

    /// <summary>An offer as JustWatch sends it: <c>availableTo</c> is a bare date.</summary>
    private static Offer Until(int packageId, string availableTo) => new()
    {
        MonetizationType = "FLATRATE",
        Package = new OfferPackage { PackageId = packageId },
        AvailableTo = availableTo,
    };

    private static FilmResult Group(TimeProvider clock, params Offer[] offers) =>
        new ReportBuilder(["8"], Providers, 60, clock)
            .Build([new WatchlistFilm { Title = "The Film", FilmId = "1" }], _ => offers.ToList()).Single();

    // ── The day it leaves ───────────────────────────────────────────────────────

    [Fact]
    public void A_bare_date_is_that_day_whatever_the_machines_time_zone()
    {
        var offer = Until(Netflix, "2027-08-30");

        Assert.Equal(new DateTime(2027, 8, 30), offer.ExpiryDate!.Value);
        Assert.Equal(DateTimeKind.Utc, offer.ExpiryDate.Value.Kind);
    }

    [Fact]
    public void In_British_summer_time_the_date_shown_and_the_days_left_are_right()
    {
        // 1 Aug 2027, midday BST.
        var now = new DateTimeOffset(2027, 8, 1, 12, 0, 0, TimeSpan.FromHours(1));
        var clock = new FixedClock(now, London);

        var result = Group(clock, Until(Netflix, "2027-08-30"));

        Assert.Equal("30 Aug 2027", result.ExpiryDisplay);
        Assert.Equal(29, result.DaysLeftAt(now, London));
        Assert.Equal("Netflix (until 30 Aug 2027)", result.MyServicesDisplay);
    }

    [Fact]
    public void Ten_hours_ahead_of_UTC_the_days_left_count_the_users_own_days()
    {
        // 1 Aug 2027 at 09:00 local is still 31 Jul in UTC.
        var now = new DateTimeOffset(2027, 8, 1, 9, 0, 0, TimeSpan.FromHours(10));

        var result = Group(new FixedClock(now, Plus10), Until(Netflix, "2027-08-30"));

        Assert.Equal(29, result.DaysLeftAt(now, Plus10));
    }

    // ── Gone, and the last day ──────────────────────────────────────────────────

    private static readonly DateTimeOffset Morning = new(2026, 11, 28, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_listing_that_ended_yesterday_is_gone_not_leaving()
    {
        var result = Group(new FixedClock(Morning, London), Until(Netflix, "2026-11-27"));

        Assert.Equal(FilmCategory.NotOnAnyService, result.Category);
        Assert.Empty(result.MyServiceOffers);
    }

    [Fact]
    public void A_listing_that_ended_yesterday_doesnt_hide_one_that_carries_on()
    {
        var result = Group(new FixedClock(Morning, London), Until(Netflix, "2026-11-27"), Until(Disney, "2026-12-20"));

        Assert.Equal(FilmCategory.LeavingSoonOtherServices, result.Category);
        Assert.Equal(new DateTime(2026, 12, 20), result.EarliestExpiry);
    }

    [Fact]
    public void Leaving_today_is_its_last_day_with_zero_days_left()
    {
        var result = Group(new FixedClock(Morning, London), Until(Netflix, "2026-11-28"));

        Assert.Equal(FilmCategory.LeavingSoonMyServices, result.Category);
        Assert.Equal(0, result.DaysLeftAt(Morning, London));
    }

    [Fact]
    public void The_results_page_says_last_day_rather_than_zero_days()
    {
        using var context = new BunitContext();
        context.Services.AddSingleton<TimeProvider>(new FixedClock(Morning, London));
        var run = new SavedRun
        {
            RanAt = Morning, MyServices = { "8" }, ExpiringSoonDays = 60, Services = { Providers[0] },
            Films = { new SavedFilm { Film = new WatchlistFilm { Title = "The Film", FilmId = "1" }, Offers = [Until(Netflix, "2026-11-28")] } },
        };

        var view = context.Render<ResultsView>(p => p.Add(v => v.Run, run));

        Assert.Contains("last day", view.Find(".cat1 tbody tr").TextContent);
        Assert.DoesNotContain("0d", view.Find(".cat1 tbody tr").TextContent);
    }

    // ── Month names ─────────────────────────────────────────────────────────────

    [Fact]
    public void September_is_Sep_on_the_page_and_in_the_CSV_even_in_British_English()
    {
        var before = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-GB");
        try
        {
            var now = new DateTimeOffset(2027, 8, 20, 12, 0, 0, TimeSpan.FromHours(1));
            var result = Group(new FixedClock(now, London), Until(Netflix, "2027-09-07"));

            Assert.Equal("07 Sep 2027", result.ExpiryDisplay);
            Assert.Contains("07 Sep 2027", new CsvReporter().ToCsv([result], now, London));
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }

    // ── Never stale ─────────────────────────────────────────────────────────────

    private static readonly DateTimeOffset Cached = new(2026, 11, 20, 9, 0, 0, TimeSpan.Zero);

    private bool StillFresh(DateTimeOffset now, params Offer[] offers)
    {
        var path = _temp.File($"cache-{Guid.NewGuid():N}.json");
        var write = new JustWatchCache(path, 7, time: new FixedClock(Cached));
        write.Set("1", offers.ToList());
        write.Save();
        return new JustWatchCache(path, 7, time: new FixedClock(now), myServices: ["8"]).TryGet("1", out _);
    }

    [Fact]
    public void A_cached_film_past_one_of_its_leaving_days_is_looked_up_again() =>
        Assert.False(StillFresh(Cached.AddDays(3), Until(Disney, "2026-11-22")));

    [Fact]
    public void A_cached_film_on_one_of_my_services_is_looked_up_again_after_two_days() =>
        Assert.False(StillFresh(Cached.AddDays(2).AddMinutes(1), Until(Netflix, "2027-06-01")));

    [Fact]
    public void A_cached_film_only_elsewhere_keeps_for_the_cache_days() =>
        Assert.True(StillFresh(Cached.AddDays(3), Until(Disney, "2027-06-01")));

    [Fact]
    public void Nothing_is_kept_past_the_cache_days() =>
        Assert.False(StillFresh(Cached.AddDays(7), Until(Disney, "2027-06-01")));
}
