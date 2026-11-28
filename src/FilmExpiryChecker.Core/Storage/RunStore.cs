using System.Globalization;
using System.Text.Json;
using FilmExpiryChecker.JustWatch;
using FilmExpiryChecker.Letterboxd;
using FilmExpiryChecker.Reporting;

namespace FilmExpiryChecker.Storage;

/// <summary>
/// One finished check, as saved: when it ran, the settings it used, the services JustWatch listed, and every
/// watchlist film with its raw offers. The offers, not the groups, are kept so a run can be regrouped later
/// with different services or a different "leaving soon" threshold, without fetching anything again.
/// </summary>
public sealed class SavedRun
{
    /// <summary>The file name without ".json": the UTC time it ran, as yyyy-MM-dd_HHmmss.</summary>
    public string Id { get; set; } = string.Empty;

    public DateTimeOffset RanAt { get; set; }
    public string Username { get; set; } = string.Empty;
    public string Region { get; set; } = string.Empty;
    public List<string> MyServices { get; set; } = new();
    public int ExpiringSoonDays { get; set; }

    /// <summary>Every service JustWatch listed, for their names.</summary>
    public List<Package> Services { get; set; } = new();

    public List<SavedFilm> Films { get; set; } = new();

    /// <summary>The films grouped with the settings the run used.</summary>
    public List<FilmResult> Group(TimeProvider? time = null) => Group(MyServices, ExpiringSoonDays, time);

    /// <summary>The films grouped as if these had been the settings.</summary>
    public List<FilmResult> Group(IEnumerable<string> myServices, int expiringSoonDays, TimeProvider? time = null)
    {
        // A film saved twice (by an older version) is shown once, as first saved.
        var films = Films.DistinctBy(f => f.Film.FilmId).ToList();
        var offers = films.ToDictionary(f => f.Film.FilmId, f => f.Offers);
        return new ReportBuilder(myServices, Services, expiringSoonDays, time)
            .Build(films.Select(f => f.Film).ToList(), film => offers.GetValueOrDefault(film.FilmId));
    }
}

public sealed class SavedFilm
{
    public WatchlistFilm Film { get; set; } = new();

    /// <summary>Null when JustWatch had no match; empty when it matched but nothing offers it.</summary>
    public List<Offer>? Offers { get; set; }

    /// <summary>The JustWatch title the offers are from, so a wrong match can be seen (#48). Null for runs from
    /// before it was kept, and for films not found.</summary>
    public TitleMatch? Match { get; set; }
}

/// <summary>A saved run's id and when it ran, without loading it.</summary>
public sealed record RunEntry(string Id, DateTimeOffset RanAt);

/// <summary>Keeps finished runs in the data folder's runs folder, one JSON file each.</summary>
public sealed class RunStore(DataFolder data)
{
    private const string IdFormat = "yyyy-MM-dd_HHmmss";
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Saves the run, giving it an id from when it ran (made unique if two runs share a second).</summary>
    public SavedRun Save(SavedRun run)
    {
        Directory.CreateDirectory(data.RunsFolder);
        var id = run.RanAt.UtcDateTime.ToString(IdFormat, CultureInfo.InvariantCulture);
        for (var n = 2; File.Exists(PathOf(id)); n++)
            id = $"{run.RanAt.UtcDateTime.ToString(IdFormat, CultureInfo.InvariantCulture)}-{n}";

        run.Id = id;
        SafeFile.WriteAllText(PathOf(id), JsonSerializer.Serialize(run, Json));
        return run;
    }

    /// <summary>Every saved run, newest first. Half-written files (.part) are not runs.</summary>
    public IReadOnlyList<RunEntry> List()
    {
        if (!Directory.Exists(data.RunsFolder)) return [];

        return Directory.EnumerateFiles(data.RunsFolder, "*.json")
            .Select(Path.GetFileNameWithoutExtension)
            .Select(id => (Id: id!, RanAt: ParseTime(id!)))
            .Where(e => e.RanAt is not null)
            .OrderByDescending(e => e.RanAt)
            .ThenByDescending(e => e.Id, StringComparer.Ordinal)
            .Select(e => new RunEntry(e.Id, e.RanAt!.Value))
            .ToList();
    }

    /// <summary>The run with this id, or null if there is none or it can't be read.</summary>
    public SavedRun? Load(string id)
    {
        if (id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || id.Contains("..")) return null;
        var path = PathOf(id);
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<SavedRun>(File.ReadAllText(path), Json);
        }
        // Being written, or open in another program: skipped this time rather than taking the page down (#49).
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Deletes the run with this id. Returns whether there was one.</summary>
    public bool Delete(string id)
    {
        if (id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || id.Contains("..")) return false;
        var path = PathOf(id);
        if (!File.Exists(path)) return false;
        File.Delete(path);
        return true;
    }

    /// <summary>
    /// Tidies up as it goes (#42): every run from this week is kept; from each earlier week, only its newest.
    /// Weeks run Monday to Sunday in <paramref name="zone"/>. Returns the ids deleted.
    /// </summary>
    public IReadOnlyList<string> Prune(DateTimeOffset now, TimeZoneInfo zone)
    {
        var thisWeek = WeekStart(now, zone);
        var deleted = List()
            .Where(e => WeekStart(e.RanAt, zone) < thisWeek)
            .GroupBy(e => WeekStart(e.RanAt, zone))
            .SelectMany(week => week.Skip(1))   // List() is newest first, so each week's first is its newest
            .Select(e => e.Id)
            .ToList();
        foreach (var id in deleted) Delete(id);
        return deleted;
    }

    /// <summary>The local date of the Monday starting the week <paramref name="moment"/> falls in.</summary>
    public static DateTime WeekStart(DateTimeOffset moment, TimeZoneInfo zone)
    {
        var day = TimeZoneInfo.ConvertTime(moment, zone).Date;
        return day.AddDays(-(((int)day.DayOfWeek + 6) % 7));
    }

    /// <summary>The newest run that can be read, or null if there are none.</summary>
    public SavedRun? Latest() => List().Select(e => Load(e.Id)).FirstOrDefault(r => r is not null);

    private string PathOf(string id) => Path.Combine(data.RunsFolder, id + ".json");

    private static DateTimeOffset? ParseTime(string id) =>
        DateTime.TryParseExact(id.Length >= IdFormat.Length ? id[..IdFormat.Length] : id, IdFormat,
            CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t)
            ? new DateTimeOffset(t, TimeSpan.Zero)
            : null;
}
