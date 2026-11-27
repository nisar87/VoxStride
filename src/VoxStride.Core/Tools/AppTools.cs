using System.Text.Json.Nodes;
using VoxStride.Contracts;
using VoxStride.Core.Execution;
using VoxStride.Core.Json;

namespace VoxStride.Core.Tools;

/// <summary>An installed application resolved from trusted registrations/shortcuts — never from a model-supplied string.</summary>
public sealed record AppEntry(string Id, string DisplayName, string LaunchTarget, IReadOnlyList<string> Aliases, string Source);

public sealed record WindowIdentity(int ProcessId, DateTime ProcessStartUtc, long Hwnd, string Title, string ProcessName);

public interface IAppCatalog
{
    bool IsReady { get; }
    IReadOnlyList<AppEntry> Snapshot();
    /// <summary>Resolves a spoken/typed name to exactly one app, or returns candidates when ambiguous.</summary>
    AppResolution Resolve(string name);
}

public sealed record AppResolution(AppEntry? App, IReadOnlyList<AppEntry> Candidates);

public interface IAppController
{
    Task<WindowIdentity?> LaunchAsync(AppEntry app, TimeSpan waitForWindow, CancellationToken ct);
    Task<bool> IsAliveAsync(WindowIdentity window, CancellationToken ct);
}

public sealed class LaunchAppTool(IAppCatalog catalog, IAppController controller, VoxStride.Core.Policy.UserPermissions? permissions = null) : ToolBase
{
    public override ToolDefinition Definition { get; } = new(
        "apps_launch",
        "Open (or focus) an installed application by name. Only apps found in the local installed-app catalog can be opened.",
        Schema.Object(("name", Schema.Str("App name as the user said it, e.g. 'Notepad++'", null, 80), true)),
        RiskTier.LowReversible, true);

    public override ValueTask<ToolValidation> ValidateAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        var r = catalog.Resolve(Str(args, "name"));
        if (r.App is null)
            return ValueTask.FromResult(r.Candidates.Count > 0
                ? ToolValidation.Invalid("ambiguous_app", "Which one: " + string.Join(", ", r.Candidates.Take(5).Select(c => c.DisplayName)) + "?")
                : ToolValidation.Invalid("app_not_found", catalog.IsReady ? $"I couldn't find an app called {Str(args, "name")} on this PC." : "The app list is still loading. Try again in a moment."));
        if (!(permissions ?? VoxStride.Core.Policy.UserPermissions.Default).AppAllowed(r.App.DisplayName, r.App.LaunchTarget, out var why))
            return ValueTask.FromResult(ToolValidation.Invalid("app_blocked_by_user", why));
        ctx.Items["app"] = r.App;
        return ValueTask.FromResult(ToolValidation.Valid());
    }

    public override async ValueTask<ToolResult> ExecuteAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        var app = (AppEntry)ctx.Items["app"];
        ctx.EnsureCanWrite(ct);
        var w = await controller.LaunchAsync(app, TimeSpan.FromSeconds(10), ct);
        if (w is null) return new ToolResult(ToolOutcome.Unknown, null, "no_window", $"{app.DisplayName} was started but no window appeared yet.", false, false, [], null, TimeSpan.Zero);
        ctx.Items["window"] = w;
        return ToolResult.Ok(new JsonObject { ["app"] = app.DisplayName, ["processId"] = w.ProcessId, ["windowTitle"] = w.Title }, $"{app.DisplayName} is open.", false);
    }

    public override async ValueTask<ToolVerification> VerifyAsync(JsonObject args, ToolResult result, ToolContext ctx, CancellationToken ct)
    {
        if (!ctx.Items.TryGetValue("window", out var w)) return new ToolVerification(false, ToolOutcome.Unknown);
        var alive = await controller.IsAliveAsync((WindowIdentity)w, ct);
        return new ToolVerification(alive, alive ? ToolOutcome.Success : ToolOutcome.Unknown, alive ? null : "The window closed immediately.");
    }
}

public sealed class ListAppsTool(IAppCatalog catalog) : ToolBase
{
    public override ToolDefinition Definition { get; } = new(
        "apps_list", "Search the installed-app catalog by name fragment.",
        Schema.Object(("query", Schema.Str("Name fragment", null, 80), true)), RiskTier.Read, false);

    public override ValueTask<ToolResult> ExecuteAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        var q = Str(args, "query");
        var hits = catalog.Snapshot().Where(a => a.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase) || a.Aliases.Any(x => x.Contains(q, StringComparison.OrdinalIgnoreCase))).Take(15)
            .Select(a => (JsonNode)a.DisplayName!).ToArray();
        return ValueTask.FromResult(ToolResult.Ok(new JsonObject { ["apps"] = new JsonArray(hits) }, $"{hits.Length} match(es).", true));
    }
}

// ---------------- Editor ----------------

public enum EditorKind { Notepad, NotepadPlusPlus }

public sealed record EditorInsertResult(bool Inserted, bool ReadBackVerified, string? ReadBack, WindowIdentity? Window, string? Error);

/// <summary>Writes text into a NEW document of a verified editor window and reads it back where supported.</summary>
public interface IEditorAdapter
{
    Task<EditorInsertResult> WriteNewDocumentAsync(EditorKind editor, string text, CancellationToken ct);
}

public sealed class EditorWriteTool(IEditorAdapter editor, IAppCatalog catalog) : ToolBase
{
    public const int MaxChars = 20000;

    public override ToolDefinition Definition { get; } = new(
        "editor_write_new_document",
        "Open a NEW, empty document in Notepad or Notepad++ and type the given text into it, then read it back to verify. Never modifies existing documents. Does not save.",
        Schema.Object(
            ("editor", Schema.Str("Target editor", null, 16, "notepad", "notepad++"), true),
            ("text", Schema.Str("Exact final text to insert", null, MaxChars), true)),
        RiskTier.LowReversible, true);

    public override ValueTask<ToolValidation> ValidateAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        if (Str(args, "editor") == "notepad++" && catalog.Resolve("notepad++").App is null)
            return ValueTask.FromResult(ToolValidation.Invalid("app_not_found", "Notepad++ is not installed. I can use Notepad instead."));
        if (string.IsNullOrWhiteSpace(Str(args, "text"))) return ValueTask.FromResult(ToolValidation.Invalid("empty_text", "No text to write."));
        return ValueTask.FromResult(ToolValidation.Valid());
    }

    public override async ValueTask<ToolResult> ExecuteAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        var kind = Str(args, "editor") == "notepad++" ? EditorKind.NotepadPlusPlus : EditorKind.Notepad;
        var text = Str(args, "text");
        ctx.EnsureCanWrite(ct);
        var r = await editor.WriteNewDocumentAsync(kind, text, ct);
        if (!r.Inserted) return ToolResult.Fail("insert_failed", r.Error ?? "Could not insert text.");
        ctx.Items["result"] = r;
        var state = new JsonObject { ["editor"] = kind.ToString(), ["characters"] = text.Length, ["readBackVerified"] = r.ReadBackVerified };
        return new ToolResult(ToolOutcome.Success, state, null,
            r.ReadBackVerified ? $"Done — it's written in a new {(kind == EditorKind.Notepad ? "Notepad" : "Notepad++")} document. I haven't saved it, so you can edit it first." : "I typed the text, but couldn't double-check it, so please have a quick look.",
            r.ReadBackVerified, false, [], null, TimeSpan.Zero);
    }

    public override ValueTask<ToolVerification> VerifyAsync(JsonObject args, ToolResult result, ToolContext ctx, CancellationToken ct)
    {
        var r = (EditorInsertResult)ctx.Items["result"];
        var expected = Normalize(Str(args, "text"));
        var ok = r.ReadBack is not null && Normalize(r.ReadBack).Contains(expected, StringComparison.Ordinal);
        return ValueTask.FromResult(new ToolVerification(ok, ok ? ToolOutcome.Success : ToolOutcome.Partial, ok ? null : "Inserted text could not be verified by read-back."));
    }

    public static string Normalize(string s) => s.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').TrimEnd();
}
