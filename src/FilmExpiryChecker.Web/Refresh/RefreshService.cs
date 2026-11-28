using FilmExpiryChecker.Running;
using FilmExpiryChecker.Storage;

namespace FilmExpiryChecker.Web.Refresh;

/// <summary>
/// Owns the one refresh: runs a check in the background, off the pages, so they stay usable; reports its progress
/// as it goes; never runs two at once. Shutting the app down cancels a refresh under way, which keeps the films
/// already checked (the run saves its cache when cancelled) and waits for it to stop.
/// </summary>
public sealed class RefreshService : BackgroundService, IRefreshStatus
{
    private readonly HttpClient _http;
    private readonly DataFolder _data;
    private readonly LatestResults _latest;
    private readonly ILoggerFactory _loggers;
    private readonly TimeProvider _time;
    private readonly ILogger<RefreshService> _log;
    private readonly SemaphoreSlim _requested = new(0);
    private int _busy;
    private int _again;
    private RefreshState _state;

    public RefreshService(
        HttpClient http, DataFolder data, LatestResults latest, ILoggerFactory loggers, TimeProvider time)
    {
        _http = http;
        _data = data;
        _latest = latest;
        _loggers = loggers;
        _time = time;
        _log = loggers.CreateLogger<RefreshService>();
        _state = new RefreshState.Idle(null);
    }

    /// <summary>What the refresh is doing now.</summary>
    public RefreshState State => Volatile.Read(ref _state);

    /// <summary>Raised (on a background thread) each time the state changes, including every bit of progress.</summary>
    public event Action<RefreshState>? Changed;

    /// <summary>
    /// Starts a refresh unless one is already running. Returns whether it started one. Asked with
    /// <paramref name="afterCurrent"/> while one runs, another follows it (only one ever waits).
    /// </summary>
    public bool RequestRefresh(bool afterCurrent = false)
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
        {
            if (afterCurrent) Volatile.Write(ref _again, 1);
            return false;
        }

        SetState(new RefreshState.Running(RunStep.ReadingWatchlist, 0, null, _time.GetUtcNow(), State.LastSuccess));
        _requested.Release();
        return true;
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        SetState(new RefreshState.Idle(_latest.Run?.RanAt));
        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stopping)
    {
        try
        {
            while (true)
            {
                await _requested.WaitAsync(stopping);
                await RefreshOnceAsync(stopping);
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
        }
    }

    private async Task RefreshOnceAsync(CancellationToken stopping)
    {
        var lastSuccess = State.LastSuccess;
        var startedAt = State is RefreshState.Running r ? r.StartedAt : _time.GetUtcNow();
        RefreshState outcome;
        try
        {
            var settings = SettingsStore.Load(_data.SettingsFile);
            if (string.IsNullOrWhiteSpace(settings.Letterboxd.Username))
            {
                outcome = Failed("No Letterboxd username is set yet. Add yours in Settings.", lastSuccess);
            }
            else
            {
                var progress = new Relay(p =>
                    SetState(new RefreshState.Running(p.Step, p.Done, p.Total, startedAt, lastSuccess)));
                var result = await new CheckRunner(_http, _data, _loggers, _time).RunAsync(settings, progress, stopping);

                if (result.Outcome == RunOutcome.EmptyWatchlist)
                {
                    outcome = Failed(
                        $"Letterboxd shows no films on {settings.Letterboxd.Username}'s watchlist. " +
                        "Check the username in Settings, and that the watchlist is public.", lastSuccess);
                }
                else
                {
                    _latest.Replace(result.Run!);
                    outcome = new RefreshState.Idle(result.Run!.RanAt);
                    _log.LogInformation("Refresh finished: {Films} films, saved as {Id}.", result.Films.Count, result.Run.Id);
                }
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            _log.LogInformation("Refresh stopped because the app is closing; the films checked so far are kept.");
            outcome = new RefreshState.Idle(lastSuccess);
        }
        catch (RunFailedException failed)
        {
            _log.LogWarning(failed.InnerException, "Refresh failed: {Reason}", failed.Message);
            outcome = Failed(failed.Message, lastSuccess);
        }
        catch (Exception unexpected)
        {
            _log.LogError(unexpected, "Refresh failed unexpectedly.");
            outcome = Failed("Something went wrong while refreshing. The details are in the log.", lastSuccess);
        }

        SetState(outcome);
        Volatile.Write(ref _busy, 0);

        // One asked for while this ran (settings changed under it) starts now. Checked after clearing _busy, so a
        // request arriving at any moment is either seen here or starts a refresh itself: never lost, never two.
        if (Interlocked.Exchange(ref _again, 0) == 1 && !stopping.IsCancellationRequested)
            RequestRefresh();
    }

    private RefreshState.Failed Failed(string reason, DateTimeOffset? lastSuccess) =>
        new(reason, _time.GetUtcNow(), lastSuccess);

    private void SetState(RefreshState state)
    {
        Volatile.Write(ref _state, state);
        Changed?.Invoke(state);
    }

    public override void Dispose()
    {
        _requested.Dispose();
        base.Dispose();
    }

    /// <summary>Passes progress on synchronously, so it arrives in order.</summary>
    private sealed class Relay(Action<RunProgress> report) : IProgress<RunProgress>
    {
        public void Report(RunProgress value) => report(value);
    }
}
