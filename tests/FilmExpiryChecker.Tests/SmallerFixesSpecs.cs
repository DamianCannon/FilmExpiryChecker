using System.Net;
using Bunit;
using FilmExpiryChecker.JustWatch;
using FilmExpiryChecker.Letterboxd;
using FilmExpiryChecker.Reporting;
using FilmExpiryChecker.Storage;
using FilmExpiryChecker.Web.Components.Results;
using FilmExpiryChecker.Web.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace FilmExpiryChecker.Tests;

/// <summary>The smaller fixes from the second-opinion review (#49).</summary>
public sealed class SmallerFixesSpecs : IDisposable
{
    private readonly TempFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    private DataFolder Data => new(_temp.Path);

    // ── Only for this PC ────────────────────────────────────────────────────────

    private static async Task<HttpResponseMessage> GetAs(TestApp app, string host)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.Host = host;
        return await app.CreateClient().SendAsync(request);
    }

    [Fact]
    public async Task A_page_asked_for_under_another_name_isnt_served()
    {
        using var app = new TestApp(dataRoot: _temp.Path);

        Assert.Equal(HttpStatusCode.BadRequest, (await GetAs(app, "films.attacker.example")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await GetAs(app, "127.0.0.1:5187")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await GetAs(app, "localhost:5187")).StatusCode);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/settings")]   // has a form, so antiforgery is at work
    public async Task Pages_say_they_mustnt_be_shown_inside_another_sites_frame(string page)
    {
        using var app = new TestApp(dataRoot: _temp.Path);

        var response = await app.CreateClient().GetAsync(page);

        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.Contains("frame-ancestors 'none'", response.Headers.GetValues("Content-Security-Policy").Single());
    }

    // ── The command line's report ───────────────────────────────────────────────

    [Fact]
    public void In_the_command_lines_report_rent_or_buy_only_has_its_own_style()
    {
        FilmResult Film(string title, FilmCategory category) =>
            new() { Film = new WatchlistFilm { Title = title, FilmId = title }, Category = category };

        var html = File.ReadAllText(new HtmlReporter().Write(
            [Film("Rented", FilmCategory.RentOrBuyOnly), Film("Gone", FilmCategory.NotOnAnyService)], _temp.Path));

        var rent = System.Text.RegularExpressions.Regex.Match(html, "<h2 class=\"(\\w+)\">💷").Groups[1].Value;
        var gone = System.Text.RegularExpressions.Regex.Match(html, "<h2 class=\"(\\w+)\">❌").Groups[1].Value;
        Assert.NotEqual(gone, rent);
        Assert.Contains($"h2.{rent} {{", html);
    }

    // ── Screen readers ──────────────────────────────────────────────────────────

    [Fact]
    public void Group_headings_read_as_words_with_their_emoji_hidden()
    {
        using var context = new BunitContext();
        context.Services.AddSingleton<TimeProvider>(TimeProvider.System);
        var run = new SavedRun
        {
            RanAt = DateTimeOffset.UtcNow, MyServices = { "8" }, ExpiringSoonDays = 60,
            Services = { new Package { PackageId = 8, ClearName = "Netflix" } },
            Films = { new SavedFilm { Film = new WatchlistFilm { Title = "The Film", FilmId = "1" }, Offers = [] } },
        };

        var heading = context.Render<ResultsView>(p => p.Add(v => v.Run, run)).Find("section.category h2");

        var icon = heading.QuerySelector(".group-icon")!;
        Assert.Equal("true", icon.GetAttribute("aria-hidden"));
        Assert.Equal("❌", icon.TextContent);
        Assert.StartsWith("Not on any streaming service", heading.TextContent.Replace(icon.TextContent, "").Trim());
    }

    // ── Housekeeping ────────────────────────────────────────────────────────────

    private string OldLog(string name, int daysOld)
    {
        Directory.CreateDirectory(Data.LogsFolder);
        var path = Path.Combine(Data.LogsFolder, name);
        File.WriteAllText(path, "a line");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-daysOld));
        return path;
    }

    [Fact]
    public async Task Starting_the_app_deletes_logs_over_30_days_old_and_keeps_the_rest()
    {
        Data.Prepare([]);
        var old = OldLog("app-2026-09-01.log", 31);
        var oldOutput = OldLog("app-output-2026-09-01-0900.log", 45);
        var recent = OldLog("launcher-2026-11-01.log", 29);

        using var app = new TestApp(dataRoot: _temp.Path);
        await app.CreateClient().GetAsync("/");

        Assert.False(File.Exists(old));
        Assert.False(File.Exists(oldOutput));
        Assert.True(File.Exists(recent));
    }

    [Fact]
    public void A_log_still_in_use_is_left_for_next_time()
    {
        var old = OldLog("launcher-2026-09-01.log", 40);
        using (File.Open(old, FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.Empty(LogFiles.Prune(Data, DateTimeOffset.UtcNow));

        Assert.Equal(["launcher-2026-09-01.log"], LogFiles.Prune(Data, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void A_run_file_another_program_has_locked_is_skipped_not_thrown()
    {
        Data.Prepare([]);
        var store = new RunStore(Data);
        var older = store.Save(new SavedRun { RanAt = DateTimeOffset.UtcNow.AddDays(-1) });
        var newer = store.Save(new SavedRun { RanAt = DateTimeOffset.UtcNow });

        using var locked = File.Open(Path.Combine(Data.RunsFolder, newer.Id + ".json"), FileMode.Open, FileAccess.Read, FileShare.None);

        Assert.Null(store.Load(newer.Id));
        Assert.Equal(older.Id, store.Latest()?.Id);
    }

    [Fact]
    public async Task A_countrys_services_that_couldnt_be_fetched_are_tried_again_next_time()
    {
        var up = false;
        var http = new FakeHttp(_ => up
            ? FakeHttp.Json("""{"data":{"packages":[{"id":"p8","packageId":8,"clearName":"Netflix","technicalName":"netflix","icon":""}]}}""")
            : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        Data.Prepare([]);
        var catalog = new ServiceCatalog(new HttpClient(http), Data, NullLoggerFactory.Instance);

        var whileDown = await catalog.ServicesIn("GB");
        up = true;
        var afterwards = await catalog.ServicesIn("GB");

        Assert.Empty(whileDown);
        Assert.Equal(["Netflix"], afterwards.Select(s => s.Name));
    }
}
