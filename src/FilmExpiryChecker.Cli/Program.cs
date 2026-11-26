using FilmExpiryChecker.Cli;
using FilmExpiryChecker.Config;
using FilmExpiryChecker.JustWatch;
using FilmExpiryChecker.Reporting;
using FilmExpiryChecker.Running;
using Microsoft.Extensions.Configuration;

// ── Configuration ─────────────────────────────────────────────────────────────

var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false)
    .Build();

var settings = config.Get<AppSettings>() ?? throw new InvalidOperationException("Could not load appsettings.json");

// ── HTTP client and logging ───────────────────────────────────────────────────

using var http = new HttpClient();
http.DefaultRequestHeaders.Add("User-Agent",
    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

var loggers = new PlainConsoleLoggerFactory();

// Ctrl+C cancels the run cleanly instead of killing the process mid-request.
using var cancel = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancel.Cancel();
};

// ── --list-services mode ──────────────────────────────────────────────────────

if (args.Contains("--list-services"))
{
    Console.WriteLine($"Fetching streaming services available on JustWatch in {settings.JustWatch.Region}...\n");

    var cache = new JustWatchCache(settings.Cache.CacheFilePath, settings.Cache.ExpiryDays, loggers.CreateLogger("Cache"));
    var jwClient = new JustWatchClient(http, settings.JustWatch.Region, cache, logger: loggers.CreateLogger("JustWatch"));
    var providers = await jwClient.GetProvidersAsync(cancel.Token);

    if (providers.Count == 0)
    {
        Console.WriteLine("No providers returned. Check your region setting or network connectivity.");
        return 1;
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
    return 0;
}

// ── Main run ──────────────────────────────────────────────────────────────────

Console.WriteLine("=== Film Expiry Checker ===");
Console.WriteLine($"Region: {settings.JustWatch.Region}");
Console.WriteLine($"Your services: {string.Join(", ", settings.JustWatch.MyServices)}");
Console.WriteLine($"Expiry threshold: {settings.JustWatch.ExpiringSoonDays} days");
Console.WriteLine();

var runner = new CheckRunner(http, loggers);
var progress = new ConsoleProgress(settings.Letterboxd.Username);

RunResult result;
try
{
    result = await runner.RunAsync(settings, progress, cancel.Token);
}
catch (OperationCanceledException) when (cancel.IsCancellationRequested)
{
    Console.WriteLine();
    Console.WriteLine("Cancelled.");
    return 130;
}

if (result.Outcome == RunOutcome.EmptyWatchlist)
{
    Console.WriteLine("ERROR: No films retrieved from Letterboxd watchlist.");
    Console.WriteLine("Check that your username is correct and your watchlist is set to public.");
    return 1;
}

Console.WriteLine();
Console.WriteLine("Building report...");

foreach (var group in result.Films.GroupBy(r => r.Category).OrderBy(g => g.Key))
    Console.WriteLine($"  {group.First().CategoryDisplay}: {group.Count()}");

Console.WriteLine();

if (settings.Output.Csv)
    Console.WriteLine($"CSV written: {new CsvReporter().Write(result.Films, settings.Output.OutputDirectory)}");

if (settings.Output.Html)
    Console.WriteLine($"HTML report written: {new HtmlReporter().Write(result.Films, settings.Output.OutputDirectory)}");

Console.WriteLine("\nDone.");
return 0;
