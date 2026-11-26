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
    public async Task Starting_the_app_makes_the_data_folder_ready()
    {
        await _app.CreateClient().GetAsync("/");

        Assert.True(File.Exists(_app.Data.SettingsFile));
        Assert.Equal("GB", SettingsStore.Load(_app.Data.SettingsFile).JustWatch.Region);
        Assert.True(Directory.Exists(_app.Data.RunsFolder));
    }
}
