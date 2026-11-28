using System.Net;
using Bunit;
using FilmExpiryChecker.JustWatch;
using FilmExpiryChecker.Letterboxd;
using FilmExpiryChecker.Storage;
using FilmExpiryChecker.Web.Components.Pages;
using FilmExpiryChecker.Web.History;
using FilmExpiryChecker.Web.Refresh;
using FilmExpiryChecker.Web.Settings;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace FilmExpiryChecker.Tests;

/// <summary>Past runs: listed newest first, opened, downloaded as CSV and deleted; and the old reports.</summary>
public sealed class HistorySpecs : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 26, 9, 0, 0, TimeSpan.Zero);
    private const int Netflix = 8, Disney = 337;

    private readonly TempFolder _temp = new();
    private readonly DataFolder _data;
    private readonly BunitContext _context = new();
    private readonly LatestResults _latest;

    public HistorySpecs()
    {
        _data = new DataFolder(_temp.Path);
        _data.Prepare([]);
        var settings = SettingsStore.Load(_data.SettingsFile);
        settings.Letterboxd.Username = "example-user";
        settings.JustWatch.MyServices = ["8"];
        SettingsStore.Save(_data.SettingsFile, settings);

        _latest = new LatestResults(_data);
        var clock = new FixedClock(Now);
        var current = new CurrentSettings(_data);
        _context.Services.AddSingleton(_data);
        _context.Services.AddSingleton(current);
        _context.Services.AddSingleton(_latest);
        _context.Services.AddSingleton<TimeProvider>(clock);
        _context.Services.AddSingleton(new RunHistory(_data, current, _latest, clock));
    }

    public void Dispose()
    {
        _context.Dispose();
        _temp.Dispose();
    }

    private static Offer On(int packageId, DateTimeOffset? until = null) => new()
    {
        MonetizationType = "FLATRATE",
        Package = new OfferPackage { PackageId = packageId },
        AvailableTo = until?.UtcDateTime.ToString("o"),
    };

    /// <summary>Saves a run of <paramref name="films"/> films, one on Netflix and leaving in 10 days, the rest on Disney.</summary>
    private SavedRun Saved(DateTimeOffset ranAt, int films) => new RunStore(_data).Save(new SavedRun
    {
        RanAt = ranAt,
        MyServices = { "8" },
        ExpiringSoonDays = 60,
        Services = { new Package { PackageId = Netflix, ClearName = "Netflix" }, new Package { PackageId = Disney, ClearName = "Disney Plus" } },
        Films = Enumerable.Range(1, films).Select(i => new SavedFilm
        {
            Film = new WatchlistFilm { Title = $"Film {i}", Year = 2000 + i, Slug = $"film-{i}", FilmId = i.ToString() },
            Offers = i == 1 ? [On(Netflix, Now.AddDays(10))] : [On(Disney)],   // leaving 10 days from "today"
        }).ToList(),
    });

    // ── The list ────────────────────────────────────────────────────────────────

    [Fact]
    public void Runs_are_listed_newest_first_with_their_counts()
    {
        Saved(new DateTimeOffset(2026, 9, 20, 18, 17, 0, TimeSpan.Zero), films: 3);
        Saved(new DateTimeOffset(2026, 11, 26, 8, 8, 0, TimeSpan.Zero), films: 5);
        Saved(new DateTimeOffset(2026, 10, 17, 18, 58, 0, TimeSpan.Zero), films: 4);

        var rows = _context.Render<History>().FindAll("tbody tr")
            .Select(r => r.QuerySelectorAll("td").Take(4).Select(c => c.TextContent.Trim()).ToArray()).ToList();

        Assert.Equal(3, rows.Count);
        Assert.Equal(new[] { "26 Nov 2026, 08:08", "5", "1", "1" }, rows[0]);
        Assert.Equal(new[] { "17 Oct 2026, 18:58", "4", "1", "1" }, rows[1]);
        Assert.Equal(new[] { "20 Sep 2026, 18:17", "3", "1", "1" }, rows[2]);
    }

    [Fact]
    public void Each_run_links_to_its_page_and_its_CSV()
    {
        var run = Saved(Now, films: 2);

        var row = _context.Render<History>().Find("tbody tr");

        Assert.Equal($"history/{run.Id}", row.QuerySelector("td a")!.GetAttribute("href"));
        Assert.Equal($"download/{run.Id}.csv", row.QuerySelectorAll("a").Single(a => a.TextContent == "CSV").GetAttribute("href"));
    }

    // ── Deleting ────────────────────────────────────────────────────────────────

    [Fact]
    public void Delete_asks_first_and_keep_changes_nothing()
    {
        var run = Saved(Now, films: 2);
        var page = _context.Render<History>();

        page.FindAll("button").Single(b => b.TextContent == "Delete").Click();
        Assert.Contains("Delete this run?", page.Markup);
        page.FindAll("button").Single(b => b.TextContent == "Keep").Click();

        Assert.NotNull(new RunStore(_data).Load(run.Id));
        Assert.DoesNotContain("Delete this run?", page.Markup);
    }

    [Fact]
    public void Deleting_a_run_removes_its_file_and_row_and_the_one_before_becomes_the_latest()
    {
        var older = Saved(Now.AddDays(-7), films: 2);
        var newer = Saved(Now, films: 3);
        Assert.Equal(newer.Id, _latest.Run!.Id);
        var page = _context.Render<History>();

        page.FindAll("tbody tr")[0].QuerySelectorAll("button").Single(b => b.TextContent == "Delete").Click();
        var confirmRow = page.FindAll("tbody tr")[0];
        Assert.Contains("Delete this run?", confirmRow.TextContent);
        confirmRow.QuerySelectorAll("button").Single(b => b.TextContent == "Delete").Click();

        Assert.Null(new RunStore(_data).Load(newer.Id));
        Assert.Single(page.FindAll("tbody tr"));
        Assert.Equal(older.Id, _latest.Run!.Id);
        Assert.Equal(older.Id, new RunStore(_data).Latest()!.Id);
    }

    // ── A past run ──────────────────────────────────────────────────────────────

    [Fact]
    public void A_past_run_opens_with_its_date_and_its_results()
    {
        var run = Saved(new DateTimeOffset(2026, 9, 20, 18, 17, 0, TimeSpan.Zero), films: 3);

        var page = _context.Render<PastRun>(p => p.Add(r => r.Id, run.Id));

        Assert.Equal("Results from 20 Sep 2026", page.Find("h1").TextContent);
        Assert.Equal(3, page.FindAll(".title").Count);
    }

    [Fact]
    public void A_run_that_isnt_there_says_so()
    {
        var page = _context.Render<PastRun>(p => p.Add(r => r.Id, "2020-01-01_000000"));

        Assert.Equal("Not found", page.Find("h1").TextContent);
    }

    // ── The old reports ─────────────────────────────────────────────────────────

    [Fact]
    public void The_command_lines_old_HTML_reports_are_listed_newest_first_and_open_in_a_new_tab()
    {
        File.WriteAllText(Path.Combine(_data.ReportsFolder, "film-expiry-2026-05-30_1441.html"), "<html></html>");
        File.SetLastWriteTime(Path.Combine(_data.ReportsFolder, "film-expiry-2026-05-30_1441.html"), new DateTime(2026, 5, 30, 14, 41, 0));
        File.WriteAllText(Path.Combine(_data.ReportsFolder, "film-expiry-2026-11-16_1946.html"), "<html></html>");
        File.SetLastWriteTime(Path.Combine(_data.ReportsFolder, "film-expiry-2026-11-16_1946.html"), new DateTime(2026, 11, 16, 19, 46, 0));
        File.WriteAllText(Path.Combine(_data.ReportsFolder, "film-expiry-2026-11-16_1946.csv"), "csv");

        var links = _context.Render<History>().FindAll(".old-reports a");

        Assert.Equal(new[] { "16 Nov 2026, 19:46", "30 May 2026, 14:41" }, links.Select(a => a.TextContent));
        Assert.Equal("reports/film-expiry-2026-11-16_1946.html", links[0].GetAttribute("href"));
        Assert.All(links, a => Assert.Equal("_blank", a.GetAttribute("target")));
    }
}

/// <summary>The download addresses, through the real app.</summary>
public sealed class DownloadSpecs : IDisposable
{
    private readonly TempFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    private (TestApp App, SavedRun Run) AppWithRun(int films)
    {
        var app = new TestApp(dataRoot: _temp.Path).WithUser();
        var run = new RunStore(app.Data).Save(new SavedRun
        {
            RanAt = DateTimeOffset.UtcNow,
            MyServices = { "8" },
            ExpiringSoonDays = 60,
            Services = { new Package { PackageId = 8, ClearName = "Netflix" } },
            Films = Enumerable.Range(1, films).Select(i => new SavedFilm
            {
                Film = new WatchlistFilm { Title = $"Film, number {i}", Year = 2000, Slug = $"film-{i}", FilmId = i.ToString() },
                Offers = [new Offer { MonetizationType = "FLATRATE", Package = new OfferPackage { PackageId = 8 } }],
            }).ToList(),
        });
        return (app, run);
    }

    [Fact]
    public async Task A_runs_CSV_has_the_command_lines_columns_and_a_row_per_film()
    {
        var (app, run) = AppWithRun(films: 4);
        using var _ = app;

        var response = await app.CreateClient().GetAsync($"/download/{run.Id}.csv");
        var lines = (await response.Content.ReadAsStringAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal("text/csv", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal($"film-expiry-{run.Id}.csv", response.Content.Headers.ContentDisposition!.FileName);
        Assert.Equal("Title,Year,Category,MyServices,OtherServices,ExpiryDate,DaysRemaining,HowToWatch,RentOrBuy", lines[0].Trim());
        Assert.Equal(5, lines.Length);
        Assert.StartsWith("\"Film, number 1\",2000,Available on your services,Netflix", lines[1]);
    }

    [Fact]
    public async Task The_latest_results_download_as_CSV_too()
    {
        var (app, _) = AppWithRun(films: 2);
        using var __ = app;

        var csv = await app.CreateClient().GetStringAsync("/download/latest.csv");

        Assert.Equal(3, csv.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
    }

    [Fact]
    public async Task An_unknown_run_is_not_found()
    {
        var (app, _) = AppWithRun(films: 1);
        using var __ = app;

        Assert.Equal(HttpStatusCode.NotFound, (await app.CreateClient().GetAsync("/download/2020-01-01_000000.csv")).StatusCode);
    }

    [Fact]
    public async Task An_old_report_is_served_from_the_reports_folder()
    {
        var (app, _) = AppWithRun(films: 1);
        using var __ = app;
        File.WriteAllText(Path.Combine(app.Data.ReportsFolder, "film-expiry-2026-05-30_1441.html"), "<html>old report</html>");

        var html = await app.CreateClient().GetStringAsync("/reports/film-expiry-2026-05-30_1441.html");

        Assert.Equal("<html>old report</html>", html);
    }

    [Theory]
    [InlineData("/reports/..%2Fsettings.json")]
    [InlineData("/reports/settings.json")]
    [InlineData("/reports/..%5Csettings.json")]
    public async Task Nothing_outside_the_old_reports_is_served(string path)
    {
        var (app, _) = AppWithRun(films: 1);
        using var __ = app;

        var response = await app.CreateClient().GetAsync(path);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain("Letterboxd", await response.Content.ReadAsStringAsync());
    }
}
