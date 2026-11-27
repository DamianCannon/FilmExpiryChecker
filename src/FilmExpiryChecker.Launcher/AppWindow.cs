using FilmExpiryChecker.Launching;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace FilmExpiryChecker.Launcher;

/// <summary>
/// The app in a window of its own: WebView2, Edge's engine, so it's one window under one taskbar icon
/// (a Chrome or Edge --app window shows as a second icon beside the pin). Links away from the app open
/// in the usual browser. Its size and place are remembered.
/// </summary>
internal sealed class AppWindow : Form
{
    private readonly WebView2 _view = new() { Dock = DockStyle.Fill };
    private readonly Uri _address;
    private readonly string _folder;

    public AppWindow(Uri address, Icon icon, string folder)
    {
        (_address, _folder) = (address, folder);
        Text = Launch.AppName;
        Icon = icon;
        Controls.Add(_view);
        Placement.Restore(this, Path.Combine(folder, "window.json"));
        Load += async (_, _) => await OpenAsync();
        FormClosing += (_, _) => Placement.Save(this, Path.Combine(folder, "window.json"));
    }

    /// <summary>Brought to the front and restored if minimised: Activate alone leaves it minimised.</summary>
    public void BringForward()
    {
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
        BringToFront();
    }

    private async Task OpenAsync()
    {
        // Its own profile in LocalAppData: beside the exe may be read-only, and a rebuild would wipe it.
        var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: Path.Combine(_folder, "WebView2"));
        await _view.EnsureCoreWebView2Async(environment);
        var web = _view.CoreWebView2;
        web.DocumentTitleChanged += (_, _) => Text = string.IsNullOrWhiteSpace(web.DocumentTitle) ? Launch.AppName : web.DocumentTitle;
        // Away from the app: the usual browser, not this window.
        web.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            OpenOutside(e.Uri);
        };
        web.NavigationStarting += (_, e) =>
        {
            if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var to) && to.Authority != _address.Authority && to.Scheme is "http" or "https")
            {
                e.Cancel = true;
                OpenOutside(e.Uri);
            }
        };
        web.Navigate(_address.ToString());
    }

    private static void OpenOutside(string uri)
    {
        if (Uri.TryCreate(uri, UriKind.Absolute, out var to) && to.Scheme is "http" or "https")
            Process.Start(new ProcessStartInfo(to.ToString()) { UseShellExecute = true })?.Dispose();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _view.Dispose();
        base.Dispose(disposing);
    }

    // Where the window was, and how big, kept between runs; only on a screen that's still there.
    private static class Placement
    {
        private sealed record Kept(int X, int Y, int Width, int Height, bool Maximised);

        public static void Restore(Form form, string file)
        {
            form.Size = new Size(1400, 900);
            form.StartPosition = FormStartPosition.CenterScreen;
            try
            {
                if (File.Exists(file) && JsonSerializer.Deserialize<Kept>(File.ReadAllText(file)) is { } kept)
                {
                    var bounds = new Rectangle(kept.X, kept.Y, kept.Width, kept.Height);
                    if (Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(bounds)))
                    {
                        form.StartPosition = FormStartPosition.Manual;
                        form.Bounds = bounds;
                    }
                    if (kept.Maximised) form.WindowState = FormWindowState.Maximized;
                }
            }
            catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
            {
                // Opens as it does the first time, then.
            }
        }

        public static void Save(Form form, string file)
        {
            var bounds = form.WindowState == FormWindowState.Normal ? form.Bounds : form.RestoreBounds;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllText(file, JsonSerializer.Serialize(new Kept(bounds.X, bounds.Y, bounds.Width, bounds.Height, form.WindowState == FormWindowState.Maximized)));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Not kept this time.
            }
        }
    }
}
