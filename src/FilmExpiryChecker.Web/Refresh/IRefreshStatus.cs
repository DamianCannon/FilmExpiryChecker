namespace FilmExpiryChecker.Web.Refresh;

/// <summary>What pages see of the refresh: its state, when that changes, and a way to ask for one.</summary>
public interface IRefreshStatus
{
    RefreshState State { get; }

    /// <summary>Raised (on a background thread) each time the state changes, including every bit of progress.</summary>
    event Action<RefreshState>? Changed;

    /// <summary>Starts a refresh unless one is already running. Returns whether it started one.</summary>
    /// <param name="afterCurrent">If one is running, start another as soon as it ends: for a change of settings
    /// that makes the running one out of date (#47). Never more than one waiting, never two at once.</param>
    bool RequestRefresh(bool afterCurrent = false);
}
