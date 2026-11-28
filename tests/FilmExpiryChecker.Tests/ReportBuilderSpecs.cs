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
    public void A_film_staying_on_mine_but_leaving_a_service_I_dont_have_is_available_on_mine()
    {
        // Nothing to lose: it stays on Netflix whatever Apple TV does (#41).
        var result = One(On(Netflix), On(AppleTv, Now.AddDays(5)));

        Assert.Equal(FilmCategory.AvailableMyServices, result.Category);
        Assert.Null(result.EarliestExpiry);
    }

    [Fact]
    public void Leaving_one_of_mine_is_flagged_even_while_staying_on_another_of_mine()
    {
        var result = One(On(Netflix, Now.AddDays(10)), On(Prime));

        Assert.Equal(FilmCategory.LeavingSoonMyServices, result.Category);
        Assert.Equal(Now.AddDays(10), result.EarliestExpiry);
    }

    [Fact]
    public void Leaving_one_of_mine_is_flagged_even_while_staying_elsewhere()
    {
        Assert.Equal(FilmCategory.LeavingSoonMyServices, One(On(Netflix, Now.AddDays(10)), On(Disney)).Category);
    }

    [Fact]
    public void Leaving_one_service_I_dont_have_while_staying_on_another_is_available_elsewhere()
    {
        var result = One(On(AppleTv, Now.AddDays(5)), On(Disney));

        Assert.Equal(FilmCategory.AvailableOtherServices, result.Category);
        Assert.Null(result.EarliestExpiry);
    }

    [Fact]
    public void Leaving_every_service_I_dont_have_is_leaving_elsewhere_dated_when_the_last_goes()
    {
        var result = One(On(AppleTv, Now.AddDays(5)), On(Disney, Now.AddDays(20)));

        Assert.Equal(FilmCategory.LeavingSoonOtherServices, result.Category);
        Assert.Equal(Now.AddDays(20), result.EarliestExpiry);
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

    // ── How it can be watched (#20) ─────────────────────────────────────────────

    [Theory]
    [InlineData("FLATRATE", OfferKind.Subscription)]
    [InlineData("FREE", OfferKind.Free)]
    [InlineData("ADS", OfferKind.Free)]
    [InlineData("FAST", OfferKind.Free)]
    [InlineData("RENT", OfferKind.RentOrBuy)]
    [InlineData("BUY", OfferKind.RentOrBuy)]
    [InlineData("CINEMA", OfferKind.Cinema)]
    [InlineData("flatrate", OfferKind.Subscription)]
    [InlineData("SOMETHING_NEW", OfferKind.Unknown)]
    [InlineData(null, OfferKind.Unknown)]
    public void Each_monetisation_type_is_a_kind(string? type, OfferKind expected) =>
        Assert.Equal(expected, OfferKinds.Of(type));

    [Fact]
    public void A_film_that_can_only_be_rented_or_bought_is_in_its_own_group()
    {
        var result = One(On(AppleTv, type: "RENT"), On(Disney, type: "BUY"));

        Assert.Equal(FilmCategory.RentOrBuyOnly, result.Category);
        Assert.Equal("Apple TV, Disney Plus", result.RentOrBuyDisplay);
    }

    [Fact]
    public void A_film_on_a_subscription_elsewhere_and_for_rent_is_available_elsewhere()
    {
        var result = One(On(Disney), On(AppleTv, type: "RENT"));

        Assert.Equal(FilmCategory.AvailableOtherServices, result.Category);
        Assert.Equal("Disney Plus", result.OtherServicesDisplay);
    }

    [Fact]
    public void A_rent_offer_ending_soon_does_not_make_a_film_leaving_soon()
    {
        var result = One(On(Netflix), On(AppleTv, Now.AddDays(5), "RENT"));

        Assert.Equal(FilmCategory.AvailableMyServices, result.Category);
        Assert.Null(result.EarliestExpiry);
    }

    [Fact]
    public void A_free_service_counts_as_mine_and_says_free()
    {
        var result = One(On(Disney, type: "ADS"));

        Assert.Equal(FilmCategory.AvailableMyServices, result.Category);
        Assert.Equal(OfferKind.Free, result.MyServiceOffers.Single().Kind);
        Assert.Equal("Disney Plus (free)", result.MyServicesDisplay);
    }

    [Fact]
    public void A_free_offer_leaving_soon_is_leaving_soon_on_mine()
    {
        Assert.Equal(FilmCategory.LeavingSoonMyServices, One(On(Disney, Now.AddDays(10), "FREE")).Category);
    }

    [Fact]
    public void Only_a_cinema_showing_is_on_no_streaming_service()
    {
        Assert.Equal(FilmCategory.NotOnAnyService, One(On(AppleTv, type: "CINEMA")).Category);
    }

    [Fact]
    public void An_unknown_kind_is_ignored_and_reported_once()
    {
        var reported = new List<string>();
        var builder = new ReportBuilder(new[] { "8" }, Providers, ThresholdDays, new FixedClock(new DateTimeOffset(Now)), reported.Add);

        var results = builder.Build(
            [Film("One", "1"), Film("Two", "2")],
            _ => [On(Netflix, type: "SOMETHING_NEW")]);

        Assert.All(results, r => Assert.Equal(FilmCategory.NotOnAnyService, r.Category));
        Assert.Equal(new[] { "SOMETHING_NEW" }, reported);
    }

    [Fact]
    public void How_to_watch_names_each_kind_once()
    {
        var result = One(On(Netflix), On(Disney, type: "ADS"), On(AppleTv));

        Assert.Equal("subscription, free", result.HowToWatchDisplay);
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

    [Fact]
    public void Leaving_another_service_a_minute_after_the_threshold_counts_as_available_elsewhere()
    {
        var result = One(On(Disney, Now.AddDays(ThresholdDays).AddMinutes(1)));

        Assert.Equal(FilmCategory.AvailableOtherServices, result.Category);
        Assert.Null(result.EarliestExpiry);
    }

    // ── Several offers from one service ─────────────────────────────────────────

    [Fact]
    public void Of_several_dated_listings_on_one_service_the_last_decides()
    {
        // The service keeps it until its last listing ends: 90 days, beyond the 60-day window (#41).
        var result = One(On(Netflix, Now.AddDays(90)), On(Netflix, Now.AddDays(10)));

        Assert.Equal(FilmCategory.AvailableMyServices, result.Category);
        Assert.Single(result.MyServiceOffers);
        Assert.Equal(Now.AddDays(90), result.MyServiceOffers[0].ExpiryDate);
    }

    [Fact]
    public void A_service_with_a_permanent_listing_keeps_the_film_whatever_its_dated_one_says()
    {
        var result = One(On(Netflix), On(Netflix, Now.AddDays(10)));

        Assert.Equal(FilmCategory.AvailableMyServices, result.Category);
        Assert.Null(result.MyServiceOffers.Single().ExpiryDate);
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
