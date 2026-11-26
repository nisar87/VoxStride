using System.Text.Json;
using Microsoft.Playwright;
using VoxStride.Core.Tools;

namespace VoxStride.Browser;

/// <summary>
/// Visible browser automation in a dedicated persistent profile (never the user's default profile), driving the
/// installed Microsoft Edge (or Chrome). No cookies, storage or credentials are read. Page content is treated
/// as untrusted observation only. The user signs in manually when a site requires it.
/// </summary>
public sealed class PlaywrightBrowserController : IBrowserController, IAsyncDisposable
{
    private readonly string _profileDir;
    private readonly string _channel;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IPlaywright? _pw;
    private IBrowserContext? _ctx;

    public PlaywrightBrowserController(string? profileDir = null, string channel = "msedge")
    {
        _profileDir = profileDir ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VoxStride", "BrowserProfile");
        _channel = channel;
    }

    private async Task<IPage> PageAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_ctx is null)
            {
                Directory.CreateDirectory(_profileDir);
                _pw = await Playwright.CreateAsync().ConfigureAwait(false);
                _ctx = await _pw.Chromium.LaunchPersistentContextAsync(_profileDir, new BrowserTypeLaunchPersistentContextOptions
                {
                    Channel = _channel,
                    Headless = false,
                    ViewportSize = ViewportSize.NoViewport,
                    Args = ["--start-maximized", "--autoplay-policy=no-user-gesture-required", "--no-first-run", "--no-default-browser-check"],
                    AcceptDownloads = false,
                }).ConfigureAwait(false);
                _ctx.Close += (_, _) => { _ctx = null; };
            }
            var page = _ctx.Pages.LastOrDefault(p => !p.IsClosed) ?? await _ctx.NewPageAsync().ConfigureAwait(false);
            await page.BringToFrontAsync().ConfigureAwait(false);
            return page;
        }
        catch (PlaywrightException ex)
        {
            _ctx = null;
            throw new InvalidOperationException("Could not start the VoxStride browser window (Microsoft Edge is required): " + ex.Message, ex);
        }
        finally { _gate.Release(); }
    }

    public async Task<PageState> OpenUrlAsync(Uri url, CancellationToken ct)
    {
        var page = await PageAsync(ct).ConfigureAwait(false);
        await page.GotoAsync(url.ToString(), new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 30000 }).ConfigureAwait(false);
        return new PageState(page.Url, await page.TitleAsync().ConfigureAwait(false));
    }

    // Extracts only geometry, title, href and ad/shorts markers from rendered video cards.
    private const string CollectCardsJs = """
        () => {
          const out = [];
          const nodes = document.querySelectorAll('ytd-video-renderer, ytd-rich-item-renderer, ytd-grid-video-renderer, ytd-ad-slot-renderer, ytd-reel-item-renderer, ytm-shorts-lockup-view-model');
          for (const n of nodes) {
            const r = n.getBoundingClientRect();
            if (r.width < 50 || r.height < 50) continue;
            const style = getComputedStyle(n);
            if (style.display === 'none' || style.visibility === 'hidden') continue;
            const a = n.querySelector('a#video-title, a#video-title-link, a.yt-lockup-metadata-view-model__title, a[href*="/watch?v="], a[href*="/shorts/"]');
            const href = a ? a.href : '';
            const title = (a && (a.getAttribute('title') || a.textContent) || '').trim();
            const isAd = n.tagName.toLowerCase() === 'ytd-ad-slot-renderer' || !!n.querySelector('ytd-ad-slot-renderer, [class*="ad-badge"], ytd-display-ad-renderer, ytd-in-feed-ad-layout-renderer') || /[?&]adurl=|googleadservices/.test(href);
            const isShort = href.includes('/shorts/') || n.tagName.toLowerCase().includes('reel') || n.tagName.toLowerCase().includes('shorts');
            if (!href && !isAd) continue;
            out.push({ title, href, x: r.left + window.scrollX, y: r.top + window.scrollY, w: r.width, h: r.height, isAd, isShort, top: r.top });
          }
          return out.filter(c => c.top < window.innerHeight * 1.5);
        }
        """;

    public async Task<IReadOnlyList<VideoCard>> YouTubeSearchAsync(string query, CancellationToken ct)
    {
        var page = await PageAsync(ct).ConfigureAwait(false);
        var url = "https://www.youtube.com/results?search_query=" + Uri.EscapeDataString(query);
        await page.GotoAsync(url, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 30000 }).ConfigureAwait(false);
        await DismissConsentIfPresent(page).ConfigureAwait(false);
        await page.WaitForSelectorAsync("ytd-video-renderer, ytd-rich-item-renderer", new PageWaitForSelectorOptions { Timeout = 15000, State = WaitForSelectorState.Visible }).ConfigureAwait(false);
        await page.WaitForTimeoutAsync(400).ConfigureAwait(false); // let lazy thumbnails/layout settle
        var json = await page.EvaluateAsync<JsonElement>(CollectCardsJs).ConfigureAwait(false);
        return Parse(json);
    }

    internal static IReadOnlyList<VideoCard> Parse(JsonElement json)
    {
        var list = new List<VideoCard>();
        foreach (var c in json.EnumerateArray())
        {
            var href = c.GetProperty("href").GetString() ?? "";
            if (href.Length > 0 && !Uri.TryCreate(href, UriKind.Absolute, out _)) continue;
            if (href.Length > 0 && !new Uri(href).Host.EndsWith("youtube.com", StringComparison.OrdinalIgnoreCase)) continue;
            list.Add(new VideoCard(c.GetProperty("title").GetString() ?? "", href, c.GetProperty("x").GetDouble(), c.GetProperty("y").GetDouble(),
                c.GetProperty("w").GetDouble(), c.GetProperty("h").GetDouble(), c.GetProperty("isAd").GetBoolean(), c.GetProperty("isShort").GetBoolean()));
        }
        return list;
    }

    public async Task<PlaybackState> OpenVideoAndVerifyAsync(VideoCard card, CancellationToken ct)
    {
        if (!Uri.TryCreate(card.Href, UriKind.Absolute, out var target) || !target.Host.EndsWith("youtube.com", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Selected card does not link to YouTube.");
        var page = await PageAsync(ct).ConfigureAwait(false);
        await page.GotoAsync(target.ToString(), new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 30000 }).ConfigureAwait(false);
        await page.WaitForSelectorAsync("video", new PageWaitForSelectorOptions { Timeout = 15000, State = WaitForSelectorState.Attached }).ConfigureAwait(false);
        var playing = false;
        double t = 0;
        for (var i = 0; i < 12 && !playing; i++)
        {
            await page.WaitForTimeoutAsync(500).ConfigureAwait(false);
            var st = await page.EvaluateAsync<JsonElement>("() => { const v = document.querySelector('video.html5-main-video') || document.querySelector('video'); if (!v) return {p:false,t:0,paused:true}; return {p: !v.paused && !v.ended && v.readyState > 2, t: v.currentTime, paused: v.paused}; }").ConfigureAwait(false);
            playing = st.GetProperty("p").GetBoolean() && st.GetProperty("t").GetDouble() > 0.2;
            t = st.GetProperty("t").GetDouble();
            if (i == 4 && st.GetProperty("paused").GetBoolean())
                await page.EvaluateAsync("() => { const v = document.querySelector('video.html5-main-video') || document.querySelector('video'); if (v && v.paused) v.play().catch(() => {}); }").ConfigureAwait(false);
        }
        var title = (await page.TitleAsync().ConfigureAwait(false)).Replace(" - YouTube", "", StringComparison.Ordinal);
        return new PlaybackState(page.Url, title, playing, t);
    }

    public async Task<(bool drafted, string readBack)> ChatGptDraftAsync(string prompt, CancellationToken ct)
    {
        var page = await PageAsync(ct).ConfigureAwait(false);
        await page.GotoAsync("https://chatgpt.com/", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 30000 }).ConfigureAwait(false);
        var input = page.Locator("#prompt-textarea, div[contenteditable='true'][id='prompt-textarea'], textarea[name='prompt-textarea']").First;
        try { await input.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 20000 }).ConfigureAwait(false); }
        catch (TimeoutException) { return (false, ""); }
        if (!new Uri(page.Url).Host.EndsWith("chatgpt.com", StringComparison.OrdinalIgnoreCase)) return (false, "");
        await input.ClickAsync().ConfigureAwait(false);
        await input.FillAsync(prompt).ConfigureAwait(false); // fills the box; never presses Enter or clicks Send
        var readBack = await input.InnerTextAsync().ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(readBack)) readBack = await input.InputValueAsync().ConfigureAwait(false);
        return (true, readBack);
    }

    private static async Task DismissConsentIfPresent(IPage page)
    {
        // Decline non-essential cookies when YouTube's consent dialog blocks results (privacy-preserving default).
        var reject = page.Locator("button[aria-label*='Reject'], tp-yt-paper-button[aria-label*='Reject']").First;
        try { if (await reject.IsVisibleAsync().ConfigureAwait(false)) await reject.ClickAsync(new LocatorClickOptions { Timeout = 3000 }).ConfigureAwait(false); }
        catch (Exception) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (_ctx is not null) { try { await _ctx.CloseAsync().ConfigureAwait(false); } catch (Exception) { } }
        _pw?.Dispose();
        _gate.Dispose();
    }
}
