using FilmExpiryChecker.Storage;
using FilmExpiryChecker.Web;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace FilmExpiryChecker.Tests;

/// <summary>
/// The web app in memory, on a temporary data folder that imports nothing, so a test never reads or writes the
/// real %LOCALAPPDATA% folder or picks up the real settings from the repo. Its network is a fake (by default one
/// that answers nothing), and its waits end at once.
/// </summary>
internal sealed class TestApp : WebApplicationFactory<Program>
{
    private readonly TempFolder? _ownTemp;
    private readonly string _dataRoot;
    private readonly HttpMessageHandler _network;
    private readonly TimeProvider _time;
    private readonly bool _autoRefreshOnStart;

    /// <param name="dataRoot">A data folder the test owns (and so outlives the app); otherwise a temporary one.</param>
    /// <param name="autoRefreshOnStart">Whether starting the app may start a refresh by itself (off, so tests start their own).</param>
    public TestApp(HttpMessageHandler? network = null, string? dataRoot = null, TimeProvider? time = null, bool autoRefreshOnStart = false)
    {
        if (dataRoot is null) _ownTemp = new TempFolder();
        _dataRoot = dataRoot ?? _ownTemp!.Path;
        _network = network ?? new FakeHttp(_ => FakeHttp.NotFound());
        _time = time ?? InstantWaits.Instance;
        _autoRefreshOnStart = autoRefreshOnStart;
    }

    public DataFolder Data => new(_dataRoot);

    /// <summary>Writes settings before the app starts, with the fake sites' user and Netflix as my service.</summary>
    public TestApp WithUser(string username = "example-user")
    {
        Data.Prepare([]);
        var settings = SettingsStore.Load(Data.SettingsFile);
        settings.Letterboxd.Username = username;
        settings.JustWatch.MyServices = ["8"];
        SettingsStore.Save(Data.SettingsFile, settings);
        return this;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder) =>
        builder.ConfigureTestServices(services =>
        {
            services.Replace(ServiceDescriptor.Singleton(Data));
            services.Replace(ServiceDescriptor.Singleton(new LegacyFolders([])));
            services.Replace(ServiceDescriptor.Singleton(new HttpClient(_network)));
            services.Replace(ServiceDescriptor.Singleton(_time));
            if (!_autoRefreshOnStart)
                foreach (var onStart in services.Where(d => d.ServiceType == typeof(IHostedService)
                             && d.ImplementationType?.Name == "AutoRefreshOnStart").ToList())
                    services.Remove(onStart);
        });

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) _ownTemp?.Dispose();
    }
}
