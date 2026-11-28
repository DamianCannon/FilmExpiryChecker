using FilmExpiryChecker.Config;
using FilmExpiryChecker.JustWatch;
using FilmExpiryChecker.Letterboxd;
using FilmExpiryChecker.Reporting;
using FilmExpiryChecker.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FilmExpiryChecker.Running;

/// <summary>The steps of a check, in the order they happen.</summary>
public enum RunStep
{
    /// <summary>Reading the Letterboxd watchlist. <c>Done</c> = pages read, <c>Films</c> = films found so far.</summary>
    ReadingWatchlist,

    /// <summary>Fetching the list of streaming services from JustWatch.</summary>
    FetchingServices,

    /// <summary>Looking each film up on JustWatch. <c>Done</c> of <c>Total</c> films.</summary>
    CheckingFilms,

    /// <summary>Saving the cache.</summary>
    Saving,
}

/// <summary>Where a run has got to. <c>Total</c> is null where it isn't known yet.</summary>
public sealed record RunProgress(RunStep Step, int Done = 0, int? Total = null, int Films = 0);

public enum RunOutcome
{
    Completed,

    /// <summary>No films were read: the username is wrong, or the watchlist is private or empty.</summary>
    EmptyWatchlist,
}

public sealed class RunResult
{
    public required RunOutcome Outcome { get; init; }

    /// <summary>Every watchlist film, grouped and ordered. Empty unless the run completed.</summary>
    public required List<FilmResult> Films { get; init; }

    public int ServiceCount { get; init; }

    /// <summary>The run as saved in the data folder. Null unless the run completed.</summary>
    public SavedRun? Run { get; init; }
}

/// <summary>
/// One whole check: read the watchlist, look each film up on JustWatch, and group the results.
/// Writes no reports; the caller decides what to do with the results.
/// </summary>
public sealed class CheckRunner(
    HttpClient http, DataFolder data, ILoggerFactory? loggerFactory = null, TimeProvider? time = null)
{
    private readonly ILoggerFactory _loggers = loggerFactory ?? NullLoggerFactory.Instance;
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>The cache is saved after every this many films.</summary>
    public const int SaveEvery = 25;

    /// <summary>A run gives up after this many JustWatch searches fail in a row.</summary>
    public const int GiveUpAfterSearchErrors = 5;

    public async Task<RunResult> RunAsync(
        AppSettings settings, IProgress<RunProgress>? progress = null, CancellationToken ct = default)
    {
        var log = _loggers.CreateLogger<CheckRunner>();
        var scraper = new LetterboxdScraper(
            http, settings.Letterboxd.Username, _loggers.CreateLogger<LetterboxdScraper>(), _time);

        // Slug → IMDb ID, for matching films whose titles differ between Letterboxd and JustWatch.
        var imdbCache = new ImdbIdCache(data.ImdbCacheFile);

        async Task<string?> ResolveImdbId(string slug, CancellationToken token)
        {
            if (imdbCache.TryGet(slug, out var cached)) return cached;
            await Task.Delay(TimeSpan.FromMilliseconds(300), _time, token);
            var id = await scraper.FetchImdbIdAsync(slug, token);
            imdbCache.Set(slug, id);
            imdbCache.Save();
            return id;
        }

        var cache = new JustWatchCache(
            data.JustWatchCacheFile, settings.Cache.ExpiryDays, _loggers.CreateLogger<JustWatchCache>());
        var client = new JustWatchClient(
            http, settings.JustWatch.Region, cache, ResolveImdbId, _loggers.CreateLogger<JustWatchClient>(), _time);

        // 1. The watchlist
        progress?.Report(new RunProgress(RunStep.ReadingWatchlist));
        var watchlist = await scraper.GetWatchlistAsync(
            new Relay<WatchlistPage>(p =>
                progress?.Report(new RunProgress(RunStep.ReadingWatchlist, p.Page, null, p.FilmsSoFar))),
            ct);

        if (watchlist.Count == 0)
            return new RunResult { Outcome = RunOutcome.EmptyWatchlist, Films = new() };

        // A film added while the pages are read shifts them, so one film can end one page and start the next.
        watchlist = watchlist.DistinctBy(f => f.FilmId).ToList();

        log.LogInformation("Total films in watchlist: {Count}", watchlist.Count);

        // 2. The streaming services, for their names
        progress?.Report(new RunProgress(RunStep.FetchingServices));
        var services = await client.GetProvidersAsync(ct);
        if (services.Count == 0)
            throw new RunFailedException("Couldn't reach JustWatch to get the list of streaming services. Try again later.");
        log.LogInformation("{Count} providers loaded.", services.Count);

        // 3. Each film's offers
        log.LogInformation("Cache: {Count} entries, {Expired} expired.", cache.Count, cache.ExpiredCount);
        progress?.Report(new RunProgress(RunStep.CheckingFilms, 0, watchlist.Count));

        var offers = new Dictionary<string, List<Offer>?>();
        try
        {
            for (var i = 0; i < watchlist.Count; i++)
            {
                var film = watchlist[i];
                offers[film.FilmId] = await client.GetOffersAsync(film, ct);

                // Failed searches look like "not found"; a run of them means JustWatch is down, not the films.
                if (client.ConsecutiveSearchErrors >= GiveUpAfterSearchErrors)
                    throw new RunFailedException(
                        "JustWatch stopped answering part-way through. Your earlier results are kept; try again later.");

                // Saved as it goes, so a run that is killed outright still keeps most of its work.
                if ((i + 1) % SaveEvery == 0)
                    cache.Save();

                progress?.Report(new RunProgress(RunStep.CheckingFilms, i + 1, watchlist.Count));
            }
        }
        catch
        {
            // Cancelled or failed part-way: keep every film fetched so far, so the next run needn't fetch it again.
            cache.Save();
            log.LogInformation("Stopped part-way; the cache keeps the films checked so far.");
            throw;
        }

        // Offers of a kind not known are ignored in grouping; say so once each, in case JustWatch adds one.
        foreach (var type in offers.Values.SelectMany(o => o ?? []).Select(o => o.MonetizationType)
                     .Where(t => OfferKinds.Of(t) == OfferKind.Unknown).Distinct(StringComparer.OrdinalIgnoreCase))
            log.LogWarning("JustWatch offered films as \"{Type}\", which isn't a known kind; those offers are ignored.", type);

        // 4. Save what was fetched
        progress?.Report(new RunProgress(RunStep.Saving));
        cache.Save();
        log.LogInformation("Cache saved.");

        // 5. Keep the run itself, raw offers and all, so it can be shown and regrouped later
        var run = new RunStore(data).Save(new SavedRun
        {
            RanAt = _time.GetUtcNow(),
            Username = settings.Letterboxd.Username,
            Region = settings.JustWatch.Region,
            MyServices = settings.JustWatch.MyServices.ToList(),
            ExpiringSoonDays = settings.JustWatch.ExpiringSoonDays,
            Services = services,
            Films = watchlist.Select(f => new SavedFilm { Film = f, Offers = offers.GetValueOrDefault(f.FilmId) }).ToList(),
        });
        log.LogInformation("Run saved as {Id}.", run.Id);

        // Earlier weeks keep only their latest run; this week keeps them all (#42).
        var pruned = new RunStore(data).Prune(_time.GetUtcNow(), _time.LocalTimeZone);
        if (pruned.Count > 0)
            log.LogInformation("Tidied {Count} older runs, keeping the latest of each earlier week.", pruned.Count);

        return new RunResult { Outcome = RunOutcome.Completed, Films = run.Group(_time), ServiceCount = services.Count, Run = run };
    }

    /// <summary>Passes each report straight on, on the same thread, so progress arrives in order.</summary>
    private sealed class Relay<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
