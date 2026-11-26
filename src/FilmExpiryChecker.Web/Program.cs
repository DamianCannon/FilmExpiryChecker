using FilmExpiryChecker.Storage;
using FilmExpiryChecker.Web;
using FilmExpiryChecker.Web.Components;

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
