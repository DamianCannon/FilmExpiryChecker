namespace FilmExpiryChecker.Reporting;

/// <summary>How a film can be watched on a service, from JustWatch's monetisation type.</summary>
public enum OfferKind
{
    /// <summary>Included with a subscription (FLATRATE).</summary>
    Subscription,

    /// <summary>Free to anyone, with or without adverts (FREE, ADS, FAST).</summary>
    Free,

    /// <summary>Paid for per film (RENT, BUY).</summary>
    RentOrBuy,

    /// <summary>In cinemas (CINEMA).</summary>
    Cinema,

    /// <summary>A type JustWatch didn't use before; ignored.</summary>
    Unknown,
}

public static class OfferKinds
{
    public static OfferKind Of(string? monetizationType) => monetizationType?.Trim().ToUpperInvariant() switch
    {
        "FLATRATE" => OfferKind.Subscription,
        "FREE" or "ADS" or "FAST" => OfferKind.Free,
        "RENT" or "BUY" => OfferKind.RentOrBuy,
        "CINEMA" => OfferKind.Cinema,
        _ => OfferKind.Unknown,
    };

    /// <summary>Whether a film can be streamed this way (rather than paid for per film or seen in a cinema).</summary>
    public static bool IsStreamable(this OfferKind kind) => kind is OfferKind.Subscription or OfferKind.Free;

    /// <summary>The kind in plain words, as shown beside a service: "subscription", "free", "rent or buy", "cinema".</summary>
    public static string Words(this OfferKind kind) => kind switch
    {
        OfferKind.Subscription => "subscription",
        OfferKind.Free => "free",
        OfferKind.RentOrBuy => "rent or buy",
        OfferKind.Cinema => "cinema",
        _ => "other",
    };

    /// <summary>The kind's icon: 📺 subscription, 🆓 free, 💷 rent or buy, 🎟️ cinema.</summary>
    public static string Icon(this OfferKind kind) => kind switch
    {
        OfferKind.Subscription => "📺",
        OfferKind.Free => "🆓",
        OfferKind.RentOrBuy => "💷",
        OfferKind.Cinema => "🎟️",
        _ => "•",
    };
}
