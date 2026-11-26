using FilmExpiryChecker.Running;
using Microsoft.Extensions.Logging;

namespace FilmExpiryChecker.Cli;

/// <summary>Prints a run's progress the way the command line always has, on the thread that reports it.</summary>
internal sealed class ConsoleProgress(string username) : IProgress<RunProgress>
{
    private int _filmsBefore;

    public void Report(RunProgress p)
    {
        switch (p.Step)
        {
            case RunStep.ReadingWatchlist when p.Done == 0:
                Console.WriteLine($"Fetching Letterboxd watchlist for {username}...");
                break;
            case RunStep.ReadingWatchlist:
                Console.WriteLine($"  Page {p.Done}... {p.Films - _filmsBefore} films.");
                _filmsBefore = p.Films;
                break;
            case RunStep.FetchingServices:
                Console.WriteLine();
                Console.WriteLine("Fetching JustWatch provider list...");
                break;
            case RunStep.CheckingFilms when p.Done == 0:
                Console.WriteLine();
                Console.WriteLine($"Enriching {p.Total} films via JustWatch...");
                break;
            case RunStep.CheckingFilms when p.Done % 25 == 0 || p.Done == p.Total:
                Console.WriteLine($"  {p.Done}/{p.Total} films processed...");
                break;
        }
    }
}

/// <summary>Writes log messages as plain indented lines, without the usual "info: Category[0]" prefix.</summary>
internal sealed class PlainConsoleLoggerFactory : ILoggerFactory
{
    public ILogger CreateLogger(string categoryName) => new PlainConsoleLogger();

    public void AddProvider(ILoggerProvider provider) { }

    public void Dispose() { }

    private sealed class PlainConsoleLogger : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var prefix = logLevel >= LogLevel.Warning ? "  Warning: " : "  ";
            Console.WriteLine(prefix + formatter(state, exception));
        }
    }
}
