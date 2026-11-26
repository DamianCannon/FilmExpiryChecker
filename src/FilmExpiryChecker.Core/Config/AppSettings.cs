namespace FilmExpiryChecker.Config;

/// <summary>The user's settings, kept in the data folder's settings.json.</summary>
public class AppSettings
{
    public LetterboxdSettings Letterboxd { get; set; } = new();
    public JustWatchSettings JustWatch { get; set; } = new();
    public CacheSettings Cache { get; set; } = new();
    public OutputSettings Output { get; set; } = new();
}

public class LetterboxdSettings
{
    public string Username { get; set; } = string.Empty;
}

public class JustWatchSettings
{
    public string Region { get; set; } = "GB";
    public List<string> MyServices { get; set; } = new();
    public int ExpiringSoonDays { get; set; } = 60;
}

public class CacheSettings
{
    public int ExpiryDays { get; set; } = 7;
}

/// <summary>Which report files the command line writes (into the data folder's reports folder).</summary>
public class OutputSettings
{
    public bool Html { get; set; } = true;
    public bool Csv { get; set; } = true;
}
