using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace RepeatDesktop;

/// <summary>One window, one WebView2, one YouTube player. The page decides full
/// vs mini, fold and pin and posts them here; this window only changes its
/// frame, size, position and always-on-top. Because the player never moves,
/// there is no progress or count to hand over between windows.
///
/// Standalone (Monkey, 2026-10-07: "not something that require the nas"): the
/// page ships in wwwroot and is served from a made-up https origin, answered
/// entirely in-process - the page, and its /api/* questions via RepeatApi. The
/// origin must be https: YouTube refuses embeds from pages with no referrer.</summary>
sealed class MainForm : Form
{
    const string Origin = "https://on-repeat.example";      // .example is reserved: never a real site
    const int MiniWidth = 320;                 // CSS px; scaled by the monitor's DPI
    readonly WebView2 web = new() { Dock = DockStyle.Fill };
    readonly Settings settings;
    readonly RepeatApi api;
    bool mini;
    // the mini window's music bars: this app's own sound, ~30 frames a second,
    // only while mini is showing (nothing is captured in the full window)
    readonly AppAudio audio = new();
    readonly System.Windows.Forms.Timer barsTimer = new() { Interval = 33 };

    public MainForm(Settings settings)
    {
        this.settings = settings;
        api = new RepeatApi(settings, Path.Combine(Settings.Dir, "repeat-counts.json"));
        Text = "On repeat";
        BackColor = Color.FromArgb(0x1E, 0x1D, 0x1B);
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);   // the exe's own repeat.ico
        StartPosition = FormStartPosition.Manual;
        Controls.Add(web);
        MinimumSize = FullMinimum();
        PlaceFull();
        Load += async (_, _) => await StartWeb();
        FormClosing += (_, _) => { audio.Stop(); Remember(); settings.Save(); };
        barsTimer.Tick += (_, _) =>
        {
            if (!mini || web.CoreWebView2 is null) return;
            var v = audio.Next().Select(x => MathF.Round(x, 2));
            web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { cmd = "levels", v, s = audio.Diag }));
        };
    }

    async Task StartWeb()
    {
        // sound may start without a click: the page opens on the last video played
        var options = new CoreWebView2EnvironmentOptions("--autoplay-policy=no-user-gesture-required");
        var env = await CoreWebView2Environment.CreateAsync(null, Settings.DataDir("WebView2"), options);
        await web.EnsureCoreWebView2Async(env);
        var core = web.CoreWebView2;
        core.WebMessageReceived += OnPageMessage;
        // "Open on YouTube", the key guide's links: the default browser, not a second app window
        // Security review 2026-10-08: only links you clicked open, never a pop-up a
        // frame (YouTube's player, an ad in it) opens on its own
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            if (e.IsUserInitiated) OpenExternal(e.Uri);
        };
        // the window only ever shows the app's own page: any other top-level
        // navigation is stopped (a clicked link goes to the default browser), so
        // no other site can end up holding this window's page-to-window channel
        core.NavigationStarting += (_, e) =>
        {
            if (e.Uri.StartsWith(Origin + "/", StringComparison.Ordinal)) return;
            e.Cancel = true;
            if (e.IsUserInitiated) OpenExternal(e.Uri);
        };
        core.DocumentTitleChanged += (_, _) => Text = core.DocumentTitle;
        core.AddWebResourceRequestedFilter(Origin + "/*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += async (_, e) =>
        {
            using var deferral = e.GetDeferral();
            e.Response = await Serve(env, e.Request);
        };
        core.Navigate(Origin + "/repeat?app=1");
    }

    /// <summary>Everything under Origin: the page itself, or the API.</summary>
    async Task<CoreWebView2WebResourceResponse> Serve(CoreWebView2Environment env, CoreWebView2WebResourceRequest req)
    {
        var uri = new Uri(req.Uri);
        string type, text;
        int status;
        if (uri.AbsolutePath is "/" or "/repeat")
        {
            (status, type) = (200, "text/html; charset=utf-8");
            text = (await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "wwwroot", "repeat.html")))
                .Replace("{{APP_TOKEN}}", token);
        }
        else if (uri.AbsolutePath.StartsWith("/api/") && !FromOwnPage(req))
            (status, type, text) = (403, "application/json", "{\"error\":\"only the app's own page may call this\"}");
        else if (uri.AbsolutePath.StartsWith("/api/"))
        {
            var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
            var q = query.AllKeys.Where(k => k is not null).ToDictionary(k => k!, k => query[k] ?? "");
            var body = "";
            if (req.Content is not null)
                using (var r = new StreamReader(req.Content)) body = await r.ReadToEndAsync();
            RepeatApi.Reply reply;
            try { reply = await api.Handle(req.Method, uri.AbsolutePath, q, body); }
            catch (Exception ex)
            {
                // e.g. a damaged counts file: say so, and never overwrite it
                reply = new(500, JsonSerializer.Serialize(new { error = $"{ex.GetType().Name}: {ex.Message}" }));
            }
            (status, type, text) = (reply.Status, "application/json", reply.Json);
        }
        else
            (status, type, text) = (404, "text/plain", "not found");
        var bytes = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(text));
        return env.CreateWebResourceResponse(bytes, status, status == 200 ? "OK" : "Error",
            $"Content-Type: {type}\r\nCache-Control: no-store");
    }

    /// <summary>The API answers the app's page only. Every request made inside
    /// this window reaches Serve - including ones from YouTube's frame, which
    /// could otherwise delete counts, edit playlists or swap the API key (found
    /// in the 2026-10-08 security review). A secret made fresh at each start is
    /// written into the page as it is served; the page sends it with every API
    /// call. Another frame can't read our page (cross-origin), so it can't know
    /// the secret, and a custom header from it would need CORS we never grant.
    /// (Sec-Fetch-Site was the first idea: WebView2 doesn't show it here.)</summary>
    readonly string token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
    bool FromOwnPage(CoreWebView2WebResourceRequest req) =>
        req.Headers.Contains("X-App-Token") &&
        System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.ASCII.GetBytes(req.Headers.GetHeader("X-App-Token")), System.Text.Encoding.ASCII.GetBytes(token));

    static void OpenExternal(string uri)
    {
        if (Uri.TryCreate(uri, UriKind.Absolute, out var u) && (u.Scheme == "http" || u.Scheme == "https"))
            Process.Start(new ProcessStartInfo(u.AbsoluteUri) { UseShellExecute = true });
    }

    void OnPageMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        // only the app's own page may move, resize or pin the window
        if (!e.Source.StartsWith(Origin + "/", StringComparison.Ordinal)) return;
        JsonElement m;
        try { m = JsonDocument.Parse(e.WebMessageAsJson).RootElement; }
        catch (JsonException) { return; }
        if (!m.TryGetProperty("cmd", out var cmd)) return;
        switch (cmd.GetString())
        {
            case "mode":
                SetMini(m.GetProperty("mini").GetBoolean());
                break;
            case "height" when mini:
                // device pixels, measured by the page itself: they include the
                // screen's DPI and Windows' text size, which WebView2 applies on
                // top (DeviceDpi alone undersized the window by 17% on a PC with
                // Text size = 117%, cutting off the counters)
                var h = Math.Clamp(m.GetProperty("h").GetInt32(), 40, 2400);
                var w = m.TryGetProperty("w", out var wv) ? Math.Clamp(wv.GetInt32(), 200, 1200) : Px(MiniWidth);
                ClientSize = new Size(w, h);
                KeepOnScreen();
                break;
            case "pin":
                settings.Pinned = m.GetProperty("on").GetBoolean();
                TopMost = mini && settings.Pinned;
                break;
            case "drag" when mini:
                // the mini window has no OS title bar: hand the mouse to Windows
                // as if it were on one, so it moves like any window
                ReleaseCapture();
                SendMessage(Handle, WM_NCLBUTTONDOWN, HTCAPTION, IntPtr.Zero);
                break;
        }
    }

    void SetMini(bool on)
    {
        if (on == mini) return;
        Remember();
        mini = on;
        if (on)
        {
            MinimumSize = Size.Empty;               // mini sizes itself to the card, as small as 320 x ~150
            FormBorderStyle = FormBorderStyle.None;
            RoundCorners();
            TopMost = settings.Pinned;
            var wa = Screen.FromControl(this).WorkingArea;
            var p = settings.Mini is { Length: 2 } s
                ? new Point(s[0], s[1])
                : new Point(wa.Right - Px(MiniWidth) - 24, wa.Bottom - Px(360) - 24);
            Location = p;
            ClientSize = new Size(Px(MiniWidth), ClientSize.Height);   // height follows the page
            KeepOnScreen();
            // the root of WebView2's processes: its audio service plays the video's sound
            if (web.CoreWebView2 is { } c) audio.Start((int)c.BrowserProcessId);
            barsTimer.Start();
        }
        else
        {
            barsTimer.Stop();
            audio.Stop();
            TopMost = false;
            FormBorderStyle = FormBorderStyle.Sizable;
            MinimumSize = FullMinimum();
            PlaceFull();
            Activate();
        }
    }

    void PlaceFull()
    {
        if (settings.Full is { Length: 4 } f && OnSomeScreen(new Rectangle(f[0], f[1], f[2], f[3])))
            Land(new Rectangle(f[0], f[1], f[2], f[3]));
        else
        {
            var wa = Screen.PrimaryScreen!.WorkingArea;
            var size = new Size(Math.Min(Px(1100), wa.Width), Math.Min(Px(860), wa.Height));
            Land(new Rectangle(wa.Left + (wa.Width - size.Width) / 2, wa.Top + (wa.Height - size.Height) / 2,
                size.Width, size.Height));
        }
    }

    /// <summary>Puts the window exactly on r, even when r is on a screen with
    /// another scale. Moving there makes Windows send WM_DPICHANGED with a size
    /// scaled from the window's *previous* size - the mini one - and WinForms
    /// applies it, so the full window came back mini-sized (Monkey, 2026-10-08:
    /// mini on one screen, dragged to the other, Esc). The wanted rect is applied
    /// again once that adjustment has run.</summary>
    Rectangle? landing;
    void Land(Rectangle r)
    {
        Bounds = r;
        if (!IsHandleCreated) return;          // first placement, before the window exists: nothing to undo yet
        landing = r;
        BeginInvoke(() => { if (landing is { } w && Bounds != w) Bounds = w; landing = null; });
    }

    /// <summary>The full window can't be dragged smaller than this (Monkey,
    /// 2026-10-08: shrunk to a sliver, it showed nothing but the "more" chip).
    /// 640 x 480 in page units: the player, its title row and the search bar
    /// still fit, and the layout's phone breakpoint (780) takes over below.</summary>
    Size FullMinimum() => new(Px(640), Px(480));

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        if (!mini) MinimumSize = FullMinimum();
        if (landing is { } w) Bounds = w;
    }

    void Remember()
    {
        if (WindowState != FormWindowState.Normal) return;
        if (mini) settings.Mini = [Left, Top];
        else settings.Full = [Left, Top, Width, Height];
    }

    void KeepOnScreen()
    {
        var wa = Screen.FromRectangle(Bounds).WorkingArea;
        Location = new Point(Math.Clamp(Left, wa.Left, Math.Max(wa.Left, wa.Right - Width)),
                             Math.Clamp(Top, wa.Top, Math.Max(wa.Top, wa.Bottom - Height)));
    }

    static bool OnSomeScreen(Rectangle r) => Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(r));

    int Px(int cssPx) => (int)Math.Round(cssPx * DeviceDpi / 96.0);

    protected override void OnMove(EventArgs e)
    {
        base.OnMove(e);
        if (mini && WindowState == FormWindowState.Normal) settings.Mini = [Left, Top];
    }

    /// <summary>A frameless window gets square corners unless it asks Windows 11
    /// for the standard rounded ones. Earlier Windows ignores the call.</summary>
    void RoundCorners()
    {
        var round = DWMWCP_ROUND;
        _ = DwmSetWindowAttribute(Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        RoundCorners();
    }

    const int DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWCP_ROUND = 2;
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    const int WM_NCLBUTTONDOWN = 0xA1;
    static readonly IntPtr HTCAPTION = 2;
    [DllImport("user32.dll")] static extern bool ReleaseCapture();
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
}
