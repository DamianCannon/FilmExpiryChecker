using System.Diagnostics;
using Bunit;
using FilmExpiryChecker.Config;
using FilmExpiryChecker.JustWatch;
using FilmExpiryChecker.Letterboxd;
using FilmExpiryChecker.Reporting;
using FilmExpiryChecker.Running;
using FilmExpiryChecker.Storage;
using FilmExpiryChecker.Web.Components.Layout;
using FilmExpiryChecker.Web.Components.Results;
using FilmExpiryChecker.Web.Refresh;
using FilmExpiryChecker.Web.Settings;
using Microsoft.Extensions.DependencyInjection;

namespace FilmExpiryChecker.Tests;

/// <summary>Settings changes always take effect (#47).</summary>
public sealed class SettingsTakeEffectSpecs : IDisposable
{
    private readonly TempFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    private DataFolder Data => new(_temp.Path);

    // ── The cache is kept per country ───────────────────────────────────────────

    [Fact]
    public void A_film_cached_for_one_country_is_looked_up_again_for_another()
    {
        var gb = new JustWatchCache(Data.JustWatchCacheFile, 7, region: "GB");
        gb.Set("44542", []);
        gb.Save();

        Assert.True(new JustWatchCache(Data.JustWatchCacheFile, 7, region: "GB").TryGet("44542", out _));
        Assert.False(new JustWatchCache(Data.JustWatchCacheFile, 7, region: "IE").TryGet("44542", out _));
    }

    [Fact]
    public void Entries_from_before_countries_were_kept_apart_count_as_the_current_countrys()
    {
        Directory.CreateDirectory(Data.CacheFolder);
        File.WriteAllText(Data.JustWatchCacheFile, "{\"44542\":{\"CachedAt\":\"" + DateTime.UtcNow.ToString("o") + "\",\"Offers\":[]}}");

        Assert.True(new JustWatchCache(Data.JustWatchCacheFile, 7, region: "GB").TryGet("44542", out _));
    }

    [Fact]
    public async Task After_a_change_of_country_a_refresh_looks_every_film_up_again()
    {
        var gb = new JustWatchCache(Data.JustWatchCacheFile, 7, region: "GB");
        foreach (var id in FakeSites.FilmIds) gb.Set(id, []);
        gb.Save();
        var http = FakeSites.Create();
        var settings = new AppSettings { Letterboxd = { Username = "example-user" }, JustWatch = { Region = "IE", MyServices = { "8" } } };

        await new CheckRunner(new HttpClient(http), Data, time: InstantWaits.Instance).RunAsync(settings);

        Assert.Equal(FakeSites.FilmIds.Length, http.RequestBodies.Count(b => b.Contains("GetTitleOffers")));
    }

    // ── A refresh asked for during one runs after it ────────────────────────────

    [Fact]
    public async Task A_refresh_asked_for_after_the_current_one_runs_once_it_ends_and_never_alongside()
    {
        using var gate = new ManualResetEventSlim();
        var inFlight = 0;
        var mostAtOnce = 0;
        var network = FakeSites.Create((request, _, ct) =>
        {
            if (request.RequestUri!.ToString() != FakeSites.Watchlist + "1/") return null;
            var now = Interlocked.Increment(ref inFlight);
            InterlockedMax(ref mostAtOnce, now);
            gate.Wait(ct);
            Interlocked.Decrement(ref inFlight);
            return null;
        });
        using var app = new TestApp(network, _temp.Path).WithUser();
        var refresh = app.Services.GetRequiredService<RefreshService>();

        Assert.True(refresh.RequestRefresh());
        Assert.False(refresh.RequestRefresh(afterCurrent: true));
        Assert.False(refresh.RequestRefresh(afterCurrent: true));   // still only one waiting
        gate.Set();

        var clock = Stopwatch.StartNew();
        while (new RunStore(app.Data).List().Count < 2 && clock.Elapsed < TimeSpan.FromSeconds(15)) await Task.Delay(20);
        while (refresh.State is RefreshState.Running && clock.Elapsed < TimeSpan.FromSeconds(15)) await Task.Delay(20);
        await Task.Delay(200);

        Assert.Equal(2, network.RequestedUrls.Count(u => u == FakeSites.Watchlist + "1/"));
        Assert.Equal(1, mostAtOnce);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int seen;
        while ((seen = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, seen) != seen) { }
    }

    // ── Results for someone else say so ─────────────────────────────────────────

    private static IRenderedComponent<ResultsView> View(BunitContext context, string currentUser, string currentRegion) =>
        context.Render<ResultsView>(p => p
            .Add(v => v.Run, new SavedRun { RanAt = DateTimeOffset.UtcNow, Username = "film-fan", Region = "GB", MyServices = { "8" }, ExpiringSoonDays = 60 })
            .Add(v => v.CurrentUsername, currentUser)
            .Add(v => v.CurrentRegion, currentRegion));

    [Fact]
    public void Results_for_another_username_or_country_say_whose_they_are()
    {
        using var context = new BunitContext();
        context.Services.AddSingleton<TimeProvider>(TimeProvider.System);

        Assert.Equal(
            "These results are for film-fan in United Kingdom, but your settings are now for new-fan in United Kingdom. Refresh to see yours.",
            View(context, "new-fan", "GB").Find(".for-others").TextContent);
        Assert.Contains("now for film-fan in Ireland", View(context, "film-fan", "IE").Find(".for-others").TextContent);
    }

    [Fact]
    public void Results_for_the_current_settings_say_nothing_extra()
    {
        using var context = new BunitContext();
        context.Services.AddSingleton<TimeProvider>(TimeProvider.System);

        Assert.Empty(View(context, "FILM-FAN", "gb").FindAll(".for-others"));
    }

    // ── A broken settings file doesn't stop the app ─────────────────────────────

    [Fact]
    public async Task With_a_broken_settings_file_the_app_still_starts_on_the_defaults_and_says_so()
    {
        Data.Prepare([]);
        File.WriteAllText(Data.SettingsFile, "{ \"Letterboxd\": { \"Username\": \"film-fan\", }\n  \"JustWatch\": oops }");
        using var app = new TestApp(dataRoot: _temp.Path, autoRefreshOnStart: true);

        var response = await app.CreateClient().GetAsync("/settings");

        Assert.True(response.IsSuccessStatusCode);
        Assert.Equal("GB", SettingsStore.Load(Data.SettingsFile).JustWatch.Region);
        Assert.NotNull(app.Services.GetRequiredService<CurrentSettings>().Problem);
    }

    [Fact]
    public void The_status_bar_says_the_settings_file_couldnt_be_read()
    {
        Data.Prepare([]);
        File.WriteAllText(Data.SettingsFile, "{ not json");
        using var context = new BunitContext();
        context.Services.AddSingleton<IRefreshStatus>(new FakeRefreshStatus());
        context.Services.AddSingleton(new LatestResults(Data));
        context.Services.AddSingleton(Data);
        context.Services.AddSingleton(new CurrentSettings(Data));
        context.Services.AddSingleton<TimeProvider>(TimeProvider.System);

        var bar = context.Render<StatusBar>();

        Assert.StartsWith("Your settings file couldn't be read", bar.Find(".status-text").TextContent);
        Assert.NotNull(bar.Find(".status-settings"));
    }

    [Fact]
    public void A_service_id_that_isnt_a_number_is_skipped()
    {
        var builder = new ReportBuilder(["Netflix", "8"], [new Package { PackageId = 8, ClearName = "Netflix" }], 60);

        var result = builder.Build([new WatchlistFilm { Title = "The Film", FilmId = "1" }],
            _ => [new Offer { MonetizationType = "FLATRATE", Package = new OfferPackage { PackageId = 8 } }]).Single();

        Assert.Equal(FilmCategory.AvailableMyServices, result.Category);
    }
}
