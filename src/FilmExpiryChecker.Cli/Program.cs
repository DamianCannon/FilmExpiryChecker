using FilmExpiryChecker.Cli;
using FilmExpiryChecker.Config;
using FilmExpiryChecker.JustWatch;
using FilmExpiryChecker.Reporting;
using FilmExpiryChecker.Running;
using FilmExpiryChecker.Storage;

// ── Data folder and settings ──────────────────────────────────────────────────

var data = DataFolder.Resolve(args, Environment.GetEnvironmentVariable,
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

var imported = data.Prepare(LegacyFiles.Folders(AppContext.BaseDirectory, Environment.CurrentDirectory));
if (imported.ImportedAnything)
{
    Console.WriteLine($"First run with the data folder {data.Root}; copied in:");
    if (imported.Settings is not null) Console.WriteLine($"  settings from {imported.Settings}");
    if (imported.JustWatchCache is not null) Console.WriteLine($"  JustWatch cache from {imported.JustWatchCache}");
    if (imported.ImdbCache is not null) Console.WriteLine($"  IMDb ID cache from {imported.ImdbCache}");
    if (imported.Reports > 0) Console.WriteLine($"  {imported.Reports} past report files");
    Console.WriteLine();
}

var settings = SettingsStore.Load(data.SettingsFile);

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

    var cache = new JustWatchCache(data.JustWatchCacheFile, settings.Cache.ExpiryDays, loggers.CreateLogger("Cache"));
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
    Console.WriteLine($"\nCopy the IDs you want into the MyServices array in {data.SettingsFile}.");

    // Also write to a file for easy reference
    var servicesPath = Path.Combine(data.ReportsFolder, "available-services.txt");

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

if (string.IsNullOrWhiteSpace(settings.Letterboxd.Username))
{
    Console.WriteLine($"No Letterboxd username set. Add it to {data.SettingsFile} and run again.");
    return 1;
}

Console.WriteLine("=== Film Expiry Checker ===");
Console.WriteLine($"Region: {settings.JustWatch.Region}");
Console.WriteLine($"Your services: {string.Join(", ", settings.JustWatch.MyServices)}");
Console.WriteLine($"Expiry threshold: {settings.JustWatch.ExpiringSoonDays} days");
Console.WriteLine();

var runner = new CheckRunner(http, data, loggers);
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
    Console.WriteLine($"CSV written: {new CsvReporter().Write(result.Films, data.ReportsFolder)}");

if (settings.Output.Html)
    Console.WriteLine($"HTML report written: {new HtmlReporter().Write(result.Films, data.ReportsFolder)}");

Console.WriteLine("\nDone.");
return 0;
