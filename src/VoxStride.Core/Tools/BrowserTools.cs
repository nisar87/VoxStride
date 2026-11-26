using System.Text.Json.Nodes;
using VoxStride.Contracts;
using VoxStride.Core.Execution;
using VoxStride.Core.Json;

namespace VoxStride.Core.Tools;

public sealed record VideoCard(string Title, string Href, double X, double Y, double Width, double Height, bool IsAd, bool IsShort);

public sealed record VideoSelection(VideoCard? Selected, IReadOnlyList<IReadOnlyList<VideoCard>> Rows, string? Ambiguity);

public sealed record PageState(string Url, string Title);

public sealed record PlaybackState(string Url, string Title, bool Playing, double CurrentTime);

/// <summary>Browser automation in a dedicated, user-visible profile. Pages are untrusted observations, never instructions.</summary>
public interface IBrowserController
{
    Task<PageState> OpenUrlAsync(Uri url, CancellationToken ct);
    Task<IReadOnlyList<VideoCard>> YouTubeSearchAsync(string query, CancellationToken ct);
    Task<PlaybackState> OpenVideoAndVerifyAsync(VideoCard card, CancellationToken ct);
    Task<(bool drafted, string readBack)> ChatGptDraftAsync(string prompt, CancellationToken ct);
}

/// <summary>Pure row grouping for "Nth video in the top row" (spec §15.6). Exposed for tests.</summary>
public static class VideoLayout
{
    public static IReadOnlyList<IReadOnlyList<VideoCard>> GroupRows(IEnumerable<VideoCard> cards, bool includeAds = false, bool includeShorts = false)
    {
        var eligible = cards.Where(c => (includeAds || !c.IsAd) && (includeShorts || !c.IsShort) && c.Width > 0 && c.Height > 0)
            .OrderBy(c => c.Y).ThenBy(c => c.X).ToList();
        var rows = new List<List<VideoCard>>();
        foreach (var c in eligible)
        {
            var row = rows.LastOrDefault();
            if (row is not null && Math.Abs(row[0].Y - c.Y) <= Math.Max(8, 0.35 * Math.Min(row[0].Height, c.Height))) row.Add(c);
            else rows.Add([c]);
        }
        return rows.Select(r => (IReadOnlyList<VideoCard>)r.OrderBy(c => c.X).ToList()).ToList();
    }

    /// <summary>Selects the index-th (1-based) video in the given row (1-based). Returns ambiguity text instead of guessing.</summary>
    public static VideoSelection Select(IEnumerable<VideoCard> cards, int row, int index)
    {
        var rows = GroupRows(cards);
        if (rows.Count == 0) return new VideoSelection(null, rows, "No videos are visible.");
        // Single-column (list) layouts: "top row" is ambiguous; interpret as overall order only when every row has one card.
        if (rows.All(r => r.Count == 1))
        {
            var flat = rows.SelectMany(r => r).ToList();
            return index <= flat.Count && row == 1
                ? new VideoSelection(flat[index - 1], rows, null)
                : new VideoSelection(null, rows, $"Results are a single column; there are {flat.Count} visible.");
        }
        if (row > rows.Count) return new VideoSelection(null, rows, $"Only {rows.Count} row(s) are visible.");
        var r = rows[row - 1];
        return index <= r.Count
            ? new VideoSelection(r[index - 1], rows, null)
            : new VideoSelection(null, rows, $"Row {row} has only {r.Count} video(s): " + string.Join("; ", r.Select(c => c.Title)));
    }
}

public sealed class BrowserOpenUrlTool(IBrowserController browser) : ToolBase
{
    public override ToolDefinition Definition { get; } = new(
        "browser_open_url", "Open an https URL in VoxStride's visible browser window.",
        Schema.Object(("url", Schema.Str("Absolute https URL", "^https://[^\\s]{3,2000}$", 2048), true)),
        RiskTier.LowReversible, true);

    public override ValueTask<ToolValidation> ValidateAsync(JsonObject args, ToolContext ctx, CancellationToken ct) =>
        ValueTask.FromResult(Uri.TryCreate(Str(args, "url"), UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps && !u.IsLoopback && u.HostNameType == UriHostNameType.Dns
            ? ToolValidation.Valid() : ToolValidation.Invalid("bad_url", "Only public https addresses can be opened."));

    public override async ValueTask<ToolResult> ExecuteAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        var page = await browser.OpenUrlAsync(new Uri(Str(args, "url")), ct);
        ctx.Items["page"] = page;
        return ToolResult.Ok(new JsonObject { ["url"] = page.Url, ["title"] = page.Title }, $"I've opened {page.Title}.", false);
    }

    public override ValueTask<ToolVerification> VerifyAsync(JsonObject args, ToolResult result, ToolContext ctx, CancellationToken ct)
    {
        var page = (PageState)ctx.Items["page"];
        var want = new Uri(Str(args, "url"));
        var ok = Uri.TryCreate(page.Url, UriKind.Absolute, out var got) && got.Host.EndsWith(want.Host.Replace("www.", "", StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase);
        return ValueTask.FromResult(new ToolVerification(ok, ok ? ToolOutcome.Success : ToolOutcome.Partial, ok ? null : $"The browser ended up at {got?.Host}."));
    }
}

public sealed class YouTubePlayTool(IBrowserController browser) : ToolBase
{
    public override ToolDefinition Definition { get; } = new(
        "browser_youtube_play",
        "Search YouTube and play the video at a visible position (row and position within that row, both 1-based; ads and Shorts excluded). If the layout makes the position ambiguous, returns the visible choices instead of guessing.",
        Schema.Object(
            ("query", Schema.Str("Search text", null, 200), true),
            ("row", Schema.Int("Visible row, 1 = top", 1, 10), true),
            ("position", Schema.Int("Position within the row, 1 = leftmost", 1, 10), true)),
        RiskTier.LowReversible, true);

    public override async ValueTask<ToolResult> ExecuteAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        var cards = await browser.YouTubeSearchAsync(Str(args, "query"), ct);
        var sel = VideoLayout.Select(cards, Int(args, "row", 1), Int(args, "position", 1));
        if (sel.Selected is null)
        {
            var choices = new JsonArray(sel.Rows.Take(2).SelectMany(r => r).Take(8).Select(c => (JsonNode)c.Title!).ToArray());
            return new ToolResult(ToolOutcome.Failure, new JsonObject { ["choices"] = choices }, "ambiguous_position", sel.Ambiguity, false, false, [], null, TimeSpan.Zero);
        }
        ctx.EnsureCanWrite(ct);
        var play = await browser.OpenVideoAndVerifyAsync(sel.Selected, ct);
        ctx.Items["play"] = play;
        ctx.Items["card"] = sel.Selected;
        return ToolResult.Ok(new JsonObject { ["title"] = play.Title, ["url"] = play.Url, ["playing"] = play.Playing }, play.Playing ? $"Now playing \"{sel.Selected.Title}\"." : $"I opened \"{sel.Selected.Title}\", but it didn't start playing — you may need to click play.", false);
    }

    public override ValueTask<ToolVerification> VerifyAsync(JsonObject args, ToolResult result, ToolContext ctx, CancellationToken ct)
    {
        var play = (PlaybackState)ctx.Items["play"];
        var card = (VideoCard)ctx.Items["card"];
        var sameVideo = VideoId(play.Url) is { } a && a == VideoId(card.Href);
        return ValueTask.FromResult(new ToolVerification(sameVideo && play.Playing, sameVideo ? (play.Playing ? ToolOutcome.Success : ToolOutcome.Partial) : ToolOutcome.Failure,
            sameVideo ? null : "The opened page is not the selected video."));
    }

    internal static string? VideoId(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return null;
        var q = System.Web.HttpUtility.ParseQueryString(u.Query);
        return q["v"];
    }
}

public sealed class ChatGptDraftTool(IBrowserController browser) : ToolBase
{
    public override ToolDefinition Definition { get; } = new(
        "browser_chatgpt_draft",
        "Open chatgpt.com in VoxStride's browser and type a prompt into the message box WITHOUT sending it. The user reviews and sends it themselves. The user handles sign-in.",
        Schema.Object(("prompt", Schema.Str("Prompt text to draft", null, 8000), true)),
        RiskTier.LowReversible, true);

    public override async ValueTask<ToolResult> ExecuteAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        ctx.EnsureCanWrite(ct);
        var (drafted, readBack) = await browser.ChatGptDraftAsync(Str(args, "prompt"), ct);
        ctx.Items["readBack"] = readBack;
        return drafted
            ? ToolResult.Ok(new JsonObject { ["submitted"] = false, ["characters"] = readBack.Length }, "I've typed your prompt into ChatGPT but haven't sent it. Have a look and press Enter when you're happy.", false)
            : ToolResult.Fail("draft_failed", "Could not find the ChatGPT message box. You may need to sign in first in the VoxStride browser window.");
    }

    public override ValueTask<ToolVerification> VerifyAsync(JsonObject args, ToolResult result, ToolContext ctx, CancellationToken ct)
    {
        var ok = EditorWriteTool.Normalize((string)ctx.Items["readBack"]).Contains(EditorWriteTool.Normalize(Str(args, "prompt")), StringComparison.Ordinal);
        return ValueTask.FromResult(new ToolVerification(ok, ok ? ToolOutcome.Success : ToolOutcome.Partial));
    }
}
