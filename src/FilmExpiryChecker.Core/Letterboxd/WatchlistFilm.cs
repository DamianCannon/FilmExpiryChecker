namespace FilmExpiryChecker.Letterboxd;

public class WatchlistFilm
{
    public string Title { get; set; } = string.Empty;
    public int? Year { get; set; }
    public string Slug { get; set; } = string.Empty;

    /// <summary>Letterboxd's internal film ID. Used as the cache key.</summary>
    public string FilmId { get; set; } = string.Empty;

    public override string ToString() => Year.HasValue ? $"{Title} ({Year})" : Title;
}
