# Film Expiry Checker

Which films on your [Letterboxd](https://letterboxd.com) watchlist can you stream right now, and which are about to leave?

Film Expiry Checker reads your public watchlist, looks each film up on [JustWatch](https://www.justwatch.com), and sorts them:

| Group | Means |
|---|---|
| 🚨 Leaving soon — your services | On a service you have (or a free one), but leaving within your "leaving soon" window |
| ⚠️ Leaving soon — other services | Leaving soon, but only from services you don't have |
| ✅ Available on your services | Yours to watch: on a service you have, or free |
| 📺 Available elsewhere only | Streaming, but only on services you don't have |
| 💷 Rent or buy only | Can only be rented or bought |
| ❌ Not on any streaming service | JustWatch knows it, but nobody streams it |
| ❓ Not found on JustWatch | No match on JustWatch |

Each service shows how it's offered: 📺 subscription, 🆓 free, 💷 rent or buy.

It runs on your own PC as a desktop app: one click opens it in its own window, pinned to the taskbar.

## Set up (Windows 11)

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download). WebView2, which draws the window, is already part of Windows 11.

1. Get the code and build it:
   ```
   git clone https://github.com/DamianCannon/FilmExpiryChecker.git
   cd FilmExpiryChecker
   dotnet build FilmExpiryChecker.slnx -c Release
   ```
2. Make the Start menu and desktop shortcuts:
   ```
   src\FilmExpiryChecker.Launcher\bin\Release\net10.0-windows\FilmExpiryChecker.Launcher.exe --install
   ```
   Run it from the **Release** build: the shortcuts point at whichever launcher made them. Run it again after a fresh clone.
3. Pin it: **Start → right-click Film Expiry Checker → Pin to taskbar**. (Windows 11 only lets you pin things yourself.)
4. Click it. The first time, it opens on **Settings**: enter your Letterboxd username (your watchlist must be public), pick your country, tick the services you have, and save. The first refresh starts straight away.

## Using it

- **It refreshes by itself once a day**, the first time you open it each day. **Refresh** in the header does one any time, for instance after adding films to your watchlist. The results you have stay on screen while it refreshes.
- **The status bar** at the bottom says how fresh the results are ("Updated today at 07:12 · 579 films · 20 leaving soon"), or how a refresh is getting on, or why the last one failed.
- **Settings**: change your services or the "leaving soon" window and the results regroup at once, with no refresh. A new username or country needs a refresh, which it offers.
- **History**: every refresh is kept; open any of them, download it as CSV, or delete it. Reports from the old command-line version are listed there too.
- Clicking a film's title opens it on Letterboxd in your usual browser.
- **Closing the window stops the app.** A refresh under way stops and keeps what it had done; the next one carries on from there.
- If you change the code, the next click rebuilds the app first. If the new code doesn't build, it says so and opens the last build that worked.

## Where your data lives

Everything is in `%LOCALAPPDATA%\FilmExpiryChecker`, away from the code, so rebuilding or cloning again never touches it:

| | |
|---|---|
| `settings.json` | Your settings |
| `cache\` | What JustWatch said about each film (kept for 7 days, so a refresh only looks up what's new) |
| `runs\` | Each finished refresh, in full |
| `reports\` | CSV and HTML reports from the command line |
| `logs\` | The app's log, the launcher's log, and the app's output |
| `WebView2\`, `window.json` | The window's browser profile, and its size and place |

The first time it runs, it copies in the settings, cache and reports from where the old command-line version kept them (it never moves or deletes those).

## The command line

The original command-line version still works, and shares the same data:

```
dotnet run --project src/FilmExpiryChecker.Cli                    # check, and write HTML and CSV reports
dotnet run --project src/FilmExpiryChecker.Cli -- --list-services # list your country's services and their IDs
```

Ctrl+C stops a check part-way, keeping the films done so far.

## Trying things safely

Every part takes `--data <folder>` (or the `FILMEXPIRYCHECKER_DATA` environment variable) to use another data folder, so you can try things without touching your own:

```
FilmExpiryChecker.Launcher.exe -- --data C:\Temp\fec-test --urls http://127.0.0.1:5197
```

Anything after `--` goes to the app; the launcher keeps its own files in that folder too. Without the launcher, `cd src\FilmExpiryChecker.Web` then `dotnet run` opens it in your browser at http://127.0.0.1:5187.

## For developers

```
dotnet build FilmExpiryChecker.slnx -c Release
dotnet test FilmExpiryChecker.slnx
```

| Project | What it is |
|---|---|
| `src/FilmExpiryChecker.Core` | Reading the watchlist, JustWatch, grouping, the data folder, saved runs |
| `src/FilmExpiryChecker.Web` | The Blazor Server app: results, history, settings, the background refresh |
| `src/FilmExpiryChecker.Cli` | The command line |
| `src/FilmExpiryChecker.Launcher` | The desktop launcher: starts the app, shows it in a WebView2 window, stops it on close |
| `src/FilmExpiryChecker.Launching` | The launcher's decisions, kept apart so they can be tested |
| `tests/FilmExpiryChecker.Tests` | The tests (xUnit and bUnit); none touch the network or your data |
| `tools/make-icon.cs` | Draws the icon: `dotnet run tools/make-icon.cs -- <folder>` |

The work is tracked in [issues](https://github.com/DamianCannon/FilmExpiryChecker/issues).

## A note on the data sources

Neither Letterboxd nor JustWatch offers a public API for this. The app reads your public watchlist pages and asks JustWatch's website the same questions its pages do, pausing between requests. Either site could change in ways that stop it working; when that happens the status bar says so, and your last results stay put.
