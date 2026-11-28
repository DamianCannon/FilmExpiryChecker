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

        // Leaving dates are calendar days, counted from today where the user is (#46).
        var today = Dates.Today(_time.GetUtcNow(), _time.LocalTimeZone);
        var lastSoonDay = today.AddDays(_expiringSoonDays);

        // Only offers on services JustWatch lists. A listing whose last day has passed is gone (the cache can be
        // days old), so it isn't counted at all.
        var known = offers
            .Where(o => _providerNames.ContainsKey(o.PackageId))
            .Where(o => o.ExpiryDate is not { } last || last.Date >= today)
            .ToList();
        foreach (var unknown in known.Select(o => o.MonetizationType).Where(t => OfferKinds.Of(t) == OfferKind.Unknown))
            if (_unknownTypes.Add(unknown)) _onUnknownType?.Invoke(unknown);

        // Only what can be streamed decides the groups. A service keeps a film until its last listing there ends:
        // any listing with no end date means it isn't leaving that service (#41). A service streaming it free
        // counts as free.
        var streamable = known
            .Where(o => OfferKinds.Of(o.MonetizationType).IsStreamable())
            .GroupBy(o => o.PackageId)
            .Select(g => (PackageId: g.Key,
                          Until: g.Any(o => o.ExpiryDate is null) ? null : g.Max(o => o.ExpiryDate),
                          Kind: g.Any(o => OfferKinds.Of(o.MonetizationType) == OfferKind.Free) ? OfferKind.Free : OfferKind.Subscription));

        foreach (var (packageId, until, kind) in streamable)
        {
            var serviceOffer = new ServiceOffer
            {
                PackageId = packageId,
                ServiceName = _providerNames[packageId],
                ExpiryDate = until,
                Kind = kind,
            };

            // Free services count as mine: anyone can watch them.
            if (_myServiceIds.Contains(packageId) || kind == OfferKind.Free)
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

        bool Leaving(ServiceOffer o) => o.ExpiryDate is { } until && until.Date <= lastSoonDay;
        var myLeaving = result.MyServiceOffers.Where(Leaving).ToList();

        // 1. Leaving any of my services is flagged, even if it can be watched elsewhere: the soonest date shows.
        if (myLeaving.Count > 0)
        {
            result.Category = FilmCategory.LeavingSoonMyServices;
            result.EarliestExpiry = myLeaving.Min(o => o.ExpiryDate!.Value);
        }
        // 2. Staying on one of mine: nothing to lose, whatever services I don't have are doing.
        else if (result.MyServiceOffers.Count > 0)
        {
            result.Category = FilmCategory.AvailableMyServices;
        }
        // 3. Only elsewhere, and every service streaming it is leaving: the last chance (a trial, say) is when the
        //    last of them goes, so that's the date shown.
        else if (result.OtherServiceOffers.Count > 0 && result.OtherServiceOffers.All(Leaving))
        {
            result.Category = FilmCategory.LeavingSoonOtherServices;
            result.EarliestExpiry = result.OtherServiceOffers.Max(o => o.ExpiryDate!.Value);
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
