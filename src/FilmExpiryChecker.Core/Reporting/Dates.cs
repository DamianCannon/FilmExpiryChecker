using System.Globalization;

namespace FilmExpiryChecker.Reporting;

/// <summary>Leaving dates, said the same way everywhere: the page, the CSV and the command line's report.</summary>
public static class Dates
{
    /// <summary>"07 Sep 2027": invariant month names are always three letters (en-GB gives "Sept").</summary>
    public static string Day(DateTime date) => date.ToString("dd MMM yyyy", CultureInfo.InvariantCulture);

    /// <summary>Today's date where the user is.</summary>
    public static DateTime Today(DateTimeOffset now, TimeZoneInfo zone) => TimeZoneInfo.ConvertTime(now, zone).Date;

    /// <summary>Whole calendar days from today until <paramref name="leaving"/>: 0 is its last day.</summary>
    public static int DaysUntil(DateTime leaving, DateTimeOffset now, TimeZoneInfo zone) =>
        (leaving.Date - Today(now, zone)).Days;
}
