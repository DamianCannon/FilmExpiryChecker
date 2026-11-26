using FilmExpiryChecker.Storage;
using FilmExpiryChecker.Web;
using FilmExpiryChecker.Web.Components;
using FilmExpiryChecker.Web.Refresh;

var builder = WebApplication.CreateBuilder(args);

// Run as its built exe (by the launcher) the app is in Production, where the framework's own static files
// (_framework/blazor.web.js) aren't mapped without this: the page would load but never come alive.
builder.WebHost.UseStaticWebAssets();

// Everything the app keeps lives in one data folder: --data <path>, FILMEXPIRYCHECKER_DATA, or %LOCALAPPDATA%.
builder.Services.AddSingleton(DataFolder.Resolve(args, Environment.GetEnvironmentVariable,
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)));
builder.Services.AddSingleton(new LegacyFolders(LegacyFiles.Folders(AppContext.BaseDirectory, Environment.CurrentDirectory)));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHostedService<DataFolderSetup>();

// The app's log goes to the data folder's logs folder: run by the launcher, nobody sees a console.
builder.Services.AddSingleton<ILoggerProvider, FileLoggerProvider>();

// One HTTP client for Letterboxd and JustWatch, introducing itself as a browser, as the CLI does.
builder.Services.AddSingleton(_ =>
{
    var http = new HttpClient();
    http.DefaultRequestHeaders.Add("User-Agent",
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
    return http;
});

// The refresh runs in the background; closing the app waits up to 30 s for it to stop and save what it has.
builder.Services.AddSingleton<LatestResults>();
builder.Services.AddSingleton<RefreshService>();
builder.Services.AddSingleton<IRefreshStatus>(services => services.GetRequiredService<RefreshService>());
builder.Services.AddHostedService(services => services.GetRequiredService<RefreshService>());

// Once a day: on starting, and whenever a page opens, refresh if the last success wasn't today.
builder.Services.AddSingleton<AutoRefresh>();
builder.Services.AddHostedService<AutoRefreshOnStart>();
builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(30));

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

/// <summary>Visible to the tests' WebApplicationFactory.</summary>
public partial class Program;
