using FilmExpiryChecker.JustWatch;
using FilmExpiryChecker.Letterboxd;

namespace FilmExpiryChecker.Reporting;

public enum FilmCategory
{
    LeavingSoonMyServices = 1,
    LeavingSoonOtherServices = 2,
    AvailableMyServices = 3,
    AvailableOtherServices = 4,
    RentOrBuyOnly = 5,
    NotOnAnyService = 6,
    NotFoundOnJustWatch = 7
}

public class FilmResult
{
    public WatchlistFilm Film { get; set; } = null!;
    public FilmCategory Category { get; set; }

    /// <summary>Services the user subscribes to that have this film.</summary>
    public List<ServiceOffer> MyServiceOffers { get; set; } = new();

    /// <summary>Other services (not subscribed) that have this film.</summary>
    public List<ServiceOffer> OtherServiceOffers { get; set; } = new();

    /// <summary>Services where it can only be rented or bought.</summary>
    public List<ServiceOffer> RentOrBuyOffers { get; set; } = new();

    /// <summary>Earliest expiry across all relevant offers, if known.</summary>
    public DateTime? EarliestExpiry { get; set; }

    public int? DaysUntilExpiry => DaysLeftAt(DateTimeOffset.UtcNow, TimeZoneInfo.Local);

    /// <summary>Calendar days left from today where the user is (0 is the last day), counted from the day the
    /// results are looked at, not the day they were fetched.</summary>
    public int? DaysLeftAt(DateTimeOffset now, TimeZoneInfo zone) =>
        EarliestExpiry is { } leaving ? Dates.DaysUntil(leaving, now, zone) : null;

    public string MyServicesDisplay =>
        MyServiceOffers.Count > 0
            ? string.Join(", ", MyServiceOffers.Select(o => FormatOffer(o)))
            : string.Empty;

    public string OtherServicesDisplay =>
        OtherServiceOffers.Count > 0
            ? string.Join(", ", OtherServiceOffers.Select(o => FormatOffer(o)))
            : string.Empty;

    public string RentOrBuyDisplay => string.Join(", ", RentOrBuyOffers.Select(o => o.ServiceName));

    /// <summary>How it can be watched, in words: e.g. "subscription, free" or "rent or buy".</summary>
    public string HowToWatchDisplay => string.Join(", ",
        MyServiceOffers.Concat(OtherServiceOffers).Select(o => o.Kind)
            .Concat(Category == FilmCategory.RentOrBuyOnly ? [OfferKind.RentOrBuy] : [])
            .Distinct().Order().Select(k => k.Words()));

    public string ExpiryDisplay => EarliestExpiry.HasValue
        ? Dates.Day(EarliestExpiry.Value)
        : string.Empty;

    public string CategoryDisplay => Category switch
    {
        FilmCategory.LeavingSoonMyServices    => "Leaving soon — your services",
        FilmCategory.LeavingSoonOtherServices => "Leaving soon — other services",
        FilmCategory.AvailableMyServices      => "Available on your services",
        FilmCategory.AvailableOtherServices   => "Available elsewhere only",
        FilmCategory.RentOrBuyOnly            => "Rent or buy only",
        FilmCategory.NotOnAnyService          => "Not on any streaming service",
        FilmCategory.NotFoundOnJustWatch      => "Not found on JustWatch",
        _                                     => string.Empty
    };

    private static string FormatOffer(ServiceOffer o)
    {
        var name = o.Kind == OfferKind.Free ? $"{o.ServiceName} (free)" : o.ServiceName;
        return o.ExpiryDate.HasValue ? $"{name} (until {Dates.Day(o.ExpiryDate.Value)})" : name;
    }
}

public class ServiceOffer
{
    public int PackageId { get; set; }
    public string ServiceName { get; set; } = string.Empty;
    public DateTime? ExpiryDate { get; set; }

    /// <summary>How the film is offered there.</summary>
    public OfferKind Kind { get; set; } = OfferKind.Subscription;
}
