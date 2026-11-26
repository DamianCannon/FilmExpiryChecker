using FilmExpiryChecker.Storage;

namespace FilmExpiryChecker.Web.Refresh;

/// <summary>
/// Refreshes by itself once a day: when the app starts, or a page opens, and the last refresh that succeeded
/// wasn't today. A failed or cancelled refresh doesn't count, so the next opening tries again.
/// </summary>
public sealed class AutoRefresh(IRefreshStatus refresh, DataFolder data, TimeProvider time)
{
    /// <summary>Whether to start a refresh now. Days are the user's own, in <paramref name="zone"/>.</summary>
    public static bool ShouldStart(DateTimeOffset? lastSuccess, DateTimeOffset now, TimeZoneInfo zone, bool usernameSet) =>
        usernameSet
        && (lastSuccess is null || TimeZoneInfo.ConvertTime(lastSuccess.Value, zone).Date != TimeZoneInfo.ConvertTime(now, zone).Date);

    /// <summary>Starts a refresh if one is due and none is running. Returns whether it started one.</summary>
    public bool CheckNow()
    {
        var state = refresh.State;
        if (state is RefreshState.Running) return false;

        var usernameSet = !string.IsNullOrWhiteSpace(SettingsStore.Load(data.SettingsFile).Letterboxd.Username);
        return ShouldStart(state.LastSuccess, time.GetUtcNow(), time.LocalTimeZone, usernameSet) && refresh.RequestRefresh();
    }
}

/// <summary>Checks once as the app starts (after the refresh service knows when the last run was).</summary>
internal sealed class AutoRefreshOnStart(AutoRefresh auto, ILogger<AutoRefreshOnStart> log) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (auto.CheckNow())
            log.LogInformation("No refresh yet today: started one.");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
