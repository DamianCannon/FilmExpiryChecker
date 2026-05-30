using FilmExpiryChecker.JustWatch;
using FilmExpiryChecker.Letterboxd;

namespace FilmExpiryChecker.Reporting;

public class ReportBuilder
{
    private readonly HashSet<int> _myServiceIds;
    private readonly Dictionary<int, string> _providerNames;
    private readonly int _expiringSoonDays;

    public ReportBuilder(
        IEnumerable<string> myServiceIds,
        IEnumerable<Package> allProviders,
        int expiringSoonDays)
    {
        _myServiceIds = new HashSet<int>(myServiceIds.Select(int.Parse));
        _providerNames = allProviders.ToDictionary(p => p.PackageId, p => p.ClearName);
        _expiringSoonDays = expiringSoonDays;
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

        var threshold = DateTime.UtcNow.AddDays(_expiringSoonDays);

        // Per service: keep the earliest-dated offer (most urgent), or permanent if no dated offers
        var deduped = offers
            .GroupBy(o => o.PackageId)
            .Select(g => g.Where(o => o.ExpiryDate.HasValue).MinBy(o => o.ExpiryDate) ?? g.First());

        foreach (var offer in deduped)
        {
            if (!_providerNames.TryGetValue(offer.PackageId, out var serviceName))
                continue;

            var serviceOffer = new ServiceOffer
            {
                PackageId = offer.PackageId,
                ServiceName = serviceName,
                ExpiryDate = offer.ExpiryDate
            };

            if (_myServiceIds.Contains(offer.PackageId))
                result.MyServiceOffers.Add(serviceOffer);
            else
                result.OtherServiceOffers.Add(serviceOffer);
        }

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
        else
        {
            result.Category = FilmCategory.NotOnAnyService;
        }

        return result;
    }
}
