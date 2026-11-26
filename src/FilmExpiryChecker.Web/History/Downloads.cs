using System.Text;
using FilmExpiryChecker.Web.Refresh;

namespace FilmExpiryChecker.Web.History;

/// <summary>The files the app hands out: a run as CSV, and the command line's old reports.</summary>
internal static class Downloads
{
    public static void MapDownloads(this WebApplication app)
    {
        // The latest results as CSV.
        app.MapGet("/download/latest.csv", (LatestResults latest, RunHistory history) =>
            latest.Run is { } run ? CsvFile(history, run) : Results.NotFound());

        // Any saved run as CSV.
        app.MapGet("/download/{id}.csv", (string id, RunHistory history) =>
            history.Load(id) is { } run ? CsvFile(history, run) : Results.NotFound());

        // An old report the command line wrote, to open in the usual browser.
        app.MapGet("/reports/{fileName}", (string fileName, RunHistory history) =>
            history.OldReportPath(fileName) is { } path
                ? Results.File(path, fileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) ? "text/csv" : "text/html")
                : Results.NotFound());
    }

    private static IResult CsvFile(RunHistory history, Storage.SavedRun run) =>
        Results.File(Encoding.UTF8.GetBytes(history.Csv(run)), "text/csv", $"film-expiry-{run.Id}.csv");
}
