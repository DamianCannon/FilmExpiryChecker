using FilmExpiryChecker.JustWatch;
using FilmExpiryChecker.Letterboxd;
using FilmExpiryChecker.Reporting;

namespace FilmExpiryChecker.Tests;

/// <summary>How each watchlist film is grouped, given its offers, my services and the "leaving soon" threshold.</summary>
public class ReportBuilderSpecs
{
    private static readonly DateTime Now = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private const int ThresholdDays = 60;

    // 8 and 9 are mine; 2 and 337 are other services; 999 is not in JustWatch's provider list.
    private const int Netflix = 8, Prime = 9, AppleTv = 2, Disney = 337, Unlisted = 999;

    private static readonly Package[] Providers =
    {
        new() { PackageId = Netflix, ClearName = "Netflix" },
        new() { PackageId = Prime, ClearName = "Amazon Prime Video" },
        new() { PackageId = AppleTv, ClearName = "Apple TV" },
        new() { PackageId = Disney, ClearName = "Disney Plus" },
    };

    private static ReportBuilder Builder() =>
        new(new[] { "8", "9" }, Providers, ThresholdDays, new FixedClock(new DateTimeOffset(Now)));

    private static WatchlistFilm Film(string title, string id = "1") =>
        new() { Title = title, Year = 2000, Slug = title.ToLowerInvariant().Replace(' ', '-'), FilmId = id };

    private static Offer On(int packageId, DateTime? until = null, string type = "FLATRATE") => new()
    {
        MonetizationType = type,
        Package = new OfferPackage { PackageId = packageId },
        AvailableTo = until?.ToString("o"),
    };

    private static FilmResult One(params Offer[] offers) => One(offers.ToList());

    private static FilmResult One(List<Offer>? offers) =>
        Builder().Build(new List<WatchlistFilm> { Film("Solo") }, _ => offers).Single();

    // ── Which group ─────────────────────────────────────────────────────────────

    [Fact]
    public void A_film_JustWatch_could_not_match_is_not_found()
    {
        Assert.Equal(FilmCategory.NotFoundOnJustWatch, One((List<Offer>?)null).Category);
    }

    [Fact]
    public void A_film_matched_with_no_offers_is_on_no_service()
    {
        Assert.Equal(FilmCategory.NotOnAnyService, One(new List<Offer>()).Category);
    }

    [Fact]
    public void A_film_leaving_one_of_my_services_within_the_threshold_is_leaving_soon_on_mine()
    {
        var result = One(On(Netflix, Now.AddDays(20)), On(Prime, Now.AddDays(10)));

        Assert.Equal(FilmCategory.LeavingSoonMyServices, result.Category);
    }

    [Fact]
    public void Leaving_soon_on_mine_reports_the_earliest_date_across_my_services()
    {
        var result = One(On(Netflix, Now.AddDays(20)), On(Prime, Now.AddDays(10)));

        Assert.Equal(Now.AddDays(10), result.EarliestExpiry);
    }

    [Fact]
    public void A_film_leaving_only_another_service_is_leaving_soon_elsewhere()
    {
        var result = One(On(AppleTv, Now.AddDays(5)));

        Assert.Equal(FilmCategory.LeavingSoonOtherServices, result.Category);
        Assert.Equal(Now.AddDays(5), result.EarliestExpiry);
    }

    [Fact]
    public void A_film_staying_on_mine_but_leaving_elsewhere_is_leaving_soon_elsewhere()
    {
        // Current behaviour, pinned: a leaving-soon offer elsewhere outranks a permanent one on my service.
        var result = One(On(Netflix), On(AppleTv, Now.AddDays(5)));

        Assert.Equal(FilmCategory.LeavingSoonOtherServices, result.Category);
    }

    [Fact]
    public void A_film_on_my_service_with_no_end_date_is_available_on_mine()
    {
        var result = One(On(Netflix));

        Assert.Equal(FilmCategory.AvailableMyServices, result.Category);
        Assert.Equal("Netflix", result.MyServicesDisplay);
    }

    [Fact]
    public void A_film_only_on_other_services_is_available_elsewhere()
    {
        var result = One(On(Disney));

        Assert.Equal(FilmCategory.AvailableOtherServices, result.Category);
        Assert.Equal("Disney Plus", result.OtherServicesDisplay);
    }

    [Fact]
    public void Rent_and_buy_offers_count_as_available()
    {
        // Current behaviour, pinned: the monetisation type isn't looked at.
        Assert.Equal(FilmCategory.AvailableOtherServices, One(On(AppleTv, type: "RENT")).Category);
    }

    // ── The threshold ───────────────────────────────────────────────────────────

    [Fact]
    public void Leaving_exactly_at_the_threshold_counts_as_leaving_soon()
    {
        Assert.Equal(FilmCategory.LeavingSoonMyServices, One(On(Netflix, Now.AddDays(ThresholdDays))).Category);
    }

    [Fact]
    public void Leaving_a_minute_after_the_threshold_counts_as_available()
    {
        var result = One(On(Netflix, Now.AddDays(ThresholdDays).AddMinutes(1)));

        Assert.Equal(FilmCategory.AvailableMyServices, result.Category);
        Assert.Null(result.EarliestExpiry);
    }

    // ── Several offers from one service ─────────────────────────────────────────

    [Fact]
    public void Of_several_dated_offers_from_one_service_the_earliest_is_kept()
    {
        var result = One(On(Netflix, Now.AddDays(90)), On(Netflix, Now.AddDays(10)));

        Assert.Equal(FilmCategory.LeavingSoonMyServices, result.Category);
        Assert.Single(result.MyServiceOffers);
        Assert.Equal(Now.AddDays(10), result.MyServiceOffers[0].ExpiryDate);
    }

    [Fact]
    public void A_dated_offer_outranks_a_permanent_one_from_the_same_service()
    {
        // Current behaviour, pinned: the service is treated as leaving even though another offer stays.
        var result = One(On(Netflix), On(Netflix, Now.AddDays(10)));

        Assert.Equal(FilmCategory.LeavingSoonMyServices, result.Category);
    }

    // ── Services JustWatch doesn't list ─────────────────────────────────────────

    [Fact]
    public void An_offer_from_an_unlisted_service_is_ignored()
    {
        Assert.Equal(FilmCategory.NotOnAnyService, One(On(Unlisted)).Category);
    }

    [Fact]
    public void An_unlisted_service_is_left_out_of_the_other_services()
    {
        var result = One(On(Netflix), On(Unlisted, Now.AddDays(3)));

        Assert.Equal(FilmCategory.AvailableMyServices, result.Category);
        Assert.Empty(result.OtherServiceOffers);
    }

    // ── Order ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Results_run_by_group_then_leaving_soon_by_date_and_the_rest_by_title()
    {
        var offers = new Dictionary<string, List<Offer>?>
        {
            ["Lost Film"] = null,
            ["Zebra Crossing"] = new() { On(Netflix) },
            ["Apple Orchard"] = new() { On(Netflix) },
            ["Leaves Later"] = new() { On(Netflix, Now.AddDays(30)) },
            ["Leaves First"] = new() { On(Prime, Now.AddDays(5)) },
            ["Elsewhere"] = new() { On(Disney) },
        };
        var films = offers.Keys.Select((t, i) => Film(t, i.ToString())).ToList();

        var titles = Builder().Build(films, f => offers[f.Title]).Select(r => r.Film.Title);

        Assert.Equal(
            new[] { "Leaves First", "Leaves Later", "Apple Orchard", "Zebra Crossing", "Elsewhere", "Lost Film" },
            titles);
    }
}
