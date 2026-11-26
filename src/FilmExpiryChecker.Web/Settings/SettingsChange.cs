using FilmExpiryChecker.Config;

namespace FilmExpiryChecker.Web.Settings;

/// <summary>What saving changed settings means for the results on screen.</summary>
public enum SettingsEffect
{
    /// <summary>Nothing to do now (e.g. a cache setting, which matters at the next refresh).</summary>
    Nothing,

    /// <summary>The saved results still apply; regroup them at once, with no fetching.</summary>
    Regroup,

    /// <summary>The saved results are for another user or country; they need a refresh.</summary>
    Refresh,
}

public static class SettingsChange
{
    /// <summary>The effect of going from <paramref name="before"/> to <paramref name="after"/>. If several things
    /// changed, the biggest effect wins.</summary>
    public static SettingsEffect Effect(AppSettings before, AppSettings after)
    {
        if (!string.Equals(before.Letterboxd.Username.Trim(), after.Letterboxd.Username.Trim(), StringComparison.OrdinalIgnoreCase)
            || !string.Equals(before.JustWatch.Region, after.JustWatch.Region, StringComparison.OrdinalIgnoreCase))
            return SettingsEffect.Refresh;

        if (!before.JustWatch.MyServices.Order().SequenceEqual(after.JustWatch.MyServices.Order())
            || before.JustWatch.ExpiringSoonDays != after.JustWatch.ExpiringSoonDays)
            return SettingsEffect.Regroup;

        return SettingsEffect.Nothing;
    }
}
