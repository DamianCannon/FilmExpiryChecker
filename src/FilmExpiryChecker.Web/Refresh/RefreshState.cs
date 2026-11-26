using FilmExpiryChecker.Running;

namespace FilmExpiryChecker.Web.Refresh;

/// <summary>What the refresh is doing. Immutable: the service swaps in a new one each time anything changes.</summary>
public abstract record RefreshState
{
    /// <summary>When the last refresh that finished successfully ran, if there has ever been one.</summary>
    public abstract DateTimeOffset? LastSuccess { get; }

    /// <summary>Nothing running.</summary>
    public sealed record Idle(DateTimeOffset? LastSuccess) : RefreshState
    {
        public override DateTimeOffset? LastSuccess { get; } = LastSuccess;
    }

    /// <summary>A refresh under way, and how far it has got. <c>Total</c> is null where it isn't known yet.</summary>
    public sealed record Running(RunStep Step, int Done, int? Total, DateTimeOffset StartedAt, DateTimeOffset? LastSuccess)
        : RefreshState
    {
        public override DateTimeOffset? LastSuccess { get; } = LastSuccess;
    }

    /// <summary>The last refresh failed, for <paramref name="Reason"/> (plain words, fit to show).</summary>
    public sealed record Failed(string Reason, DateTimeOffset At, DateTimeOffset? LastSuccess) : RefreshState
    {
        public override DateTimeOffset? LastSuccess { get; } = LastSuccess;
    }
}
