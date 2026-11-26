using Bunit;
using FilmExpiryChecker.JustWatch;
using FilmExpiryChecker.Letterboxd;
using FilmExpiryChecker.Storage;
using FilmExpiryChecker.Web.Components.Pages;
using FilmExpiryChecker.Web.Components.Results;
using FilmExpiryChecker.Web.Refresh;
using Microsoft.Extensions.DependencyInjection;

namespace FilmExpiryChecker.Tests;

/// <summary>The results page: the last saved run, at once, grouped and coloured as the report always was.</summary>
public sealed class ResultsPageSpecs : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 26, 0, 0, 0, TimeSpan.Zero);
    private const int Netflix = 8, Disney = 337;

    private readonly TempFolder _temp = new();
    private readonly BunitContext _context = new();
    private readonly DataFolder _data;

    public ResultsPageSpecs()
    {
        _data = new DataFolder(_temp.Path);
        _data.Prepare([]);
        var settings = SettingsStore.Load(_data.SettingsFile);
        settings.Letterboxd.Username = "example-user";
        SettingsStore.Save(_data.SettingsFile, settings);
        _context.Services.AddSingleton(_data);
        _context.Services.AddSingleton(new FilmExpiryChecker.Web.Settings.CurrentSettings(_data));
        _context.Services.AddSingleton<TimeProvider>(new FixedClock(Now));
        _context.Services.AddSingleton<IRefreshStatus>(new FakeRefreshStatus());
    }

    public void Dispose()
    {
        _context.Dispose();
        _temp.Dispose();
    }

    private static SavedFilm Film(string title, params Offer[] offers) => new()
    {
        Film = new WatchlistFilm { Title = title, Year = 2000, Slug = title.ToLowerInvariant().Replace(' ', '-'), FilmId = title },
        Offers = offers.ToList(),
    };

    private static Offer On(int packageId, DateTimeOffset? until = null) => new()
    {
        MonetizationType = "FLATRATE",
        Package = new OfferPackage { PackageId = packageId },
        AvailableTo = until?.UtcDateTime.ToString("o"),
    };

    private static SavedRun Run(DateTimeOffset ranAt, params SavedFilm[] films) => new()
    {
        Id = ranAt.ToString("yyyy-MM-dd_HHmmss"),
        RanAt = ranAt,
        MyServices = { "8" },
        ExpiringSoonDays = 60,
        Services = { new Package { PackageId = Netflix, ClearName = "Netflix" }, new Package { PackageId = Disney, ClearName = "Disney Plus" } },
        Films = films.ToList(),
    };

    private IRenderedComponent<ResultsView> View(SavedRun run) =>
        _context.Render<ResultsView>(p => p.Add(v => v.Run, run));

    private static string[] Cells(IRenderedComponent<ResultsView> view, string title, int column) =>
        view.FindAll("tr").Where(r => r.QuerySelector(".title")?.TextContent == title)
            .Select(r => r.QuerySelectorAll("td")[column].ClassName ?? "").ToArray();

    // ── Shown at once ───────────────────────────────────────────────────────────

    [Fact]
    public async Task The_saved_results_are_on_the_page_at_once_even_while_a_refresh_runs()
    {
        var data = new TempFolder();
        try
        {
            new DataFolder(data.Path).Prepare([]);
            new RunStore(new DataFolder(data.Path)).Save(Run(Now, Film("High and Low", On(Netflix)), Film("Socrates", On(Disney))));
            // Every Letterboxd request hangs, so the refresh started below never finishes during the test.
            var hanging = new FakeHttp((_, ct) => { ct.WaitHandle.WaitOne(); throw new TaskCanceledException(); });
            using var app = new TestApp(hanging, data.Path).WithUser();
            var client = app.CreateClient();

            Assert.True(app.Services.GetRequiredService<RefreshService>().RequestRefresh());
            var html = await client.GetStringAsync("/");

            Assert.Contains("High and Low", html);
            Assert.Contains("Socrates", html);
            Assert.DoesNotContain("No results yet", html);
            Assert.IsType<RefreshState.Running>(app.Services.GetRequiredService<RefreshService>().State);
        }
        finally
        {
            data.Dispose();
        }
    }

    [Fact]
    public async Task New_results_replace_the_old_ones_on_the_page_when_a_refresh_finishes()
    {
        var latest = new LatestResults(_data);
        latest.Replace(Run(Now.AddDays(-1), Film("Old Film", On(Netflix))));
        _context.Services.AddSingleton(latest);
        var page = _context.Render<Home>();
        Assert.Contains("Old Film", page.Markup);

        await Task.Run(() => latest.Replace(Run(Now, Film("New Film", On(Netflix)))));

        page.WaitForAssertion(() =>
        {
            Assert.Contains("New Film", page.Markup);
            Assert.DoesNotContain("Old Film", page.Markup);
        });
    }

    [Fact]
    public void With_no_results_yet_the_page_says_how_to_get_some()
    {
        _context.Services.AddSingleton(new LatestResults(_data));

        var page = _context.Render<Home>();

        Assert.Contains("No results yet", page.Markup);
        Assert.Contains("Press <strong>Refresh</strong>", page.Markup);
    }

    // ── Order and colours ───────────────────────────────────────────────────────

    [Fact]
    public void Groups_run_in_the_reports_order()
    {
        var view = View(Run(Now,
            Film("Nowhere"),
            Film("Elsewhere", On(Disney)),
            Film("Mine", On(Netflix)),
            Film("Leaving Elsewhere", On(Disney, Now.AddDays(10))),
            Film("Leaving Mine", On(Netflix, Now.AddDays(10)))));

        var headings = view.FindAll("h2").Select(h => h.TextContent).ToList();

        Assert.Equal(5, headings.Count);
        Assert.Contains("Leaving soon — your services", headings[0]);
        Assert.Contains("Leaving soon — other services", headings[1]);
        Assert.Contains("Available on your services", headings[2]);
        Assert.Contains("Available elsewhere only", headings[3]);
        Assert.Contains("Not on any streaming service", headings[4]);
    }

    [Fact]
    public void Leaving_soon_runs_from_the_soonest()
    {
        var view = View(Run(Now,
            Film("In Thirty", On(Netflix, Now.AddDays(30))),
            Film("In Five", On(Netflix, Now.AddDays(5))),
            Film("In Twenty", On(Netflix, Now.AddDays(20)))));

        Assert.Equal(new[] { "In Five", "In Twenty", "In Thirty" }, view.FindAll(".cat1 .title").Select(t => t.TextContent));
    }

    [Theory]
    [InlineData(14, "expiry-urgent")]
    [InlineData(15, "expiry-soon")]
    [InlineData(30, "expiry-soon")]
    [InlineData(31, "expiry")]
    public void Days_left_are_coloured_by_how_soon(int days, string expected)
    {
        var view = View(Run(Now, Film("The Film", On(Netflix, Now.AddDays(days)))));

        Assert.Equal(new[] { expected }, Cells(view, "The Film", 3));
        Assert.Contains($"{days}d", view.Find(".cat1 tbody tr").TextContent);
    }

    [Fact]
    public void Days_left_count_from_today_not_from_when_the_results_were_fetched()
    {
        // Fetched ten days ago, when 30 days were left; 20 are left today.
        var view = View(Run(Now.AddDays(-10), Film("The Film", On(Netflix, Now.AddDays(20)))));

        Assert.Contains("20d", view.Find(".cat1 tbody tr").TextContent);
    }

    // ── What each row shows ─────────────────────────────────────────────────────

    [Fact]
    public void Each_group_has_the_reports_columns()
    {
        var view = View(Run(Now, Film("Leaving Mine", On(Netflix, Now.AddDays(10))), Film("Elsewhere", On(Disney))));

        Assert.Equal(new[] { "Title", "Year", "Leaving", "Days left", "Your services" }, view.FindAll(".cat1 th").Select(h => h.TextContent));
        Assert.Equal(new[] { "Title", "Year", "Service" }, view.FindAll(".cat4 th").Select(h => h.TextContent));
        Assert.Contains("Disney Plus", view.Find(".cat4 tbody tr").TextContent);
    }

    [Fact]
    public void Titles_link_to_Letterboxd_in_a_new_window()
    {
        var link = View(Run(Now, Film("High and Low", On(Netflix)))).Find(".title a");

        Assert.Equal("https://letterboxd.com/film/high-and-low/", link.GetAttribute("href"));
        Assert.Equal("_blank", link.GetAttribute("target"));
    }

    [Fact]
    public void The_summary_counts_the_films_mine_and_leaving_soon()
    {
        var view = View(Run(Now,
            Film("Mine", On(Netflix)),
            Film("Leaving Mine", On(Netflix, Now.AddDays(10))),
            Film("Leaving Elsewhere", On(Disney, Now.AddDays(10))),
            Film("Nowhere")));

        var summary = view.Find(".summary").TextContent;
        Assert.Contains("Films on your watchlist: 4", summary);
        Assert.Contains("On your services: 2", summary);
        Assert.Contains("Leaving soon: 2", summary);
    }

    [Fact]
    public void The_services_given_decide_what_counts_as_mine()
    {
        var view = _context.Render<ResultsView>(p => p
            .Add(v => v.Run, Run(Now, Film("Disney Film", On(Disney))))
            .Add(v => v.MyServices, ["337"]));

        Assert.Contains("Available on your services", view.Find("h2").TextContent);
    }
}
