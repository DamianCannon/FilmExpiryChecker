using System.Net;
using System.Text;

namespace FilmExpiryChecker.Tests;

/// <summary>A clock stopped at one moment, so "now" never moves under a test.</summary>
internal sealed class FixedClock(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

/// <summary>
/// Real time, except that every wait (the polite pauses between requests) ends at once, so tests don't sit idle.
/// </summary>
internal sealed class InstantWaits : TimeProvider
{
    public static readonly InstantWaits Instance = new();

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        if (dueTime != Timeout.InfiniteTimeSpan)
            ThreadPool.UnsafeQueueUserWorkItem(_ => callback(state), null);
        return new Fired();
    }

    private sealed class Fired : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => true;
        public void Dispose() { }
        public ValueTask DisposeAsync() => default;
    }
}

/// <summary>Stands in for the network: answers each request from a function and records what was asked.</summary>
internal sealed class FakeHttp(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<string> RequestedUrls { get; } = new();
    public List<string> RequestBodies { get; } = new();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        RequestedUrls.Add(request.RequestUri!.ToString());
        RequestBodies.Add(request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(ct));
        return respond(request);
    }

    public static HttpResponseMessage Html(string html) =>
        new(HttpStatusCode.OK) { Content = new StringContent(html, Encoding.UTF8, "text/html") };

    public static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage NotFound() => new(HttpStatusCode.NotFound);
}

/// <summary>Records progress reports on the reporting thread, in order (unlike <see cref="Progress{T}"/>).</summary>
internal sealed class RecordedProgress<T> : IProgress<T>
{
    private readonly Action<T>? _also;

    public RecordedProgress(Action<T>? also = null) => _also = also;

    public List<T> Reports { get; } = new();

    public void Report(T value)
    {
        Reports.Add(value);
        _also?.Invoke(value);
    }
}

internal static class Fixture
{
    public static string Read(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));
}

/// <summary>A folder of its own for one test, removed afterwards.</summary>
internal sealed class TempFolder : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), "FilmExpiryChecker.Tests", Guid.NewGuid().ToString("N"));

    public TempFolder() => Directory.CreateDirectory(Path);

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
    }
}
