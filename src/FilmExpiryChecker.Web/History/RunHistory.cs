using System.Collections.Concurrent;
using FilmExpiryChecker.Reporting;
using FilmExpiryChecker.Storage;
using FilmExpiryChecker.Web.Refresh;
using FilmExpiryChecker.Web.Settings;

namespace FilmExpiryChecker.Web.History;

/// <summary>One saved run as the History page lists it, counted with the current settings.</summary>
public sealed record RunSummary(string Id, DateTimeOffset RanAt, int Films, int OnMyServices, int LeavingSoon, string Username = "", string Region = "");

/// <summary>An HTML or CSV report the command line wrote before the app existed.</summary>
public sealed record OldReport(string FileName, DateTime WrittenAt);

/// <summary>Past runs, their downloads, and the old reports, for the History page.</summary>
public sealed class RunHistory(DataFolder data, CurrentSettings settings, LatestResults latest, TimeProvider time)
{
    // Counting a run means reading it (about 1 MB); keep the counts, keyed by run and the settings they used.
    private readonly ConcurrentDictionary<(string Id, string Settings), RunSummary> _summaries = new();

    private RunStore Store => new(data);

    /// <summary>Every saved run, newest first.</summary>
    public IReadOnlyList<RunSummary> List()
    {
        var current = settings.Load();
        var key = string.Join(",", current.JustWatch.MyServices.Order()) + "|" + current.JustWatch.ExpiringSoonDays;
        return Store.List()
            .Select(entry => _summaries.TryGetValue((entry.Id, key), out var known) ? known : Summarise(entry.Id, key))
            .OfType<RunSummary>()
            .ToList();
    }

    private RunSummary? Summarise(string id, string key)
    {
        var run = Store.Load(id);
        if (run is null) return null;
        var results = Group(run);
        var summary = new RunSummary(id, run.RanAt, results.Count,
            results.Count(r => r.Category is FilmCategory.AvailableMyServices or FilmCategory.LeavingSoonMyServices),
            results.Count(r => r.Category is FilmCategory.LeavingSoonMyServices or FilmCategory.LeavingSoonOtherServices),
            run.Username, run.Region);
        _summaries[(id, key)] = summary;
        return summary;
    }

    public SavedRun? Load(string id) => Store.Load(id);

    /// <summary>The run's films grouped with the current settings, as the results page shows them.</summary>
    public List<FilmResult> Group(SavedRun run)
    {
        var current = settings.Load();
        return run.Group(current.JustWatch.MyServices, current.JustWatch.ExpiringSoonDays, time);
    }

    /// <summary>The run as CSV, in the command line's columns, with days remaining counted from today.</summary>
    public string Csv(SavedRun run) => new CsvReporter().ToCsv(Group(run), time.GetUtcNow(), time.LocalTimeZone);

    /// <summary>Deletes a run. If it was the latest, the one before becomes the latest.</summary>
    public bool Delete(string id)
    {
        if (!Store.Delete(id)) return false;
        foreach (var key in _summaries.Keys.Where(k => k.Id == id)) _summaries.TryRemove(key, out _);
        latest.Reload();
        return true;
    }

    /// <summary>The HTML reports the command line wrote, newest first.</summary>
    public IReadOnlyList<OldReport> OldReports() =>
        Directory.Exists(data.ReportsFolder)
            ? new DirectoryInfo(data.ReportsFolder).EnumerateFiles("*.html")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Select(f => new OldReport(f.Name, f.LastWriteTime))
                .ToList()
            : [];

    /// <summary>The path of an old report, or null if <paramref name="fileName"/> isn't one (never outside the folder).</summary>
    public string? OldReportPath(string fileName)
    {
        if (fileName != Path.GetFileName(fileName) || fileName.Contains("..")) return null;
        if (!fileName.EndsWith(".html", StringComparison.OrdinalIgnoreCase) && !fileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)) return null;
        var path = Path.Combine(data.ReportsFolder, fileName);
        return File.Exists(path) ? path : null;
    }
}
