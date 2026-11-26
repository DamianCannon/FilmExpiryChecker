using System.Globalization;
using FilmExpiryChecker.Running;

namespace FilmExpiryChecker.Web.Refresh;

/// <summary>How fresh the shown results are, in numbers: when they're from, how many films, how many leaving soon.</summary>
public sealed record ResultsSummary(DateTimeOffset RanAt, int Films, int LeavingSoon);

/// <summary>One line for the status bar, with what goes beside it.</summary>
/// <param name="Progress">0–1 for a progress bar; null for no bar; <see cref="double.NaN"/> for one that just moves.</param>
public sealed record StatusLine(string Text, double? Progress = null, bool OfferRetry = false, bool PointToSettings = false);

/// <summary>Words the status bar's line. Pure, so every case can be specified.</summary>
public static class StatusText
{
    // Invariant month names are all three letters ("Sep"; en-GB gives "Sept").
    private static readonly CultureInfo English = CultureInfo.InvariantCulture;

    /// <param name="shown">The results on screen, if any.</param>
    /// <param name="usernameSet">Whether there's a Letterboxd username to refresh with.</param>
    /// <param name="zone">The user's time zone, for "today" and "yesterday" and the times shown.</param>
    public static StatusLine For(RefreshState state, ResultsSummary? shown, bool usernameSet, DateTimeOffset now, TimeZoneInfo zone) => state switch
    {
        RefreshState.Running running => Running(running),
        RefreshState.Failed failed => Failed(failed, shown, now, zone),
        _ when shown is not null => new StatusLine(
            $"Updated {When(shown.RanAt, now, zone)} · {Films(shown.Films)} · {shown.LeavingSoon} leaving soon"),
        _ when !usernameSet => new StatusLine("No results yet. Set your Letterboxd username in Settings.", PointToSettings: true),
        _ => new StatusLine("No results yet. Press Refresh to check your watchlist."),
    };

    private static StatusLine Running(RefreshState.Running r) => r.Step switch
    {
        RunStep.ReadingWatchlist when r.Done == 0 => new("Reading your Letterboxd watchlist…", double.NaN),
        RunStep.ReadingWatchlist => new($"Reading your Letterboxd watchlist… page {r.Done}", double.NaN),
        RunStep.FetchingServices => new("Fetching the list of streaming services…", double.NaN),
        RunStep.CheckingFilms when r.Total is > 0 => new(
            $"Checking films {r.Done} / {r.Total}…", Math.Clamp((double)r.Done / r.Total.Value, 0, 1)),
        RunStep.CheckingFilms => new("Checking films…", double.NaN),
        _ => new("Saving…", 1),
    };

    private static StatusLine Failed(RefreshState.Failed f, ResultsSummary? shown, DateTimeOffset now, TimeZoneInfo zone)
    {
        var text = $"Last refresh failed: {f.Reason}";
        if (shown is not null) text += $" Showing results from {Day(shown.RanAt, now, zone)}.";
        var settingsProblem = f.Reason.Contains("Settings", StringComparison.Ordinal);
        return new StatusLine(text, OfferRetry: !settingsProblem, PointToSettings: settingsProblem);
    }

    /// <summary>"today at 07:12", "yesterday at 21:40", "on 25 Nov at 19:46" (with the year if it isn't this one).</summary>
    public static string When(DateTimeOffset at, DateTimeOffset now, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(at, zone);
        var day = Day(at, now, zone);
        return day is "today" or "yesterday" ? $"{day} at {Time(local)}" : $"on {day} at {Time(local)}";
    }

    /// <summary>The local day of <paramref name="at"/>, each moment converted with its own offset (so a clocks-change is right).</summary>
    private static string Day(DateTimeOffset at, DateTimeOffset now, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(at, zone);
        var today = TimeZoneInfo.ConvertTime(now, zone).Date;
        return local.Date == today ? "today"
             : local.Date == today.AddDays(-1) ? "yesterday"
             : local.Year == today.Year ? local.ToString("d MMM", English)
             : local.ToString("d MMM yyyy", English);
    }

    private static string Time(DateTimeOffset local) => local.ToString("HH:mm", English);

    private static string Films(int count) => count == 1 ? "1 film" : $"{count} films";
}
