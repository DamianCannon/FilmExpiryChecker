using Bunit;
using FilmExpiryChecker.JustWatch;
using FilmExpiryChecker.Letterboxd;
using FilmExpiryChecker.Running;
using FilmExpiryChecker.Storage;
using FilmExpiryChecker.Web.Components.Layout;
using FilmExpiryChecker.Web.Refresh;
using Microsoft.Extensions.DependencyInjection;

namespace FilmExpiryChecker.Tests;

/// <summary>A refresh status a test can drive directly.</summary>
internal sealed class FakeRefreshStatus : IRefreshStatus
{
    public RefreshState State { get; private set; } = new RefreshState.Idle(null);
    public event Action<RefreshState>? Changed;
    public int Requests { get; private set; }

    public bool RequestRefresh()
    {
        Requests++;
        return true;
    }

    /// <summary>Changes the state from a background thread, as the real service does.</summary>
    public Task Push(RefreshState state) => Task.Run(() =>
    {
        State = state;
        Changed?.Invoke(state);
    });
}

/// <summary>The status bar on the page: it shows the state and keeps up as it changes, without a reload.</summary>
public sealed class StatusBarSpecs : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 26, 8, 0, 0, TimeSpan.Zero);

    private readonly TempFolder _temp = new();
    private readonly BunitContext _context = new();
    private readonly FakeRefreshStatus _refresh = new();
    private readonly LatestResults _latest;

    public StatusBarSpecs()
    {
        var data = new DataFolder(_temp.Path);
        data.Prepare([]);
        var settings = SettingsStore.Load(data.SettingsFile);
        settings.Letterboxd.Username = "example-user";
        SettingsStore.Save(data.SettingsFile, settings);

        _latest = new LatestResults(data);
        _context.Services.AddSingleton<IRefreshStatus>(_refresh);
        _context.Services.AddSingleton(_latest);
        _context.Services.AddSingleton(data);
        _context.Services.AddSingleton(new FilmExpiryChecker.Web.Settings.CurrentSettings(data));
        _context.Services.AddSingleton<TimeProvider>(new FixedClock(Now));
    }

    public void Dispose()
    {
        _context.Dispose();
        _temp.Dispose();
    }

    private static SavedRun RunAt(DateTimeOffset ranAt, int films) => new()
    {
        Id = ranAt.ToString("yyyyMMddHHmmss"),
        RanAt = ranAt,
        MyServices = { "8" },
        ExpiringSoonDays = 60,
        Services = { new Package { PackageId = 8, ClearName = "Netflix" } },
        Films = Enumerable.Range(1, films)
            .Select(i => new SavedFilm { Film = new WatchlistFilm { Title = $"Film {i}", FilmId = i.ToString() }, Offers = [] })
            .ToList(),
    };

    [Fact]
    public void It_starts_with_the_current_state()
    {
        var bar = _context.Render<StatusBar>();

        Assert.Contains("No results yet. Press Refresh to check your watchlist.", bar.Markup);
    }

    [Fact]
    public async Task Progress_from_the_refresh_changes_the_text_and_the_bar_live()
    {
        var bar = _context.Render<StatusBar>();

        await _refresh.Push(new RefreshState.Running(RunStep.CheckingFilms, 120, 480, Now, null));

        bar.WaitForAssertion(() =>
        {
            Assert.Contains("Checking films 120 / 480…", bar.Find(".status-text").TextContent);
            Assert.Equal("0.25", bar.Find("progress").GetAttribute("value"));
        });
    }

    [Fact]
    public async Task The_bar_is_marked_out_while_refreshing_and_after_a_failure()
    {
        var bar = _context.Render<StatusBar>();
        Assert.Contains("status--idle", bar.Find(".status").ClassName);

        await _refresh.Push(new RefreshState.Running(RunStep.CheckingFilms, 1, 2, Now, null));
        bar.WaitForAssertion(() => Assert.Contains("status--running", bar.Find(".status").ClassName));

        await _refresh.Push(new RefreshState.Failed("Couldn't reach Letterboxd. Check your internet connection and try again.", Now, null));
        bar.WaitForAssertion(() => Assert.Contains("status--failed", bar.Find(".status").ClassName));
    }

    [Fact]
    public async Task A_step_of_unknown_length_shows_a_bar_with_no_value()
    {
        var bar = _context.Render<StatusBar>();

        await _refresh.Push(new RefreshState.Running(RunStep.FetchingServices, 0, null, Now, null));

        bar.WaitForAssertion(() => Assert.False(bar.Find("progress").HasAttribute("value")));
    }

    [Fact]
    public async Task New_results_show_how_fresh_they_are()
    {
        var bar = _context.Render<StatusBar>();

        _latest.Replace(RunAt(Now.AddMinutes(-48), films: 3));
        await _refresh.Push(new RefreshState.Idle(Now.AddMinutes(-48)));

        bar.WaitForAssertion(() =>
            Assert.Equal("Updated today at 07:12 · 3 films · 0 leaving soon", bar.Find(".status-text").TextContent));
    }

    [Fact]
    public async Task Retry_after_a_failure_asks_for_a_refresh()
    {
        var bar = _context.Render<StatusBar>();
        await _refresh.Push(new RefreshState.Failed("Couldn't reach Letterboxd. Check your internet connection and try again.", Now, null));

        bar.WaitForAssertion(() => bar.Find("button.status-retry").Click());

        Assert.Equal(1, _refresh.Requests);
    }

    [Fact]
    public async Task Once_the_refresh_is_done_the_bar_goes()
    {
        var bar = _context.Render<StatusBar>();
        await _refresh.Push(new RefreshState.Running(RunStep.CheckingFilms, 1, 2, Now, null));
        bar.WaitForAssertion(() => bar.Find("progress"));

        _latest.Replace(RunAt(Now, films: 2));
        await _refresh.Push(new RefreshState.Idle(Now));

        bar.WaitForAssertion(() => Assert.Empty(bar.FindAll("progress")));
    }
}
