using System.Text.Json;
using FilmExpiryChecker.Config;
using FilmExpiryChecker.Storage;

namespace FilmExpiryChecker.Web.Settings;

/// <summary>
/// The user's settings, as saved in the data folder, and a signal when they change, so the results on screen can
/// regroup at once when the services or the "leaving soon" threshold change.
/// </summary>
public sealed class CurrentSettings(DataFolder data)
{
    private static readonly JsonSerializerOptions Copy = new();

    /// <summary>Raised (on the saving thread) after new settings are saved.</summary>
    public event Action? Changed;

    /// <summary>A fresh copy of the saved settings, safe to edit.</summary>
    public AppSettings Load() => SettingsStore.Load(data.SettingsFile);

    public bool UsernameSet => !string.IsNullOrWhiteSpace(Load().Letterboxd.Username);

    /// <summary>Why settings.json can't be read (so the defaults are in use), or null.</summary>
    public string? Problem => SettingsStore.Problem(data.SettingsFile);

    /// <summary>Saves the settings and says what that means for the results.</summary>
    public SettingsEffect Save(AppSettings settings)
    {
        var before = Load();
        var tidy = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings, Copy), Copy)!;
        tidy.Letterboxd.Username = tidy.Letterboxd.Username.Trim();
        tidy.JustWatch.MyServices = tidy.JustWatch.MyServices.Distinct().ToList();

        SettingsStore.Save(data.SettingsFile, tidy);
        Changed?.Invoke();
        return SettingsChange.Effect(before, tidy);
    }
}
