using FilmExpiryChecker.JustWatch;
using FilmExpiryChecker.Letterboxd;
using FilmExpiryChecker.Reporting;
using FilmExpiryChecker.Storage;

namespace FilmExpiryChecker.Tests;

/// <summary>Finished runs are kept with their raw offers, found again newest first, and can be regrouped.</summary>
public sealed class RunStoreSpecs : IDisposable
{
    private readonly TempFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    private DataFolder Data => new(_temp.Path);
    private RunStore Store => new(Data);

    private static readonly DateTimeOffset Morning = new(2026, 11, 26, 7, 12, 0, TimeSpan.Zero);
    private static readonly FixedClock Now = new(Morning);

    private const int Netflix = 8, Disney = 337;

    private static Offer On(int packageId, DateTimeOffset? until = null, string type = "FLATRATE") => new()
    {
        MonetizationType = type,
        Package = new OfferPackage { Id = "p" + packageId, PackageId = packageId },
        AvailableTo = until?.ToString("yyyy-MM-dd"),
    };

    private static SavedRun Run(DateTimeOffset ranAt) => new()
    {
        RanAt = ranAt,
        Username = "film-fan",
        Region = "GB",
        MyServices = { "8" },
        ExpiringSoonDays = 60,
        Services =
        {
            new Package { PackageId = Netflix, ClearName = "Netflix" },
            new Package { PackageId = Disney, ClearName = "Disney Plus" },
        },
        Films =
        {
            new SavedFilm { Film = new WatchlistFilm { Title = "High and Low", Year = 1963, Slug = "high-and-low", FilmId = "44542" }, Offers = [On(Netflix)] },
            new SavedFilm { Film = new WatchlistFilm { Title = "Socrates", Year = null, Slug = "socrates", FilmId = "61097" }, Offers = [On(Disney, Morning.AddDays(90)), On(Disney, type: "RENT")] },
            new SavedFilm { Film = new WatchlistFilm { Title = "Kisapmata", Year = 1981, Slug = "kisapmata", FilmId = "154091" }, Offers = null },
            new SavedFilm { Film = new WatchlistFilm { Title = "Away from Her", Year = 2006, Slug = "away-from-her", FilmId = "50686" }, Offers = [] },
        },
    };

    private static FilmCategory CategoryOf(List<FilmResult> results, string title) =>
        results.Single(r => r.Film.Title == title).Category;

    // ── Saving and loading ──────────────────────────────────────────────────────

    [Fact]
    public void A_saved_run_loads_back_with_its_films_offers_and_settings()
    {
        var saved = Store.Save(Run(Morning));

        var loaded = Store.Load(saved.Id)!;

        Assert.Equal(Morning, loaded.RanAt);
        Assert.Equal(("film-fan", "GB", 60), (loaded.Username, loaded.Region, loaded.ExpiringSoonDays));
        Assert.Equal(new[] { "8" }, loaded.MyServices);
        Assert.Equal(new[] { "Netflix", "Disney Plus" }, loaded.Services.Select(s => s.ClearName));
        Assert.Equal(new[] { "High and Low", "Socrates", "Kisapmata", "Away from Her" }, loaded.Films.Select(f => f.Film.Title));
        Assert.Null(loaded.Films.Single(f => f.Film.Slug == "socrates").Film.Year);

        var socrates = loaded.Films.Single(f => f.Film.Slug == "socrates").Offers!;
        Assert.Equal(new[] { (Disney, "FLATRATE", "2027-02-24"), (Disney, "RENT", (string?)null) },
            socrates.Select(o => (o.PackageId, o.MonetizationType, o.AvailableTo)));
    }

    [Fact]
    public void Not_found_and_no_offers_stay_distinct_after_saving()
    {
        var loaded = Store.Load(Store.Save(Run(Morning)).Id)!;

        Assert.Null(loaded.Films.Single(f => f.Film.Slug == "kisapmata").Offers);
        Assert.Empty(loaded.Films.Single(f => f.Film.Slug == "away-from-her").Offers!);
    }

    [Fact]
    public void A_run_is_named_after_the_UTC_time_it_ran()
    {
        Assert.Equal("2026-11-26_071200", Store.Save(Run(Morning)).Id);
    }

    [Fact]
    public void Two_runs_in_the_same_second_are_both_kept()
    {
        var first = Store.Save(Run(Morning));
        var second = Store.Save(Run(Morning));

        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(2, Store.List().Count);
    }

    // ── Finding runs ────────────────────────────────────────────────────────────

    [Fact]
    public void Runs_are_listed_newest_first()
    {
        Store.Save(Run(Morning.AddDays(-30)));
        Store.Save(Run(Morning));
        Store.Save(Run(Morning.AddDays(-1)));

        Assert.Equal(new[] { Morning, Morning.AddDays(-1), Morning.AddDays(-30) }, Store.List().Select(e => e.RanAt));
    }

    [Fact]
    public void The_latest_run_is_the_newest()
    {
        Store.Save(Run(Morning.AddDays(-1)));
        Store.Save(Run(Morning));

        Assert.Equal(Morning, Store.Latest()!.RanAt);
    }

    [Fact]
    public void A_half_written_newer_run_is_ignored()
    {
        var finished = Store.Save(Run(Morning.AddDays(-1)));
        File.WriteAllText(Path.Combine(Data.RunsFolder, "2026-11-26_071200.json.part"), "{\"Films\":[{\"Fi");

        Assert.Equal(finished.Id, Store.Latest()!.Id);
        Assert.Single(Store.List());
    }

    [Fact]
    public void A_newer_run_that_cannot_be_read_is_skipped()
    {
        var finished = Store.Save(Run(Morning.AddDays(-1)));
        File.WriteAllText(Path.Combine(Data.RunsFolder, "2026-11-26_071200.json"), "{ not json");

        Assert.Equal(finished.Id, Store.Latest()!.Id);
    }

    [Fact]
    public void With_no_runs_there_is_no_latest()
    {
        Assert.Null(Store.Latest());
        Assert.Empty(Store.List());
    }

    // ── Tidying up (#42) ────────────────────────────────────────────────────────

    // Ten hours ahead of UTC: local Monday 00:00 is Sunday 14:00 UTC, so the UTC and local weeks disagree.
    private static readonly TimeZoneInfo Plus10 = TimeZoneInfo.CreateCustomTimeZone("Test+10", TimeSpan.FromHours(10), "Test+10", "Test+10");

    private static DateTimeOffset Local(int month, int day, int hour, int minute = 0) =>
        new DateTimeOffset(2026, month, day, hour, minute, 0, TimeSpan.FromHours(10)).ToUniversalTime();

    // Thursday 26 Nov 2026, midday local: this week began on Monday 23 Nov.
    private static readonly DateTimeOffset Thursday = Local(11, 26, 12);

    private string[] Kept() => Store.List().Select(e => e.Id).ToArray();

    [Fact]
    public void Every_run_from_this_week_is_kept()
    {
        var ids = new[] { Local(11, 23, 8), Local(11, 25, 19), Local(11, 26, 7) }.Select(t => Store.Save(Run(t)).Id).ToList();

        var deleted = Store.Prune(Thursday, Plus10);

        Assert.Empty(deleted);
        Assert.Equal(ids.Order(), Kept().Order());
    }

    [Fact]
    public void From_each_earlier_week_only_the_newest_is_kept()
    {
        Store.Save(Run(Local(11, 16, 8)));               // last week: Monday,
        Store.Save(Run(Local(11, 20, 19)));              //            Friday,
        var sunday = Store.Save(Run(Local(11, 22, 21))); //            Sunday (the newest)
        var weekBefore = Store.Save(Run(Local(11, 10, 9)));
        var monday = Store.Save(Run(Local(11, 23, 8)));  // this week

        var deleted = Store.Prune(Thursday, Plus10);

        Assert.Equal(2, deleted.Count);
        Assert.Equal(new[] { monday.Id, sunday.Id, weekBefore.Id }, Kept());
    }

    [Fact]
    public void A_week_with_one_run_keeps_it()
    {
        var only = Store.Save(Run(Local(10, 14, 9)));

        Store.Prune(Thursday, Plus10);

        Assert.Equal(new[] { only.Id }, Kept());
    }

    [Fact]
    public void The_week_starts_at_midnight_on_Monday_in_the_users_own_time()
    {
        // 00:30 on Monday 23 Nov local is 14:30 on Sunday 22 Nov in UTC: by UTC it would be last week's newest,
        // and last week's real newest (Sunday 23:30 local) would be deleted.
        Store.Save(Run(Local(11, 21, 10)));
        var lastSunday = Store.Save(Run(Local(11, 22, 23, 30)));
        var mondayJustAfterMidnight = Store.Save(Run(Local(11, 23, 0, 30)));

        Store.Prune(Thursday, Plus10);

        Assert.Equal(new[] { mondayJustAfterMidnight.Id, lastSunday.Id }, Kept());
    }

    [Fact]
    public void A_Sunday_run_counts_as_the_end_of_its_week()
    {
        Assert.Equal(new DateTime(2026, 11, 16), RunStore.WeekStart(Local(11, 22, 23, 59), Plus10));
        Assert.Equal(new DateTime(2026, 11, 23), RunStore.WeekStart(Local(11, 23, 0, 0), Plus10));
    }

    // ── Regrouping without fetching ─────────────────────────────────────────────

    [Fact]
    public void A_saved_run_groups_with_the_settings_it_ran_with()
    {
        var results = Store.Load(Store.Save(Run(Morning)).Id)!.Group(Now);

        Assert.Equal(FilmCategory.AvailableMyServices, CategoryOf(results, "High and Low"));
        Assert.Equal(FilmCategory.AvailableOtherServices, CategoryOf(results, "Socrates"));
        Assert.Equal(FilmCategory.NotFoundOnJustWatch, CategoryOf(results, "Kisapmata"));
        Assert.Equal(FilmCategory.NotOnAnyService, CategoryOf(results, "Away from Her"));
    }

    [Fact]
    public void A_run_holding_a_film_twice_groups_it_once()
    {
        var run = Run(Morning);
        run.Films.Add(new SavedFilm { Film = run.Films[0].Film, Offers = [On(Disney)] });

        var results = run.Group(Now);

        Assert.Single(results, r => r.Film.FilmId == "44542");
        Assert.Equal(FilmCategory.AvailableMyServices, CategoryOf(results, "High and Low"));
    }

    [Fact]
    public void Regrouping_with_other_services_moves_films_between_mine_and_elsewhere()
    {
        var run = Store.Load(Store.Save(Run(Morning)).Id)!;

        var results = run.Group(myServices: ["337"], expiringSoonDays: 60, Now);

        Assert.Equal(FilmCategory.AvailableOtherServices, CategoryOf(results, "High and Low"));
        Assert.Equal(FilmCategory.AvailableMyServices, CategoryOf(results, "Socrates"));
    }

    [Fact]
    public void Regrouping_with_a_longer_threshold_brings_a_later_end_date_into_leaving_soon()
    {
        var run = Store.Load(Store.Save(Run(Morning)).Id)!;

        var results = run.Group(myServices: ["8"], expiringSoonDays: 120, Now);

        Assert.Equal(FilmCategory.LeavingSoonOtherServices, CategoryOf(results, "Socrates"));
    }
}
