using System.Text.RegularExpressions;

namespace FilmExpiryChecker.Launching;

/// <summary>What answers on the app's address.</summary>
public enum Answering { Nothing, Ours, SomethingElse }

/// <summary>What a click does once the window isn't already open.</summary>
public enum NextStep { OpenOntoIt, StartTheApp, PrerequisiteDown, PortTaken }

/// <summary>
/// The desktop launcher's decisions, kept free of windows and processes so they can be specified (from the
/// blazor-desktop-app skill's templates).
/// </summary>
public static class Launch
{
    public const string AppName = "Film Expiry Checker";

    /// <summary>The app's own address, as its appsettings.json has it.</summary>
    public static readonly Uri Usual = new("http://127.0.0.1:5187/");

    /// <summary>From the page the address served (null if nothing answered). Ours if its title starts with the app's name.</summary>
    public static Answering Of(string? page) =>
        page is null ? Answering.Nothing
        : Regex.IsMatch(page, @"<title>\s*" + Regex.Escape(AppName) + @"\b", RegexOptions.IgnoreCase) ? Answering.Ours
        : Answering.SomethingElse;

    /// <summary>From what answers, and the first prerequisite found down (if any).</summary>
    public static NextStep Decide(Answering answering, string? prerequisiteDown) =>
        answering == Answering.Ours ? NextStep.OpenOntoIt
        : answering == Answering.SomethingElse ? NextStep.PortTaken
        : prerequisiteDown is not null ? NextStep.PrerequisiteDown
        : NextStep.StartTheApp;

    /// <summary>The address to open: the one given to the app (--urls), else its own.</summary>
    public static Uri Address(IReadOnlyList<string> appArguments)
    {
        ArgumentNullException.ThrowIfNull(appArguments);
        for (var i = 0; i < appArguments.Count; i++)
        {
            if (appArguments[i] == "--urls" && i + 1 < appArguments.Count) return First(appArguments[i + 1]);
            if (appArguments[i].StartsWith("--urls=", StringComparison.Ordinal)) return First(appArguments[i]["--urls=".Length..]);
        }
        return Usual;

        static Uri First(string urls) => new(urls.Split(';')[0].TrimEnd('/') + "/");
    }

    /// <summary>
    /// The arguments the app is started with: its own address added unless one was given. The built exe never
    /// reads launchSettings.json; the app's appsettings.json has the address too, but saying it here makes sure.
    /// </summary>
    public static IReadOnlyList<string> WithAddress(IReadOnlyList<string> appArguments)
    {
        ArgumentNullException.ThrowIfNull(appArguments);
        return appArguments.Any(a => a == "--urls" || a.StartsWith("--urls=", StringComparison.Ordinal))
            ? appArguments
            : [.. appArguments, "--urls", Usual.GetLeftPart(UriPartial.Authority)];
    }

    /// <summary>
    /// Where the launcher keeps its logs, browser profile and window place: the app's own data folder, or the one
    /// given to the app with --data (so a hand test with a temporary folder keeps everything there).
    /// </summary>
    public static string DataFolder(IReadOnlyList<string> appArguments, string localAppData)
    {
        ArgumentNullException.ThrowIfNull(appArguments);
        for (var i = 0; i < appArguments.Count - 1; i++)
            if (appArguments[i] == "--data") return appArguments[i + 1];
        return Path.Combine(localAppData, "FilmExpiryChecker");
    }

    // ---- Rebuild first ----

    /// <summary>The folders holding the app's code, from the repo root.</summary>
    private static readonly string[] CodeFolders = ["src"];

    /// <summary>Folders whose files aren't the app's code: what builds write, and the launcher's own (it can't rebuild itself).</summary>
    private static readonly string[] NotTheApps = ["bin", "obj", "node_modules", ".git", ".vs", "FilmExpiryChecker.Launcher", "FilmExpiryChecker.Launching"];

    private static readonly string[] Kinds = [".cs", ".razor", ".cshtml", ".css", ".js", ".json", ".sql", ".csproj", ".props", ".targets", ".svg", ".html", ".png", ".ico"];

    /// <summary>Whether any source was written after the build (none: never built).</summary>
    public static bool NeedsRebuilding(IEnumerable<(string Path, DateTime WrittenUtc)> sources, DateTime? builtUtc) =>
        builtUtc is not { } built || sources.Any(s => Counts(s.Path) && s.WrittenUtc > built);

    /// <summary>The files in the code folders (skipping what builds write), and the repo's build settings.</summary>
    public static IEnumerable<(string Path, DateTime WrittenUtc)> Sources(string root)
    {
        var skip = new EnumerationOptions { RecurseSubdirectories = false };
        var folders = new Stack<string>(CodeFolders.Select(f => Path.GetFullPath(Path.Combine(root, f))).Where(Directory.Exists));
        while (folders.TryPop(out var folder))
        {
            foreach (var file in Directory.EnumerateFiles(folder, "*", skip))
                yield return (Path.GetRelativePath(root, file), File.GetLastWriteTimeUtc(file));
            foreach (var sub in Directory.EnumerateDirectories(folder, "*", skip))
                if (!NotTheApps.Contains(Path.GetFileName(sub), StringComparer.OrdinalIgnoreCase)) folders.Push(sub);
        }
        foreach (var file in Directory.EnumerateFiles(root, "Directory.*.props"))
            yield return (Path.GetFileName(file), File.GetLastWriteTimeUtc(file));
    }

    private static bool Counts(string path) =>
        !path.Split('\\', '/').Any(p => NotTheApps.Contains(p, StringComparer.OrdinalIgnoreCase))
        && Kinds.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
}
