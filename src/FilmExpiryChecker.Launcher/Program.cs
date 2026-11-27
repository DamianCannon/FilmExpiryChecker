using FilmExpiryChecker.Launching;
// Film Expiry Checker opened with one click. `FilmExpiryChecker.Launcher.exe` opens it; `--install` makes its
// Start menu and desktop shortcuts; anything after `--` goes to the app (another port or database, for
// trying it by hand without touching real data).
namespace FilmExpiryChecker.Launcher;

internal static class Program
{
    // Windows Forms and WebView2 need the main thread single-threaded for COM: WebView2 crashes without it.
    [STAThread]
    private static int Main(string[] args)
    {
        // Before any window: the taskbar groups this process's windows under the pinned shortcut.
        NativeMethods.SetCurrentProcessExplicitAppUserModelID(NativeMethods.AppId);
        var launcherArguments = args.TakeWhile(a => a != "--").ToList();
        var appArguments = args.SkipWhile(a => a != "--").Skip(1).ToList();
        // The app's own data folder (or the one given to it with --data): logs, browser profile and window place.
        var folder = Launch.DataFolder(appArguments, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        var logs = Path.Combine(folder, "logs");
        Directory.CreateDirectory(logs);
        // No console in a WinExe: every step goes to a file, or "it didn't open" can't be diagnosed.
        var launcherLog = Path.Combine(logs, $"launcher-{DateTime.Now:yyyy-MM-dd}.log");
        void Log(string line) => File.AppendAllText(launcherLog, $"{DateTime.Now:HH:mm:ss} {line.TrimEnd()}{Environment.NewLine}");

        if (launcherArguments.Contains("--install"))
        {
            foreach (var made in Shortcuts.Make(Environment.ProcessPath!)) Log($"Shortcut made: {made}");
            return 0;
        }

        // Once: clicked again while open, the window already open is brought forward.
        using var once = new Mutex(true, @"Local\FilmExpiryChecker.Launcher", out var first);
        using var bringForward = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\FilmExpiryChecker.Launcher.Forward");
        if (!first)
        {
            // Without this, Windows' foreground lock leaves the other window flashing in the taskbar.
            NativeMethods.AllowSetForegroundWindow(NativeMethods.AnyProcess);
            bringForward.Set();
            return 0;
        }

        ApplicationConfiguration.Initialize();
        using var icon = new Icon(typeof(Program).Assembly.GetManifestResourceStream("app.ico")!);
        using var context = new Opening(Repository.Root(AppContext.BaseDirectory), appArguments, folder, logs, icon, bringForward, Log);
        Application.Run(context);
        return 0;
    }
}

/// <summary>The repository the launcher was built in: the solution's folder, found from the launcher's own upwards.</summary>
internal static class Repository
{
    public static string? Root(string from)
    {
        for (var dir = new DirectoryInfo(from); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "FilmExpiryChecker.slnx"))) return dir.FullName;
        return null;
    }
}

/// <summary>
/// From the click to the window, and from closing it to the app stopped. The starting window shows each
/// step; what's found decides what's done; the app is rebuilt if its code changed; and it's stopped when
/// its window closes, but only if the launcher started it.
/// </summary>
internal sealed class Opening : ApplicationContext
{
    private readonly StartingWindow _starting;
    private readonly TheApp? _app;
    private readonly string _folder;
    private readonly string _logs;
    private readonly Icon _icon;
    private readonly Action<string> _log;
    private readonly RegisteredWaitHandle _forward;
    private AppWindow? _window;
    private bool _started;
    private bool _closingDown;

    public Opening(string? root, IReadOnlyList<string> appArguments, string folder, string logs, Icon icon, EventWaitHandle bringForward, Action<string> log)
    {
        (_folder, _logs, _icon, _log) = (folder, logs, icon, log);
        _starting = new StartingWindow(icon);
        // Closed while the app it started is still starting: the app is stopped too, not left running with
        // no window (found in a trial: an orphaned app, and every later click opened onto it and never stopped it).
        _starting.FormClosing += async (_, e) =>
        {
            if (_window is not null || _closingDown || !_started || _app is null) return;
            e.Cancel = true;
            _closingDown = true;
            _starting.Step($"Stopping {Launch.AppName}…");
            await _app.StopAsync();
            ExitThread();
        };
        _starting.FormClosed += (_, _) => { if (_window is null) ExitThread(); };
        _app = root is null ? null : new TheApp(root, appArguments, log);
        _forward = ThreadPool.RegisterWaitForSingleObject(bringForward, (_, _) => _starting.BeginInvoke(() =>
        {
            if (_window is { Visible: true }) _window.BringForward();
            else _starting.Activate();
        }), null, -1, false);
        _starting.Shown += async (_, _) => await OpenAsync();
        _starting.Show();
    }

    private async Task OpenAsync()
    {
        if (_app is null)
        {
            _starting.Stopped($"{Launch.AppName}'s folder can't be found from where this launcher is. Build it there again.");
            return;
        }
        _starting.Step("Checking…");
        var down = await TheApp.PrerequisiteDownAsync();
        var launch = Launch.Decide(await _app.AnsweringAsync(), down);
        _log($"Opening: {launch}");
        switch (launch)
        {
            case NextStep.PrerequisiteDown:
                _starting.Stopped(down!);
                return;
            case NextStep.PortTaken:
                _starting.Stopped($"Something other than {Launch.AppName} is using its address ({_app.Address.Authority}). Close it, then try again.");
                return;
            case NextStep.StartTheApp:
                if (_app.NeedsRebuilding())
                {
                    _starting.Step($"Updating {Launch.AppName} to the latest code: rebuilding…");
                    if (await _app.RebuildAsync() is { } failed)
                    {
                        _log($"Rebuild failed: {failed}");
                        _starting.Step($"The latest code didn't build ({failed.Split(Environment.NewLine)[0]}), so the last build that worked is opening.");
                        await Task.Delay(TimeSpan.FromSeconds(4));
                    }
                }
                _starting.Step($"Starting {Launch.AppName}…");
                if (!_app.Start(Path.Combine(_logs, $"app-{DateTime.Now:yyyy-MM-dd-HHmm}.log")))
                {
                    _starting.Stopped($"{Launch.AppName} isn't built yet: run dotnet build -c Release once, then try again.");
                    return;
                }
                _started = true;
                var ready = await _app.ReadyAsync();
                if (_closingDown) return;
                if (!ready)
                {
                    _starting.Stopped($"{Launch.AppName} didn't start. Its log in {_logs} says why.");
                    return;
                }
                break;
        }
        _starting.Step("Opening…");
        _window = new AppWindow(_app.Address, _icon, _folder);
        _window.FormClosed += async (_, _) => await ClosedAsync();
        _window.Show();
        _starting.Hide();
    }

    // The window closed: the app is stopped if the launcher started it; one started some other way is left running.
    private async Task ClosedAsync()
    {
        if (_started && _app is not null)
        {
            _starting.Step($"Stopping {Launch.AppName}… If it has work under way, that finishes first.");
            _starting.Show();
            await _app.StopAsync();
            _log("Stopped.");
        }
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _forward.Unregister(null);
            _starting.Dispose();
            _window?.Dispose();
        }
        base.Dispose(disposing);
    }
}
