using FilmExpiryChecker.Config;
using FilmExpiryChecker.JustWatch;
using FilmExpiryChecker.Letterboxd;
using FilmExpiryChecker.Reporting;
using Microsoft.Extensions.Configuration;

// ── Configuration ─────────────────────────────────────────────────────────────

var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false)
    .Build();

var settings = config.Get<AppSettings>() ?? throw new InvalidOperationException("Could not load appsettings.json");

// ── HTTP client setup ─────────────────────────────────────────────────────────

using var http = new HttpClient();
http.DefaultRequestHeaders.Add("User-Agent",
    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

// ── Letterboxd scraper ────────────────────────────────────────────────────────

var scraper = new LetterboxdScraper(http, settings.Letterboxd.Username);

// ── IMDb ID cache (slug → IMDb ID, for cross-referencing foreign film titles) ─

var imdbCachePath = Path.Combine(
    Path.GetDirectoryName(Path.GetFullPath(settings.Cache.CacheFilePath)) ?? ".",
    "imdb-ids.json");
var imdbCache = new ImdbIdCache(imdbCachePath);

Func<string, CancellationToken, Task<string?>> imdbResolver = async (slug, ct) =>
{
    if (imdbCache.TryGet(slug, out var cached)) return cached;
    await Task.Delay(300, ct);
    var id = await scraper.FetchImdbIdAsync(slug, ct);
    imdbCache.Set(slug, id);
    imdbCache.Save();
    return id;
};

// ── JustWatch client and cache ────────────────────────────────────────────────

var cache = new JustWatchCache(settings.Cache.CacheFilePath, settings.Cache.ExpiryDays);
var jwClient = new JustWatchClient(http, settings.JustWatch.Region, cache, imdbResolver);

// ── --list-services mode ──────────────────────────────────────────────────────

if (args.Contains("--list-services"))
{
    Console.WriteLine($"Fetching streaming services available on JustWatch in {settings.JustWatch.Region}...\n");

    var providers = await jwClient.GetProvidersAsync();

    if (providers.Count == 0)
    {
        Console.WriteLine("No providers returned. Check your region setting or network connectivity.");
        return;
    }

    Console.WriteLine($"{"ID",-20} {"Display Name",-40} {"Technical Name"}");
    Console.WriteLine(new string('-', 80));

    foreach (var p in providers.OrderBy(p => p.ClearName))
        Console.WriteLine($"{p.PackageId,-20} {p.ClearName,-40} {p.TechnicalName}");

    Console.WriteLine($"\n{providers.Count} services listed.");
    Console.WriteLine("\nCopy the IDs you want into the MyServices array in appsettings.json.");

    // Also write to a file for easy reference
    var servicesPath = Path.Combine(settings.Output.OutputDirectory, "available-services.txt");
    Directory.CreateDirectory(settings.Output.OutputDirectory);

    var lines = new List<string>
    {
        $"JustWatch streaming services — region: {settings.JustWatch.Region}",
        $"Generated: {DateTime.Now:dd MMM yyyy HH:mm}",
        new string('-', 80),
        $"{"ID",-20} {"Display Name",-40} {"Technical Name"}",
        new string('-', 80)
    };
    lines.AddRange(providers.OrderBy(p => p.ClearName)
        .Select(p => $"{p.PackageId,-20} {p.ClearName,-40} {p.TechnicalName}"));

    File.WriteAllLines(servicesPath, lines);
    Console.WriteLine($"\nAlso written to: {servicesPath}");
    return;
}

// ── Main run ──────────────────────────────────────────────────────────────────

Console.WriteLine("=== Film Expiry Checker ===");
Console.WriteLine($"Region: {settings.JustWatch.Region}");
Console.WriteLine($"Your services: {string.Join(", ", settings.JustWatch.MyServices)}");
Console.WriteLine($"Expiry threshold: {settings.JustWatch.ExpiringSoonDays} days");
Console.WriteLine();

// Phase 1: Fetch watchlist
var watchlist = await scraper.GetWatchlistAsync();

if (watchlist.Count == 0)
{
    Console.WriteLine("ERROR: No films retrieved from Letterboxd watchlist.");
    Console.WriteLine("Check that your username is correct and your watchlist is set to public.");
    return;
}

Console.WriteLine();

// Phase 2: Fetch all providers (needed for name lookups in reports)
Console.WriteLine("Fetching JustWatch provider list...");
var allProviders = await jwClient.GetProvidersAsync();
Console.WriteLine($"  {allProviders.Count} providers loaded.");
Console.WriteLine();

// Phase 3: Enrich each film via JustWatch (with caching)
Console.WriteLine($"Enriching {watchlist.Count} films via JustWatch...");
Console.WriteLine($"  Cache: {cache.Count} entries, {cache.ExpiredCount} expired.");
Console.WriteLine();

// Pre-fetch all offers so we can pass them to the builder
var offerMap = new Dictionary<string, List<Offer>?>();
var processed = 0;

foreach (var film in watchlist)
{
    processed++;
    if (processed % 25 == 0 || processed == watchlist.Count)
        Console.WriteLine($"  {processed}/{watchlist.Count} films processed...");

    offerMap[film.FilmId] = await jwClient.GetOffersAsync(film);
}

// Save cache after all fetches
cache.Save();
Console.WriteLine("Cache saved.");
Console.WriteLine();

// Phase 4: Build categorised report
Console.WriteLine("Building report...");
var builder = new ReportBuilder(
    settings.JustWatch.MyServices,
    allProviders,
    settings.JustWatch.ExpiringSoonDays);

var results = builder.Build(watchlist, film =>
{
    offerMap.TryGetValue(film.FilmId, out var offers);
    return offers;
});

// Summary to console
var categories = results.GroupBy(r => r.Category).OrderBy(g => g.Key);
foreach (var group in categories)
    Console.WriteLine($"  {group.First().CategoryDisplay}: {group.Count()}");

Console.WriteLine();

// Phase 5: Output reports
if (settings.Output.Csv)
    new CsvReporter().Write(results, settings.Output.OutputDirectory);

if (settings.Output.Html)
    new HtmlReporter().Write(results, settings.Output.OutputDirectory);

Console.WriteLine("\nDone.");
