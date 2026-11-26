using System.Text;
using FilmExpiryChecker.JustWatch;
using FilmExpiryChecker.Storage;

namespace FilmExpiryChecker.Tests;

/// <summary>Files are written so a cut-off write never replaces a good file, and leftovers are cleared.</summary>
public sealed class SafeSavingSpecs : IDisposable
{
    private readonly TempFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void A_safe_write_replaces_the_file_and_leaves_no_part_file()
    {
        var path = _temp.File("settings.json");
        File.WriteAllText(path, "old");

        SafeFile.WriteAllText(path, "new");

        Assert.Equal("new", File.ReadAllText(path));
        Assert.False(File.Exists(path + ".part"));
    }

    [Fact]
    public void A_write_that_fails_part_way_leaves_the_previous_file_intact()
    {
        var path = _temp.File("cache.json");
        File.WriteAllText(path, "the good copy");

        Assert.Throws<IOException>(() => SafeFile.Write(path, stream =>
        {
            stream.Write(Encoding.UTF8.GetBytes("{\"half\":"));
            throw new IOException("disk full");
        }));

        Assert.Equal("the good copy", File.ReadAllText(path));
        Assert.False(File.Exists(path + ".part"));
    }

    [Fact]
    public void A_stray_part_file_is_ignored_and_removed_when_the_cache_loads()
    {
        var path = _temp.File("justwatch.json");
        var good = new JustWatchCache(path, expiryDays: 7);
        good.Set("42", new List<Offer>());
        good.Save();
        File.WriteAllText(path + ".part", "{\"cut off half-wa");

        var loaded = new JustWatchCache(path, expiryDays: 7);

        Assert.True(loaded.TryGet("42", out _));
        Assert.False(File.Exists(path + ".part"));
    }

    [Fact]
    public void A_stray_part_file_is_ignored_and_removed_when_settings_load()
    {
        var path = _temp.File("settings.json");
        SettingsStore.Save(path, new Config.AppSettings { Letterboxd = { Username = "film-fan" } });
        File.WriteAllText(path + ".part", "{\"Letterbo");

        var settings = SettingsStore.Load(path);

        Assert.Equal("film-fan", settings.Letterboxd.Username);
        Assert.False(File.Exists(path + ".part"));
    }
}
