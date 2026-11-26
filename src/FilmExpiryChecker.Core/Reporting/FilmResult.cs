using FilmExpiryChecker.JustWatch;
using FilmExpiryChecker.Letterboxd;

namespace FilmExpiryChecker.Reporting;

public enum FilmCategory
{
    LeavingSoonMyServices = 1,
    LeavingSoonOtherServices = 2,
    AvailableMyServices = 3,
    AvailableOtherServices = 4,
    NotOnAnyService = 5,
    NotFoundOnJustWatch = 6
}

public class FilmResult
{
    public WatchlistFilm Film { get; set; } = null!;
    public FilmCategory Category { get; set; }

    /// <summary>Services the user subscribes to that have this film.</summary>
    public List<ServiceOffer> MyServiceOffers { get; set; } = new();

    /// <summary>Other services (not subscribed) that have this film.</summary>
    public List<ServiceOffer> OtherServiceOffers { get; set; } = new();

    /// <summary>Earliest expiry across all relevant offers, if known.</summary>
    public DateTime? EarliestExpiry { get; set; }

    public int? DaysUntilExpiry => DaysLeftAt(DateTimeOffset.UtcNow);

    /// <summary>Whole days left from <paramref name="now"/> (rounded up), so "days left" is counted from the day
    /// the results are looked at, not the day they were fetched.</summary>
    public int? DaysLeftAt(DateTimeOffset now) => EarliestExpiry.HasValue
        ? (int)Math.Ceiling((EarliestExpiry.Value - now.UtcDateTime).TotalDays)
        : null;

    public string MyServicesDisplay =>
        MyServiceOffers.Count > 0
            ? string.Join(", ", MyServiceOffers.Select(o => FormatOffer(o)))
            : string.Empty;

    public string OtherServicesDisplay =>
        OtherServiceOffers.Count > 0
            ? string.Join(", ", OtherServiceOffers.Select(o => FormatOffer(o)))
            : string.Empty;

    public string ExpiryDisplay => EarliestExpiry.HasValue
        ? EarliestExpiry.Value.ToString("dd MMM yyyy")
        : string.Empty;

    public string CategoryDisplay => Category switch
    {
        FilmCategory.LeavingSoonMyServices    => "Leaving soon — your services",
        FilmCategory.LeavingSoonOtherServices => "Leaving soon — other services",
        FilmCategory.AvailableMyServices      => "Available on your services",
        FilmCategory.AvailableOtherServices   => "Available elsewhere only",
        FilmCategory.NotOnAnyService          => "Not on any streaming service",
        FilmCategory.NotFoundOnJustWatch      => "Not found on JustWatch",
        _                                     => string.Empty
    };

    private static string FormatOffer(ServiceOffer o) =>
        o.ExpiryDate.HasValue
            ? $"{o.ServiceName} (until {o.ExpiryDate.Value:dd MMM yyyy})"
            : o.ServiceName;
}

public class ServiceOffer
{
    public int PackageId { get; set; }
    public string ServiceName { get; set; } = string.Empty;
    public DateTime? ExpiryDate { get; set; }
}
