using FilmExpiryChecker.Storage;

namespace FilmExpiryChecker.Web;

/// <summary>
/// Writes the app's log to the data folder's logs folder, one file a day. Run by the launcher the app has no
/// console anyone sees, so this is where "it didn't refresh" gets explained.
/// </summary>
public sealed class FileLoggerProvider(DataFolder data, TimeProvider time) : ILoggerProvider
{
    private readonly Lock _gate = new();

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose() { }

    private void Write(LogLevel level, string category, string message, Exception? exception)
    {
        var now = time.GetLocalNow();
        var line = $"{now:HH:mm:ss.fff} [{level}] {category}: {message}{Environment.NewLine}";
        if (exception is not null) line += exception + Environment.NewLine;

        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(data.LogsFolder);
                File.AppendAllText(Path.Combine(data.LogsFolder, $"app-{now:yyyy-MM-dd}.log"), line);
            }
            catch (IOException)
            {
                // Logging must never take the app down.
            }
        }
    }

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) =>
            logLevel >= LogLevel.Warning || (logLevel >= LogLevel.Information && !category.StartsWith("Microsoft."));

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
                provider.Write(logLevel, category, formatter(state, exception), exception);
        }
    }
}
