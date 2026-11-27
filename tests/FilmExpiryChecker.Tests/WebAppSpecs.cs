using System.Net;
using System.Text.RegularExpressions;
using FilmExpiryChecker.Storage;

namespace FilmExpiryChecker.Tests;

/// <summary>The web app answers, and is recognisable as itself (the desktop launcher matches the page title).</summary>
public sealed class WebAppSpecs : IDisposable
{
    private readonly TestApp _app = new();

    public void Dispose() => _app.Dispose();

    private static string Title(string html) =>
        Regex.Match(html, @"<title>\s*(.*?)\s*</title>", RegexOptions.Singleline).Groups[1].Value;

    [Fact]
    public async Task The_home_page_answers()
    {
        var response = await _app.CreateClient().GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/not-found")]
    [InlineData("/no-such-page")]
    public async Task Every_page_title_starts_with_the_apps_name(string path)
    {
        // A missing page is a 404, but still carries the app's title.
        var response = await _app.CreateClient().GetAsync(path);
        var html = await response.Content.ReadAsStringAsync();

        Assert.StartsWith("Film Expiry Checker", Title(html));
    }

    [Fact]
    public async Task The_page_has_a_header_a_main_area_and_a_status_bar()
    {
        var html = await _app.CreateClient().GetStringAsync("/");

        Assert.Contains("<header class=\"app-header\"", html);
        Assert.Contains("<main class=\"app-main\"", html);
        Assert.Contains("<footer class=\"status-bar\"", html);
    }

    [Theory]
    [InlineData("/counter")]
    [InlineData("/weather")]
    public async Task The_template_sample_pages_are_gone(string sample)
    {
        var response = await _app.CreateClient().GetAsync(sample);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task The_refresh_button_is_greyed_out_until_the_page_is_live()
    {
        // The first HTML is shown before the page can respond to clicks; a click then would be lost.
        var html = await _app.CreateClient().GetStringAsync("/");

        var button = Regex.Match(html, "<button[^>]*class=\"refresh-button\"[^>]*>").Value;
        Assert.Contains("disabled", button);
    }

    [Fact]
    public async Task The_page_names_its_icon_for_the_browser_tab()
    {
        var html = await _app.CreateClient().GetStringAsync("/");

        Assert.Matches("<link rel=\"icon\" href=\"favicon.ico\" sizes=\"any\"\\s*/?>", html);
        Assert.Matches("<link rel=\"apple-touch-icon\" href=\"icon-256.png\"\\s*/?>", html);
    }

    [Theory]
    [InlineData("/favicon.ico")]
    [InlineData("/icon-256.png")]
    public async Task The_icons_are_served(string path)
    {
        var response = await _app.CreateClient().GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True((await response.Content.ReadAsByteArrayAsync()).Length > 1000);
    }

    [Fact]
    public void The_launcher_and_the_browser_tab_use_the_same_icon()
    {
        var repo = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(repo, "FilmExpiryChecker.slnx"))) repo = Path.GetDirectoryName(repo)!;

        var launcher = File.ReadAllBytes(Path.Combine(repo, "src", "FilmExpiryChecker.Launcher", "app.ico"));
        var favicon = File.ReadAllBytes(Path.Combine(repo, "src", "FilmExpiryChecker.Web", "wwwroot", "favicon.ico"));

        Assert.Equal(launcher, favicon);
        Assert.Equal(new byte[] { 0, 0, 1, 0, 6, 0 }, launcher[..6]);   // an .ico holding six sizes
    }

    [Fact]
    public async Task Starting_the_app_makes_the_data_folder_ready()
    {
        await _app.CreateClient().GetAsync("/");

        Assert.True(File.Exists(_app.Data.SettingsFile));
        Assert.Equal("GB", SettingsStore.Load(_app.Data.SettingsFile).JustWatch.Region);
        Assert.True(Directory.Exists(_app.Data.RunsFolder));
    }
}
