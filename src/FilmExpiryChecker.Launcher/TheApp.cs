using FilmExpiryChecker.Launching;
using System.Diagnostics;
using System.Net.Sockets;

namespace FilmExpiryChecker.Launcher;

/// <summary>
/// The app as the launcher handles it: found already running, rebuilt if its code changed, started as
/// its built exe (its output to a log), and stopped gently, as Ctrl+C does, so work under way finishes.
/// </summary>
internal sealed class TheApp(string root, IReadOnlyList<string> appArguments, Action<string> log)
{
    /// <summary>The web project's folder, from the repo root.</summary>
    private const string WebProjectPath = "src/FilmExpiryChecker.Web";

    /// <summary>The web project's assembly name: its exe is this, .exe.</summary>
    private const string WebAssembly = "FilmExpiryChecker.Web";

    /// <summary>The projects rebuilt before it opens, in order, from the repo root (add any tool that must match the app's code).</summary>
    private static readonly string[] RebuildProjects = [WebProjectPath];

    /// <summary>What must answer before the app can start, and what to tell the user if it doesn't. Delete any that don't apply.</summary>
    public static readonly (string Name, string Host, int Port, string Fix)[] Prerequisites =
    [
        // ("PostgreSQL", "127.0.0.1", 5432, "Start it (the postgresql-17 service), then try again."),
    ];

    // A first page from an app idle a while can take some seconds (found by hand).
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    /// <summary>Longer than the app's own HostOptions.ShutdownTimeout, so its background work finishes.</summary>
    /// <remarks>The app gives a refresh under way 30 s to stop and save what it has (its HostOptions.ShutdownTimeout).</remarks>
    private static readonly TimeSpan StopWithin = TimeSpan.FromSeconds(45);

    private Process? _process;
    private bool _ready;

    public Uri Address { get; } = Launch.Address(appArguments);

    private string ProjectFolder => Path.GetFullPath(Path.Combine(root, WebProjectPath));

    /// <summary>The newest Release build of the app, or null if it's never been built.</summary>
    private string? Exe => Directory.Exists(Path.Combine(ProjectFolder, "bin", "Release"))
        ? Directory.EnumerateFiles(Path.Combine(ProjectFolder, "bin", "Release"), WebAssembly + ".exe", SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()
        : null;

    /// <summary>What answers on the app's address: nothing listening is known at once; something listening is given time.</summary>
    public async Task<Answering> AnsweringAsync()
    {
        if (!await ListeningAsync(Address.Host, Address.Port)) return Answering.Nothing;
        try
        {
            return Launch.Of(await Http.GetStringAsync(Address));
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return Answering.Nothing;
        }
    }

    /// <summary>The first prerequisite that doesn't answer, said plainly; null if all do.</summary>
    public static async Task<string?> PrerequisiteDownAsync()
    {
        foreach (var (name, host, port, fix) in Prerequisites)
            if (!await ListeningAsync(host, port)) return $"{name} isn't running, so {Launch.AppName} can't start. {fix}";
        return null;
    }

    private static async Task<bool> ListeningAsync(string host, int port)
    {
        using var client = new TcpClient();
        try
        {
            await client.ConnectAsync(host, port).WaitAsync(TimeSpan.FromSeconds(2));
            return true;
        }
        catch (Exception e) when (e is SocketException or TimeoutException)
        {
            return false;
        }
    }

    private string Stamp => Path.Combine(ProjectFolder, "bin", "Release", ".launcher-built");

    /// <summary>Whether its code changed since it was last built (by the launcher or anyone).</summary>
    public bool NeedsRebuilding()
    {
        DateTime? built = Exe is { } exe ? File.GetLastWriteTimeUtc(Path.ChangeExtension(exe, ".dll")) : null;
        if (built is { } at && File.Exists(Stamp) && File.GetLastWriteTimeUtc(Stamp) > at) built = File.GetLastWriteTimeUtc(Stamp);
        return Launch.NeedsRebuilding(Launch.Sources(root), built);
    }

    /// <summary>Rebuilds Release; null if it built, else the first errors.</summary>
    public async Task<string?> RebuildAsync()
    {
        foreach (var project in RebuildProjects)
        {
            var (code, output) = await RunAsync("dotnet", ["build", "-c", "Release", "--nologo", "-v", "q", project]);
            log(output);
            if (code != 0)
                return string.Join(Environment.NewLine, output.Split('\n').Where(l => l.Contains("error", StringComparison.OrdinalIgnoreCase)).Select(l => l.Trim()).Distinct().Take(3));
        }
        // A source touched but not changed in the output would otherwise look stale forever.
        await File.WriteAllTextAsync(Stamp, DateTime.UtcNow.ToString("O"));
        return null;
    }

    /// <summary>Starts the built app; false if there's no build to start.</summary>
    public bool Start(string logFile)
    {
        if (Exe is not { } exe) return false;
        var start = new ProcessStartInfo(exe)
        {
            // Its content root, as `dotnet run` gives it: appsettings.json and wwwroot are found there.
            WorkingDirectory = ProjectFolder,
            // A hidden console of its own, so it can be sent Ctrl+C. Never shell-execute or detach it.
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in Launch.WithAddress(appArguments)) start.ArgumentList.Add(argument);
        _process = Process.Start(start)!;
        var writer = new StreamWriter(logFile, append: true) { AutoFlush = true };
        _process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (writer) writer.WriteLine(e.Data); };
        _process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (writer) writer.WriteLine(e.Data); };
        _process.Exited += (_, _) => { lock (writer) writer.Dispose(); };
        _process.EnableRaisingEvents = true;
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
        return true;
    }

    /// <summary>Waits till the app answers; false if it stopped, or didn't answer within a minute and a half.</summary>
    public async Task<bool> ReadyAsync()
    {
        for (var tries = 0; tries < 180; tries++)
        {
            if (_process is { HasExited: true }) return false;
            // Its own, still running: not another one that happens to answer (found by hand).
            if (await AnsweringAsync() == Answering.Ours) return _ready = _process is { HasExited: false };
            await Task.Delay(500);
        }
        return false;
    }

    /// <summary>Stops the app it started, as Ctrl+C does, and waits; killed only if it won't stop.</summary>
    public async Task StopAsync()
    {
        if (_process is not { HasExited: false } process) return;
        // Ready, it's asked once. Still starting, its console or its Ctrl+C handler may not be there yet
        // (found in a trial: one early Ctrl+C was lost), so it's asked each second for a little while.
        for (var tries = 0; tries < (_ready ? 1 : 10) && !process.HasExited; tries++)
        {
            if (!SendCtrlC(process.Id)) log($"Couldn't reach the app's console to ask it to stop (try {tries + 1}).");
            if (_ready) break;
            try
            {
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(1));
            }
            catch (TimeoutException)
            {
                // Asked again.
            }
        }
        try
        {
            await process.WaitForExitAsync().WaitAsync(StopWithin);
        }
        catch (TimeoutException)
        {
            log("The app didn't stop when asked: stopped outright.");
            process.Kill();
        }
        finally
        {
            NativeMethods.SetConsoleCtrlHandler(IntPtr.Zero, false);
        }
    }

    /// <summary>Ctrl+C to the app's hidden console, the launcher ignoring it itself; false if the console couldn't be reached.</summary>
    private static bool SendCtrlC(int processId)
    {
        NativeMethods.FreeConsole();
        if (!NativeMethods.AttachConsole((uint)processId)) return false;
        NativeMethods.SetConsoleCtrlHandler(IntPtr.Zero, true);
        NativeMethods.GenerateConsoleCtrlEvent(NativeMethods.CtrlC, 0);
        NativeMethods.FreeConsole();
        return true;
    }

    private async Task<(int Code, string Output)> RunAsync(string file, IEnumerable<string> arguments)
    {
        var start = new ProcessStartInfo(file) { WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var errors = process.StandardError.ReadToEndAsync();
        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, output + await errors);
    }
}
