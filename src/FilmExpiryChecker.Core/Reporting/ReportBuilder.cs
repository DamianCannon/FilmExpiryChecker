using FilmExpiryChecker.JustWatch;
using FilmExpiryChecker.Letterboxd;

namespace FilmExpiryChecker.Reporting;

public class ReportBuilder
{
    private readonly HashSet<int> _myServiceIds;
    private readonly Dictionary<int, string> _providerNames;
    private readonly int _expiringSoonDays;
    private readonly TimeProvider _time;
    private readonly Action<string>? _onUnknownType;
    private readonly HashSet<string> _unknownTypes = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="onUnknownType">Told once for each monetisation type JustWatch uses that isn't known (and so ignored).</param>
    public ReportBuilder(
        IEnumerable<string> myServiceIds,
        IEnumerable<Package> allProviders,
        int expiringSoonDays,
        TimeProvider? timeProvider = null,
        Action<string>? onUnknownType = null)
    {
        _myServiceIds = new HashSet<int>(myServiceIds.Select(int.Parse));
        _providerNames = allProviders.ToDictionary(p => p.PackageId, p => p.ClearName);
        _expiringSoonDays = expiringSoonDays;
        _time = timeProvider ?? TimeProvider.System;
        _onUnknownType = onUnknownType;
    }

    public List<FilmResult> Build(List<WatchlistFilm> films, Func<WatchlistFilm, List<Offer>?> getOffers)
    {
        var results = new List<FilmResult>();

        foreach (var film in films)
        {
            var offers = getOffers(film);
            var result = Categorise(film, offers);
            results.Add(result);
        }

        return results
            .OrderBy(r => r.Category)
            .ThenBy(r => r.Category is FilmCategory.LeavingSoonMyServices or FilmCategory.LeavingSoonOtherServices
                ? r.EarliestExpiry ?? DateTime.MaxValue
                : (object)r.Film.Title)
            .ToList();
    }

    private FilmResult Categorise(WatchlistFilm film, List<Offer>? offers)
    {
        var result = new FilmResult { Film = film };

        if (offers is null)
        {
            result.Category = FilmCategory.NotFoundOnJustWatch;
            return result;
        }

        if (offers.Count == 0)
        {
            result.Category = FilmCategory.NotOnAnyService;
            return result;
        }

        var threshold = _time.GetUtcNow().UtcDateTime.AddDays(_expiringSoonDays);

        // Only offers on services JustWatch lists, of a kind we know.
        var known = offers.Where(o => _providerNames.ContainsKey(o.PackageId)).ToList();
        foreach (var unknown in known.Select(o => o.MonetizationType).Where(t => OfferKinds.Of(t) == OfferKind.Unknown))
            if (_unknownTypes.Add(unknown)) _onUnknownType?.Invoke(unknown);

        // Only what can be streamed decides the groups. Per service: keep the earliest-dated offer (most urgent),
        // or a permanent one if none is dated; a service streaming it free counts as free.
        var streamable = known
            .Where(o => OfferKinds.Of(o.MonetizationType).IsStreamable())
            .GroupBy(o => o.PackageId)
            .Select(g => (Offer: g.Where(o => o.ExpiryDate.HasValue).MinBy(o => o.ExpiryDate) ?? g.First(),
                          Kind: g.Any(o => OfferKinds.Of(o.MonetizationType) == OfferKind.Free) ? OfferKind.Free : OfferKind.Subscription));

        foreach (var (offer, kind) in streamable)
        {
            var serviceOffer = new ServiceOffer
            {
                PackageId = offer.PackageId,
                ServiceName = _providerNames[offer.PackageId],
                ExpiryDate = offer.ExpiryDate,
                Kind = kind,
            };

            // Free services count as mine: anyone can watch them.
            if (_myServiceIds.Contains(offer.PackageId) || kind == OfferKind.Free)
                result.MyServiceOffers.Add(serviceOffer);
            else
                result.OtherServiceOffers.Add(serviceOffer);
        }

        // Where it can only be paid for, film by film, for the "Rent or buy only" group.
        result.RentOrBuyOffers = known
            .Where(o => OfferKinds.Of(o.MonetizationType) == OfferKind.RentOrBuy)
            .DistinctBy(o => o.PackageId)
            .Select(o => new ServiceOffer { PackageId = o.PackageId, ServiceName = _providerNames[o.PackageId], Kind = OfferKind.RentOrBuy })
            .OrderBy(o => o.ServiceName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Find earliest expiry across my services first, then other services
        var myExpiringOffers = result.MyServiceOffers
            .Where(o => o.ExpiryDate.HasValue && o.ExpiryDate.Value <= threshold)
            .ToList();

        var otherExpiringOffers = result.OtherServiceOffers
            .Where(o => o.ExpiryDate.HasValue && o.ExpiryDate.Value <= threshold)
            .ToList();

        if (myExpiringOffers.Count > 0)
        {
            result.Category = FilmCategory.LeavingSoonMyServices;
            result.EarliestExpiry = myExpiringOffers.Min(o => o.ExpiryDate!.Value);
        }
        else if (otherExpiringOffers.Count > 0)
        {
            result.Category = FilmCategory.LeavingSoonOtherServices;
            result.EarliestExpiry = otherExpiringOffers.Min(o => o.ExpiryDate!.Value);
        }
        else if (result.MyServiceOffers.Count > 0)
        {
            result.Category = FilmCategory.AvailableMyServices;
        }
        else if (result.OtherServiceOffers.Count > 0)
        {
            result.Category = FilmCategory.AvailableOtherServices;
        }
        else if (result.RentOrBuyOffers.Count > 0)
        {
            result.Category = FilmCategory.RentOrBuyOnly;
        }
        else
        {
            result.Category = FilmCategory.NotOnAnyService;
        }

        return result;
    }
}
