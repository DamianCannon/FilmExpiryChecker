using FilmExpiryChecker.Launching;

namespace FilmExpiryChecker.Tests;

/// <summary>The desktop launcher's decisions, free of windows and processes.</summary>
public sealed class LaunchingSpecs : IDisposable
{
    private readonly TempFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    // ── What answers on the address ─────────────────────────────────────────────

    [Fact] public void Nothing_answering_is_nothing() => Assert.Equal(Answering.Nothing, Launch.Of(null));

    [Theory]
    [InlineData("<html><head><title>Film Expiry Checker</title></head></html>")]
    [InlineData("<title>Film Expiry Checker — Settings</title>")]
    [InlineData("<title>\n  film expiry checker</title>")]
    public void A_page_titled_with_the_apps_name_is_ours(string page) => Assert.Equal(Answering.Ours, Launch.Of(page));

    [Theory]
    [InlineData("<title>Home</title>")]
    [InlineData("<title>Film Expiry Checkerboard</title>")]
    [InlineData("<html>no title at all</html>")]
    public void Any_other_page_is_something_else(string page) => Assert.Equal(Answering.SomethingElse, Launch.Of(page));

    // ── What to do about it ─────────────────────────────────────────────────────

    [Fact] public void Ours_already_running_is_opened_onto() => Assert.Equal(NextStep.OpenOntoIt, Launch.Decide(Answering.Ours, null));
    [Fact] public void Something_else_on_the_port_stops_it() => Assert.Equal(NextStep.PortTaken, Launch.Decide(Answering.SomethingElse, null));
    [Fact] public void A_prerequisite_down_stops_it() => Assert.Equal(NextStep.PrerequisiteDown, Launch.Decide(Answering.Nothing, "PostgreSQL isn't running."));
    [Fact] public void Nothing_answering_and_nothing_down_starts_the_app() => Assert.Equal(NextStep.StartTheApp, Launch.Decide(Answering.Nothing, null));

    // ── The app's address and arguments ─────────────────────────────────────────

    [Fact] public void With_no_address_given_it_is_the_apps_own() => Assert.Equal(new Uri("http://127.0.0.1:5187/"), Launch.Address([]));

    [Fact]
    public void An_address_given_after_urls_is_used() =>
        Assert.Equal(new Uri("http://127.0.0.1:5197/"), Launch.Address(["--data", @"C:\x", "--urls", "http://127.0.0.1:5197"]));

    [Fact]
    public void The_first_of_several_addresses_is_used() =>
        Assert.Equal(new Uri("http://127.0.0.1:5198/"), Launch.Address(["--urls=http://127.0.0.1:5198/;http://localhost:5199"]));

    [Fact]
    public void The_apps_own_address_is_added_when_none_is_given() =>
        Assert.Equal(new[] { "--data", @"C:\x", "--urls", "http://127.0.0.1:5187" }, Launch.WithAddress(["--data", @"C:\x"]));

    [Fact]
    public void An_address_given_is_not_doubled() =>
        Assert.Equal(new[] { "--urls", "http://127.0.0.1:5197" }, Launch.WithAddress(["--urls", "http://127.0.0.1:5197"]));

    // ── Where its files go ──────────────────────────────────────────────────────

    [Fact]
    public void By_default_the_launcher_keeps_its_files_in_the_apps_data_folder() =>
        Assert.Equal(@"C:\Users\someone\AppData\Local\FilmExpiryChecker", Launch.DataFolder([], @"C:\Users\someone\AppData\Local"));

    [Fact]
    public void A_data_folder_given_to_the_app_is_the_launchers_too() =>
        Assert.Equal(@"D:\test-data", Launch.DataFolder(["--urls", "http://127.0.0.1:5197", "--data", @"D:\test-data"], @"C:\Local"));

    // ── Whether to rebuild first ────────────────────────────────────────────────

    private static readonly DateTime Built = new(2026, 11, 27, 9, 0, 0, DateTimeKind.Utc);

    [Fact] public void Never_built_means_rebuild() => Assert.True(Launch.NeedsRebuilding([], builtUtc: null));

    [Fact]
    public void Code_changed_after_the_build_means_rebuild() =>
        Assert.True(Launch.NeedsRebuilding([(@"src\FilmExpiryChecker.Web\Components\Pages\Home.razor", Built.AddMinutes(1))], Built));

    [Fact]
    public void Nothing_changed_since_the_build_means_no_rebuild() =>
        Assert.False(Launch.NeedsRebuilding([(@"src\FilmExpiryChecker.Core\Running\CheckRunner.cs", Built.AddMinutes(-5))], Built));

    [Theory]
    [InlineData(@"src\FilmExpiryChecker.Web\bin\Release\net10.0\FilmExpiryChecker.Web.dll")]
    [InlineData(@"src\FilmExpiryChecker.Web\obj\project.assets.json")]
    [InlineData(@"src\FilmExpiryChecker.Launcher\Program.cs")]
    [InlineData(@"src\FilmExpiryChecker.Launching\Launch.cs")]
    [InlineData(@"src\FilmExpiryChecker.Web\notes.md")]
    public void Changes_that_arent_the_apps_code_dont_mean_rebuild(string path) =>
        Assert.False(Launch.NeedsRebuilding([(path, Built.AddMinutes(1))], Built));

    [Fact]
    public void The_sources_are_the_code_under_src_except_build_output_and_the_launcher()
    {
        void Touch(string relative)
        {
            var path = Path.Combine(_temp.Path, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "");
        }
        Touch(@"src\FilmExpiryChecker.Web\Components\App.razor");
        Touch(@"src\FilmExpiryChecker.Core\Storage\RunStore.cs");
        Touch(@"src\FilmExpiryChecker.Web\bin\Release\x.cs");
        Touch(@"src\FilmExpiryChecker.Launcher\Program.cs");
        Touch(@"tests\FilmExpiryChecker.Tests\HistorySpecs.cs");
        Touch(@"Directory.Build.props");

        var sources = Launch.Sources(_temp.Path).Select(s => s.Path).Order().ToList();

        Assert.Equal(new[]
        {
            "Directory.Build.props",
            @"src\FilmExpiryChecker.Core\Storage\RunStore.cs",
            @"src\FilmExpiryChecker.Web\Components\App.razor",
        }, sources);
    }
}
