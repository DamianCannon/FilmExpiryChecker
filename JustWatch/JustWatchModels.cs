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
    public DateTime? ExpiryDate => DateTime.TryParse(AvailableTo, out var d) ? d.ToUniversalTime() : null;
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
}