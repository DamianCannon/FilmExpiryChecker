namespace FilmExpiryChecker.JustWatch;

// ── Request ──────────────────────────────────────────────────────────────────

public class GraphQlRequest
{
    public string Query { get; set; } = string.Empty;
    public object? Variables { get; set; }
}

// ── Provider list response ────────────────────────────────────────────────────

public class ProvidersResponse
{
    public ProvidersData? Data { get; set; }
}

public class ProvidersData
{
    public List<Package> Packages { get; set; } = new();
}

public class Package
{
    public string Id { get; set; } = string.Empty;
    public int PackageId { get; set; }
    public string ClearName { get; set; } = string.Empty;
    public string TechnicalName { get; set; } = string.Empty;
    public string Icon { get; set; } = string.Empty;
}

// ── Film lookup response ──────────────────────────────────────────────────────

public class FilmSearchResponse
{
    public FilmSearchData? Data { get; set; }

    /// <summary>GraphQL errors: JustWatch answers 200 with these (and no data) when a query no longer fits its API.</summary>
    public List<System.Text.Json.JsonElement>? Errors { get; set; }
}

public class FilmSearchData
{
    public NodeList? PopularTitles { get; set; }
}

public class NodeList
{
    public List<TitleNode> Edges { get; set; } = new();
}

public class TitleNode
{
    public TitleDetail? Node { get; set; }
}

public class TitleDetail
{
    public string Id { get; set; } = string.Empty;
    public string ObjectType { get; set; } = string.Empty;
    public Content? Content { get; set; }
    public List<Offer> Offers { get; set; } = new();
}

public class Content
{
    public string Title { get; set; } = string.Empty;
    public int? OriginalReleaseYear { get; set; }
    public ExternalIds? ExternalIds { get; set; }
}

public class ExternalIds
{
    public string? ImdbId { get; set; }
    public int? TmdbId { get; set; }
}

public class Offer
{
    public string MonetizationType { get; set; } = string.Empty;
    public OfferPackage? Package { get; set; }
    public string? AvailableTo { get; set; }

    public int PackageId => Package?.PackageId ?? 0;
    /// <summary>
    /// The day it leaves, as a calendar date (midnight, <see cref="DateTimeKind.Utc"/> so nothing shifts it).
    /// JustWatch gives a date only ("2027-08-30"): read as the machine's local time, it showed a day early in
    /// summer (#46).
    /// </summary>
    public DateTime? ExpiryDate =>
        DateTime.TryParse(AvailableTo, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var d)
            ? DateTime.SpecifyKind(d.Date, DateTimeKind.Utc)
            : null;
}

public class OfferPackage
{
    public string Id { get; set; } = string.Empty;
    public int PackageId { get; set; }
}

// ── Cached entry ──────────────────────────────────────────────────────────────

public class CachedFilmData
{
    public DateTime CachedAt { get; set; }

    /// <summary>Null means JustWatch returned no match for this film.</summary>
    public List<Offer>? Offers { get; set; }

    /// <summary>Which JustWatch title the film was matched to, so a wrong match can be seen (#48).</summary>
    public TitleMatch? Match { get; set; }
}

/// <summary>The JustWatch title a watchlist film was matched to.</summary>
public sealed record TitleMatch(string JustWatchId, string Title, int? Year);

/// <summary>A film looked up on JustWatch: its offers (null if no match) and what it was matched to.</summary>
public sealed record FilmLookup(List<Offer>? Offers, TitleMatch? Match);