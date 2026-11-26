using FilmExpiryChecker.Storage;

namespace FilmExpiryChecker.Tests;

/// <summary>Where the data lives, and the one-time import of the files the command line used to keep elsewhere.</summary>
public sealed class DataFolderSpecs : IDisposable
{
    private readonly TempFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    private DataFolder Data => new(Path.Combine(_temp.Path, "data"));

    /// <summary>A folder laid out as the CLI used to leave it, with files written at <paramref name="when"/>.</summary>
    private string Legacy(string name, DateTime when, string? username = "film-fan", string cache = "{}", params string[] reports)
    {
        var folder = Path.Combine(_temp.Path, name);
        Directory.CreateDirectory(folder);
        if (username is not null)
            Write(Path.Combine(folder, "appsettings.json"), when, $$"""
                { "Letterboxd": { "Username": "{{username}}" },
                  "JustWatch": { "Region": "FR", "MyServices": [ "8", "337" ], "ExpiringSoonDays": 30 },
                  "Cache": { "ExpiryDays": 3, "CacheFilePath": "./cache.json" },
                  "Output": { "Html": false, "Csv": true, "OutputDirectory": "./reports" } }
                """);
        Write(Path.Combine(folder, "cache.json"), when, cache);
        Write(Path.Combine(folder, "imdb-ids.json"), when, $$"""{"from":"{{name}}"}""");
        foreach (var report in reports)
            Write(Path.Combine(folder, "reports", report), when, report);
        return folder;
    }

    private static void Write(string path, DateTime when, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        File.SetLastWriteTimeUtc(path, when);
    }

    private static readonly DateTime May = new(2026, 5, 30, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime November = new(2026, 11, 26, 0, 0, 0, DateTimeKind.Utc);

    // ── Where ───────────────────────────────────────────────────────────────────

    [Fact]
    public void By_default_the_data_lives_under_local_app_data()
    {
        var data = DataFolder.Resolve([], _ => null, @"C:\Users\someone\AppData\Local");

        Assert.Equal(@"C:\Users\someone\AppData\Local\FilmExpiryChecker", data.Root);
    }

    [Fact]
    public void The_environment_variable_overrides_the_default()
    {
        var data = DataFolder.Resolve([], name => name == "FILMEXPIRYCHECKER_DATA" ? @"D:\elsewhere" : null, @"C:\Local");

        Assert.Equal(@"D:\elsewhere", data.Root);
    }

    [Fact]
    public void The_data_option_overrides_the_environment_variable()
    {
        var data = DataFolder.Resolve(["--list-services", "--data", @"E:\test-data"], _ => @"D:\elsewhere", @"C:\Local");

        Assert.Equal(@"E:\test-data", data.Root);
    }

    // ── The first start ─────────────────────────────────────────────────────────

    [Fact]
    public void The_first_start_imports_the_old_settings()
    {
        var old = Legacy("old", May);

        Data.Prepare([old]);

        var settings = SettingsStore.Load(Data.SettingsFile);
        Assert.Equal("film-fan", settings.Letterboxd.Username);
        Assert.Equal("FR", settings.JustWatch.Region);
        Assert.Equal(new[] { "8", "337" }, settings.JustWatch.MyServices);
        Assert.Equal(30, settings.JustWatch.ExpiringSoonDays);
        Assert.Equal(3, settings.Cache.ExpiryDays);
        Assert.False(settings.Output.Html);
    }

    [Fact]
    public void The_first_start_imports_both_caches_and_the_old_reports()
    {
        var old = Legacy("old", May, cache: """{"42":{}}""", reports: ["film-expiry-2026-05-30_1441.csv", "film-expiry-2026-05-30_1441.html"]);

        var report = Data.Prepare([old]);

        Assert.Equal("""{"42":{}}""", File.ReadAllText(Data.JustWatchCacheFile));
        Assert.Equal("""{"from":"old"}""", File.ReadAllText(Data.ImdbCacheFile));
        Assert.True(File.Exists(Path.Combine(Data.ReportsFolder, "film-expiry-2026-05-30_1441.html")));
        Assert.Equal(2, report.Reports);
    }

    [Fact]
    public void Of_two_copies_of_a_file_the_newer_is_imported()
    {
        var repoRoot = Legacy("repo-root", May, cache: "may");
        var buildOutput = Legacy("bin-debug-net10", November, cache: "november");

        var report = Data.Prepare([repoRoot, buildOutput]);

        Assert.Equal("november", File.ReadAllText(Data.JustWatchCacheFile));
        Assert.Equal(Path.Combine(buildOutput, "cache.json"), report.JustWatchCache);
    }

    [Fact]
    public void Reports_from_several_folders_are_merged_without_duplicates()
    {
        var a = Legacy("a", May, reports: ["one.csv", "two.csv"]);
        var b = Legacy("b", May, reports: ["two.csv", "three.csv"]);

        var report = Data.Prepare([a, b]);

        Assert.Equal(new[] { "one.csv", "three.csv", "two.csv" },
            Directory.EnumerateFiles(Data.ReportsFolder).Select(Path.GetFileName).Order());
        Assert.Equal(3, report.Reports);
    }

    [Fact]
    public void The_originals_are_left_where_they_were()
    {
        var old = Legacy("old", May, cache: "original");

        Data.Prepare([old]);

        Assert.Equal("original", File.ReadAllText(Path.Combine(old, "cache.json")));
        Assert.True(File.Exists(Path.Combine(old, "appsettings.json")));
    }

    [Fact]
    public void With_nothing_to_import_default_settings_are_written()
    {
        var report = Data.Prepare([]);

        var settings = SettingsStore.Load(Data.SettingsFile);
        Assert.Equal("", settings.Letterboxd.Username);
        Assert.Equal("GB", settings.JustWatch.Region);
        Assert.Equal(60, settings.JustWatch.ExpiringSoonDays);
        Assert.Equal(7, settings.Cache.ExpiryDays);
        Assert.False(report.ImportedAnything);
        Assert.False(File.Exists(Data.JustWatchCacheFile));
    }

    [Fact]
    public void An_import_cut_off_part_way_runs_again_on_the_next_start()
    {
        var old = Legacy("old", May, cache: "the cache", reports: ["locked.csv"]);
        var report = Path.Combine(old, "reports", "locked.csv");

        using (new FileStream(report, FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.ThrowsAny<IOException>(() => Data.Prepare([old]));

        Assert.False(File.Exists(Data.SettingsFile), "settings.json was written before the import finished");

        var second = Data.Prepare([old]);

        Assert.Equal(1, second.Reports);
        Assert.Equal("film-fan", SettingsStore.Load(Data.SettingsFile).Letterboxd.Username);
        Assert.Equal("the cache", File.ReadAllText(Data.JustWatchCacheFile));
    }

    // ── Later starts ────────────────────────────────────────────────────────────

    [Fact]
    public void A_second_start_imports_nothing_even_if_the_old_files_changed()
    {
        var old = Legacy("old", May, cache: "first");
        Data.Prepare([old]);
        Write(Path.Combine(old, "cache.json"), November, "changed later");

        var report = Data.Prepare([old]);

        Assert.False(report.ImportedAnything);
        Assert.Equal("first", File.ReadAllText(Data.JustWatchCacheFile));
    }

    [Fact]
    public void A_file_already_in_the_data_folder_is_never_overwritten()
    {
        Directory.CreateDirectory(Data.CacheFolder);
        File.WriteAllText(Data.JustWatchCacheFile, "already here");
        var old = Legacy("old", November, cache: "from the old place");

        var report = Data.Prepare([old]);

        Assert.Equal("already here", File.ReadAllText(Data.JustWatchCacheFile));
        Assert.Null(report.JustWatchCache);
    }

    // ── Finding the old files ───────────────────────────────────────────────────

    [Fact]
    public void Run_from_its_build_folder_the_CLI_looks_in_the_repo_and_every_build_output()
    {
        var repo = Path.Combine(_temp.Path, "repo");
        var cli = Path.Combine(repo, "src", "FilmExpiryChecker.Cli");
        var exeFolder = Path.Combine(cli, "bin", "Debug", "net10.0");
        var oldBuild = Path.Combine(repo, "bin", "Debug", "net8.0");
        foreach (var folder in new[] { exeFolder, oldBuild }) Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(repo, "FilmExpiryChecker.slnx"), "<Solution />");

        var folders = LegacyFiles.Folders(exeFolder, _temp.Path);

        Assert.Contains(repo, folders);
        Assert.Contains(cli, folders);
        Assert.Contains(exeFolder, folders);
        Assert.Contains(oldBuild, folders);
    }

    [Fact]
    public void Outside_a_repo_only_the_current_and_exe_folders_are_looked_in()
    {
        var exeFolder = Path.Combine(_temp.Path, "installed");
        var current = Path.Combine(_temp.Path, "somewhere");
        Directory.CreateDirectory(exeFolder);
        Directory.CreateDirectory(current);

        Assert.Equal(new[] { current, exeFolder }, LegacyFiles.Folders(exeFolder, current));
    }
}
