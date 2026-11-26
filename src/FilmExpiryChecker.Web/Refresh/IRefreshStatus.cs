namespace FilmExpiryChecker.Web.Refresh;

/// <summary>What pages see of the refresh: its state, when that changes, and a way to ask for one.</summary>
public interface IRefreshStatus
{
    RefreshState State { get; }

    /// <summary>Raised (on a background thread) each time the state changes, including every bit of progress.</summary>
    event Action<RefreshState>? Changed;

    /// <summary>Starts a refresh unless one is already running. Returns whether it started one.</summary>
    bool RequestRefresh();
}
