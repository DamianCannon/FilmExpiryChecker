using Bunit;
using FilmExpiryChecker.Config;
using FilmExpiryChecker.JustWatch;
using FilmExpiryChecker.Letterboxd;
using FilmExpiryChecker.Reporting;
using FilmExpiryChecker.Storage;
using FilmExpiryChecker.Web.Components.Pages;
using FilmExpiryChecker.Web.Refresh;
using FilmExpiryChecker.Web.Settings;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace FilmExpiryChecker.Tests;

/// <summary>What a change of settings means for the results on screen.</summary>
public class SettingsChangeSpecs
{
    private static AppSettings Base() => new()
    {
        Letterboxd = { Username = "film-fan" },
        JustWatch = { Region = "GB", MyServices = ["8", "9"], ExpiringSoonDays = 60 },
        Cache = { ExpiryDays = 7 },
    };

    private static SettingsEffect After(Action<AppSettings> change)
    {
        var after = Base();
        change(after);
        return SettingsChange.Effect(Base(), after);
    }

    [Fact] public void Ticking_another_service_regroups() => Assert.Equal(SettingsEffect.Regroup, After(s => s.JustWatch.MyServices.Add("337")));
    [Fact] public void Unticking_a_service_regroups() => Assert.Equal(SettingsEffect.Regroup, After(s => s.JustWatch.MyServices.Remove("9")));
    [Fact] public void A_new_threshold_regroups() => Assert.Equal(SettingsEffect.Regroup, After(s => s.JustWatch.ExpiringSoonDays = 30));
    [Fact] public void Another_username_needs_a_refresh() => Assert.Equal(SettingsEffect.Refresh, After(s => s.Letterboxd.Username = "someone-else"));
    [Fact] public void Another_country_needs_a_refresh() => Assert.Equal(SettingsEffect.Refresh, After(s => s.JustWatch.Region = "IE"));
    [Fact] public void Cache_days_do_nothing_now() => Assert.Equal(SettingsEffect.Nothing, After(s => s.Cache.ExpiryDays = 3));
    [Fact] public void Report_choices_do_nothing_now() => Assert.Equal(SettingsEffect.Nothing, After(s => s.Output.Html = false));
    [Fact] public void The_same_services_in_another_order_are_no_change() => Assert.Equal(SettingsEffect.Nothing, After(s => s.JustWatch.MyServices = ["9", "8"]));
    [Fact] public void The_username_in_other_case_or_with_spaces_is_no_change() => Assert.Equal(SettingsEffect.Nothing, After(s => s.Letterboxd.Username = " Film-Fan "));

    [Fact]
    public void Several_changes_at_once_take_the_biggest_effect() =>
        Assert.Equal(SettingsEffect.Refresh, After(s =>
        {
            s.JustWatch.MyServices.Add("337");
            s.Cache.ExpiryDays = 3;
            s.Letterboxd.Username = "someone-else";
        }));
}

/// <summary>The Settings page, against fake Letterboxd and JustWatch sites that count every request.</summary>
public sealed class SettingsPageSpecs : IDisposable
{
    private const int Netflix = 8, Disney = 337;
    private static readonly DateTimeOffset Now = new(2026, 11, 26, 9, 0, 0, TimeSpan.Zero);

    private readonly TempFolder _temp = new();
    private readonly BunitContext _context = new();
    private readonly DataFolder _data;
    private readonly FakeHttp _network;
    private readonly FakeRefreshStatus _refresh = new();
    private readonly LatestResults _latest;

    public SettingsPageSpecs()
    {
        _data = new DataFolder(_temp.Path);
        _data.Prepare([]);
        _network = new FakeHttp(request =>
        {
            var url = request.RequestUri!.ToString();
            var body = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? "";
            if (body.Contains("GetPackages"))
                return FakeHttp.Json("""{"data":{"packages":[{"id":"p8","packageId":8,"clearName":"Netflix","technicalName":"netflix","icon":""},{"id":"p337","packageId":337,"clearName":"Disney Plus","technicalName":"disneyplus","icon":""}]}}""");
            if (url.StartsWith("https://letterboxd.com/example-user/") || url.StartsWith("https://letterboxd.com/new-fan/"))
                return FakeHttp.Html(Fixture.Read("watchlist-page-2.html"));
            if (url.StartsWith("https://letterboxd.com/private-fan/"))
                return FakeHttp.Html("<html><body><p>This watchlist is private.</p></body></html>");
            return new HttpResponseMessage(System.Net.HttpStatusCode.Forbidden);   // as letterboxd.com answers an unknown user
        });
        _latest = new LatestResults(_data);

        _context.Services.AddSingleton(_data);
        _context.Services.AddSingleton(new CurrentSettings(_data));
        _context.Services.AddSingleton(new ServiceCatalog(new HttpClient(_network), _data, NullLoggerFactory.Instance));
        _context.Services.AddSingleton(_latest);
        _context.Services.AddSingleton<IRefreshStatus>(_refresh);
        _context.Services.AddSingleton<TimeProvider>(new FixedClock(Now));
    }

    public void Dispose()
    {
        _context.Dispose();
        _temp.Dispose();
    }

    private AppSettings Saved => SettingsStore.Load(_data.SettingsFile);

    private void WithUser(string username = "example-user")
    {
        var settings = Saved;
        settings.Letterboxd.Username = username;
        settings.JustWatch.MyServices = ["8"];
        SettingsStore.Save(_data.SettingsFile, settings);
    }

    private IRenderedComponent<Settings> Open()
    {
        _context.Renderer.SetRendererInfo(new RendererInfo("Server", isInteractive: true));
        _context.Services.GetRequiredService<NavigationManager>().NavigateTo("settings");
        var page = _context.Render<Settings>();
        page.WaitForState(() => page.FindAll("label.service").Count == 2);
        return page;
    }

    private static void Tick(IRenderedComponent<Settings> page, string service, bool ticked) =>
        page.FindAll("label.service").Single(l => l.TextContent.Contains(service)).QuerySelector("input")!.Change(ticked);

    private static void SaveOn(IRenderedComponent<Settings> page) => page.Find("form").Submit();

    // ── Regrouping without fetching ─────────────────────────────────────────────

    [Fact]
    public void Ticking_a_service_and_saving_regroups_the_results_with_no_requests()
    {
        WithUser();
        _latest.Replace(new SavedRun
        {
            Id = "r1", RanAt = Now, MyServices = { "8" }, ExpiringSoonDays = 60,
            Services = { new Package { PackageId = Netflix, ClearName = "Netflix" }, new Package { PackageId = Disney, ClearName = "Disney Plus" } },
            Films =
            {
                new SavedFilm { Film = new WatchlistFilm { Title = "Disney Film", FilmId = "1" }, Offers = [new Offer { MonetizationType = "FLATRATE", Package = new OfferPackage { PackageId = Disney } }] },
            },
        });
        var home = _context.Render<Home>();
        Assert.Contains("Available elsewhere only", home.Find("h2").TextContent);
        var page = Open();
        var requestsBefore = _network.RequestedUrls.Count;

        Tick(page, "Disney Plus", true);
        SaveOn(page);

        page.WaitForAssertion(() => Assert.Contains("regrouped", page.Find(".notice").TextContent));
        home.WaitForAssertion(() => Assert.Contains("Available on your services", home.Find("h2").TextContent));
        Assert.Equal(requestsBefore, _network.RequestedUrls.Count);
        Assert.Equal(new[] { "337", "8" }, Saved.JustWatch.MyServices.Order());
    }

    // ── The order of the services (#39) ─────────────────────────────────────────

    private static string[] ServiceNames(IRenderedComponent<Settings> page) =>
        page.FindAll("label.service").Select(l => l.TextContent.Trim()).ToArray();

    [Fact]
    public void On_opening_the_ticked_services_come_first()
    {
        WithUser();   // Netflix ticked

        Assert.Equal(new[] { "Netflix", "Disney Plus" }, ServiceNames(Open()));
    }

    [Fact]
    public void Ticking_a_service_leaves_the_list_where_it_is()
    {
        WithUser();
        var page = Open();
        var before = ServiceNames(page);

        Tick(page, "Disney Plus", true);

        Assert.Equal(before, ServiceNames(page));
    }

    [Fact]
    public void Unticking_a_service_at_the_top_leaves_the_list_where_it_is()
    {
        WithUser();
        var page = Open();
        var before = ServiceNames(page);

        Tick(page, "Netflix", false);

        Assert.Equal(before, ServiceNames(page));
        Assert.False(page.FindAll("label.service").First().QuerySelector("input")!.HasAttribute("checked"));
    }

    // ── Checking the username ───────────────────────────────────────────────────

    [Fact]
    public void An_unknown_username_is_refused_and_nothing_is_saved()
    {
        WithUser();
        var page = Open();

        page.Find("#username").Input("nobody-at-all");
        SaveOn(page);

        page.WaitForAssertion(() => Assert.Equal("There's no Letterboxd user called \"nobody-at-all\". Check the spelling.", page.Find(".notice--error").TextContent.Trim()));
        Assert.Equal("example-user", Saved.Letterboxd.Username);
    }

    [Fact]
    public void A_private_or_empty_watchlist_is_refused_and_nothing_is_saved()
    {
        WithUser();
        var page = Open();

        page.Find("#username").Input("private-fan");
        SaveOn(page);

        page.WaitForAssertion(() => Assert.Contains("it may be private", page.Find(".notice--error").TextContent));
        Assert.Equal("example-user", Saved.Letterboxd.Username);
    }

    [Fact]
    public void A_new_username_with_a_public_watchlist_is_saved_and_a_refresh_offered()
    {
        WithUser();
        _latest.Replace(new SavedRun { Id = "r1", RanAt = Now });
        var page = Open();

        page.Find("#username").Input("new-fan");
        SaveOn(page);

        page.WaitForAssertion(() => Assert.Contains("refresh to see new ones", page.Find(".notice--ok").TextContent));
        Assert.Equal("new-fan", Saved.Letterboxd.Username);
        Assert.NotNull(page.Find(".notice--ok button"));
    }

    [Fact]
    public void Out_of_range_days_are_refused()
    {
        WithUser();
        var page = Open();

        page.Find("#soon").Change("0");
        SaveOn(page);

        page.WaitForAssertion(() => Assert.Contains("between 1 and 365", page.Find(".notice--error").TextContent));
        Assert.Equal(60, Saved.JustWatch.ExpiringSoonDays);
    }

    // ── First run ───────────────────────────────────────────────────────────────

    [Fact]
    public void With_no_username_and_no_results_the_app_opens_on_settings()
    {
        var home = _context.Render<Home>();

        Assert.EndsWith("/settings", _context.Services.GetRequiredService<NavigationManager>().Uri);
    }

    [Fact]
    public void Settings_greets_a_first_run_and_saving_a_username_starts_the_first_refresh()
    {
        var page = Open();
        Assert.Contains("Welcome.", page.Markup);

        page.Find("#username").Input("new-fan");
        SaveOn(page);

        page.WaitForAssertion(() => Assert.Equal(1, _refresh.Requests));
        Assert.Equal("new-fan", Saved.Letterboxd.Username);
        Assert.EndsWith("/", _context.Services.GetRequiredService<NavigationManager>().Uri);
    }

    // ── Cancel and leaving ──────────────────────────────────────────────────────

    [Fact]
    public void Cancel_puts_back_the_saved_settings()
    {
        WithUser();
        var page = Open();
        page.Find("#username").Input("typo");

        page.FindAll("button").Single(b => b.TextContent == "Cancel").Click();

        Assert.Equal("example-user", page.Find("#username").GetAttribute("value"));
    }

    [Fact]
    public async Task Leaving_with_unsaved_changes_asks_first_on_the_page()
    {
        WithUser();
        var page = Open();
        var navigation = _context.Services.GetRequiredService<NavigationManager>();
        Tick(page, "Disney Plus", true);

        await page.InvokeAsync(() => navigation.NavigateTo("/"));

        page.WaitForAssertion(() => Assert.Contains("You have changes that aren't saved.", page.Markup));
        Assert.EndsWith("/settings", navigation.Uri);
    }

    [Fact]
    public async Task Leaving_with_nothing_changed_just_goes()
    {
        WithUser();
        var page = Open();
        var navigation = _context.Services.GetRequiredService<NavigationManager>();

        await page.InvokeAsync(() => navigation.NavigateTo("/"));

        Assert.DoesNotContain("You have changes that aren't saved.", page.Markup);
        Assert.EndsWith("/", navigation.Uri);
    }
}

/// <summary>Settings are kept in the data folder, so they're still there after the app restarts.</summary>
public sealed class SettingsPersistenceSpecs : IDisposable
{
    private readonly TempFolder _data = new();

    public void Dispose() => _data.Dispose();

    [Fact]
    public void Settings_survive_a_restart()
    {
        using (var first = new TestApp(dataRoot: _data.Path))
        {
            var saved = first.Services.GetRequiredService<CurrentSettings>().Load();
            saved.Letterboxd.Username = "film-fan";
            saved.JustWatch.MyServices = ["8", "337"];
            saved.JustWatch.ExpiringSoonDays = 45;
            first.Services.GetRequiredService<CurrentSettings>().Save(saved);
        }

        using var second = new TestApp(dataRoot: _data.Path);
        var loaded = second.Services.GetRequiredService<CurrentSettings>().Load();

        Assert.Equal(("film-fan", 45), (loaded.Letterboxd.Username, loaded.JustWatch.ExpiringSoonDays));
        Assert.Equal(new[] { "337", "8" }, loaded.JustWatch.MyServices.Order());
    }
}
