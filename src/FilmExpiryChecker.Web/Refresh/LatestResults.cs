using FilmExpiryChecker.Storage;

namespace FilmExpiryChecker.Web.Refresh;

/// <summary>
/// The newest saved run, kept in memory so every page shows it at once without re-reading a large file,
/// and swapped whole when a refresh finishes (never a half-updated mixture).
/// </summary>
public sealed class LatestResults(DataFolder data)
{
    private readonly Lock _gate = new();
    private SavedRun? _run;
    private bool _loaded;

    /// <summary>Raised (on a background thread) when a newer run replaces the current one.</summary>
    public event Action? Changed;

    /// <summary>The newest run, or null if there has never been one.</summary>
    public SavedRun? Run
    {
        get
        {
            lock (_gate)
            {
                if (!_loaded)
                {
                    _run = new RunStore(data).Latest();
                    _loaded = true;
                }
                return _run;
            }
        }
    }

    public void Replace(SavedRun run)
    {
        lock (_gate)
        {
            _run = run;
            _loaded = true;
        }
        Changed?.Invoke();
    }
}
