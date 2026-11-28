using FilmExpiryChecker.Storage;

namespace FilmExpiryChecker.Web;

/// <summary>Where the command line used to keep its files, for the data folder's one-time import.</summary>
public sealed record LegacyFolders(IReadOnlyList<string> Folders);

/// <summary>Makes the data folder ready (and does its one-time import) before the app serves anything.</summary>
internal sealed class DataFolderSetup(DataFolder data, LegacyFolders legacy, TimeProvider time, ILogger<DataFolderSetup> log) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var imported = data.Prepare(legacy.Folders);
        log.LogInformation("Data folder: {Root}", data.Root);
        if (imported.ImportedAnything)
            log.LogInformation("First start: imported settings from {Settings}, JustWatch cache from {Cache}, {Reports} report files.",
                imported.Settings ?? "(defaults)", imported.JustWatchCache ?? "(none)", imported.Reports);
        var oldLogs = LogFiles.Prune(data, time.GetUtcNow());
        if (oldLogs.Count > 0)
            log.LogInformation("Deleted {Count} log files over 30 days old.", oldLogs.Count);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
