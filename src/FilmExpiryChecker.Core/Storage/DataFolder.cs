using System.Text.Json;
using FilmExpiryChecker.Config;

namespace FilmExpiryChecker.Storage;

/// <summary>
/// The one folder holding everything the app keeps: settings, caches, saved runs, reports and logs.
/// By default <c>%LOCALAPPDATA%\FilmExpiryChecker</c>, where no rebuild or re-clone touches it.
/// </summary>
public sealed class DataFolder
{
    public const string EnvironmentVariable = "FILMEXPIRYCHECKER_DATA";
    public const string Option = "--data";

    public DataFolder(string root) => Root = Path.GetFullPath(root);

    public string Root { get; }
    public string SettingsFile => Path.Combine(Root, "settings.json");
    public string CacheFolder => Path.Combine(Root, "cache");
    public string JustWatchCacheFile => Path.Combine(CacheFolder, "justwatch.json");
    public string ImdbCacheFile => Path.Combine(CacheFolder, "imdb-ids.json");
    public string RunsFolder => Path.Combine(Root, "runs");
    public string ReportsFolder => Path.Combine(Root, "reports");
    public string LogsFolder => Path.Combine(Root, "logs");

    /// <summary>
    /// Where the data lives: <c>--data &lt;path&gt;</c> if given, else the environment variable, else the
    /// usual place under local app data. Tests and hand checks use the first two to point at a temporary folder.
    /// </summary>
    public static DataFolder Resolve(IReadOnlyList<string> args, Func<string, string?> environment, string localAppData)
    {
        for (var i = 0; i < args.Count - 1; i++)
            if (args[i] == Option)
                return new DataFolder(args[i + 1]);

        var fromEnvironment = environment(EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
            return new DataFolder(fromEnvironment);

        return new DataFolder(Path.Combine(localAppData, "FilmExpiryChecker"));
    }

    /// <summary>
    /// Makes the folder ready. The first time (no settings.json yet), copies in the newest of each file the app
    /// used to keep elsewhere, or writes default settings if there are none. Never overwrites a file already here,
    /// and never moves or deletes the originals.
    /// </summary>
    public ImportReport Prepare(IEnumerable<string> legacyFolders)
    {
        foreach (var folder in new[] { Root, CacheFolder, RunsFolder, ReportsFolder, LogsFolder })
            Directory.CreateDirectory(folder);

        if (File.Exists(SettingsFile))
            return ImportReport.Nothing;

        var folders = legacyFolders.Where(Directory.Exists).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var report = new ImportReport();

        var settings = Newest(folders, "appsettings.json");
        if (settings is not null)
        {
            SettingsStore.Save(SettingsFile, SettingsStore.Load(settings));
            report.Settings = settings;
        }
        else
        {
            SettingsStore.Save(SettingsFile, new AppSettings());
        }

        report.JustWatchCache = CopyNewest(folders, "cache.json", JustWatchCacheFile);
        report.ImdbCache = CopyNewest(folders, "imdb-ids.json", ImdbCacheFile);

        foreach (var reports in folders.Select(f => Path.Combine(f, "reports")).Where(Directory.Exists))
        foreach (var file in Directory.EnumerateFiles(reports))
        {
            var target = Path.Combine(ReportsFolder, Path.GetFileName(file));
            if (File.Exists(target)) continue;
            File.Copy(file, target);
            report.Reports++;
        }

        return report;
    }

    private static string? Newest(IEnumerable<string> folders, string name) =>
        folders.Select(f => new FileInfo(Path.Combine(f, name)))
               .Where(f => f.Exists)
               .OrderByDescending(f => f.LastWriteTimeUtc)
               .FirstOrDefault()?.FullName;

    private static string? CopyNewest(IEnumerable<string> folders, string name, string target)
    {
        if (File.Exists(target)) return null;
        var source = Newest(folders, name);
        if (source is null) return null;
        File.Copy(source, target);
        return source;
    }
}

/// <summary>What <see cref="DataFolder.Prepare"/> copied in: the source of each file, or null if none.</summary>
public sealed class ImportReport
{
    public static ImportReport Nothing => new();

    public string? Settings { get; set; }
    public string? JustWatchCache { get; set; }
    public string? ImdbCache { get; set; }
    public int Reports { get; set; }

    public bool ImportedAnything => Settings is not null || JustWatchCache is not null || ImdbCache is not null || Reports > 0;
}

/// <summary>Reads and writes settings.json.</summary>
public static class SettingsStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    /// <summary>The settings in the file, with defaults for anything missing. Unknown old fields are ignored.</summary>
    public static AppSettings Load(string path) =>
        JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Json) ?? new AppSettings();

    public static void Save(string path, AppSettings settings) =>
        File.WriteAllText(path, JsonSerializer.Serialize(settings, Json));
}

/// <summary>Where the command line kept its files before the data folder existed.</summary>
public static class LegacyFiles
{
    /// <summary>
    /// The folders worth looking in: the current folder, the exe's own folder, and, if the exe runs from inside the
    /// repo, the repo root, the CLI project folder and every build output folder under them.
    /// </summary>
    public static IReadOnlyList<string> Folders(string exeFolder, string currentFolder)
    {
        var folders = new List<string> { currentFolder, exeFolder };

        var repo = FindRepoRoot(exeFolder) ?? FindRepoRoot(currentFolder);
        if (repo is not null)
        {
            var cli = Path.Combine(repo, "src", "FilmExpiryChecker.Cli");
            folders.Add(repo);
            folders.Add(cli);
            folders.AddRange(BuildOutputs(repo));
            folders.AddRange(BuildOutputs(cli));
        }

        return folders.Where(Directory.Exists).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string? FindRepoRoot(string start)
    {
        for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "FilmExpiryChecker.slnx")))
                return dir.FullName;
        return null;
    }

    /// <summary>bin/&lt;configuration&gt;/&lt;framework&gt; folders, e.g. bin/Debug/net8.0.</summary>
    private static IEnumerable<string> BuildOutputs(string project)
    {
        var bin = Path.Combine(project, "bin");
        if (!Directory.Exists(bin)) return [];
        return Directory.EnumerateDirectories(bin).SelectMany(Directory.EnumerateDirectories);
    }
}
