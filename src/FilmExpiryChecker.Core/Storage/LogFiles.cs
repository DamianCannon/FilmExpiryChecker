namespace FilmExpiryChecker.Storage;

/// <summary>Keeps the logs folder from growing for ever (#49).</summary>
public static class LogFiles
{
    public static readonly TimeSpan KeptFor = TimeSpan.FromDays(30);

    /// <summary>Deletes log files last written more than 30 days before <paramref name="now"/>. Returns their names.</summary>
    public static IReadOnlyList<string> Prune(DataFolder data, DateTimeOffset now)
    {
        if (!Directory.Exists(data.LogsFolder)) return [];
        var deleted = new List<string>();
        foreach (var file in new DirectoryInfo(data.LogsFolder).EnumerateFiles("*.log"))
        {
            if (now - new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero) <= KeptFor) continue;
            try
            {
                file.Delete();
                deleted.Add(file.Name);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // In use (the launcher's, say): it goes next time.
            }
        }
        return deleted;
    }
}
