using FilmExpiryChecker.Storage;
using FilmExpiryChecker.Web;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FilmExpiryChecker.Tests;

/// <summary>
/// The web app in memory, on a temporary data folder that imports nothing, so a test never reads or writes the
/// real %LOCALAPPDATA% folder or picks up the real settings from the repo.
/// </summary>
internal sealed class TestApp : WebApplicationFactory<Program>
{
    private readonly TempFolder _temp = new();
    private readonly Action<IServiceCollection>? _services;

    public TestApp(Action<IServiceCollection>? services = null) => _services = services;

    public DataFolder Data => new(_temp.Path);

    protected override void ConfigureWebHost(IWebHostBuilder builder) =>
        builder.ConfigureTestServices(services =>
        {
            services.Replace(ServiceDescriptor.Singleton(Data));
            services.Replace(ServiceDescriptor.Singleton(new LegacyFolders([])));
            _services?.Invoke(services);
        });

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) _temp.Dispose();
    }
}
