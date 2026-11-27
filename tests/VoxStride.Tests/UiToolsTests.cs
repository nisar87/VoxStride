using System.Text.Json.Nodes;
using VoxStride.Contracts;
using VoxStride.Core.Policy;
using VoxStride.Core.Tools;
using VoxStride.Providers;
using Xunit;

namespace VoxStride.Tests;

public sealed class FakeUi : IUiAutomation
{
    public List<UiWindow> Windows { get; } = [];
    public List<UiElement> Elements { get; } = [];
    public List<string> Calls { get; } = [];
    public string? ReadBack { get; set; }
    public static UiWindow Win(string proc, string title, long hwnd = 0x10) => new($"w{hwnd:x}-{100 + hwnd}", hwnd, (int)(100 + hwnd), DateTime.UtcNow, title, proc, true, 0, 0, 800, 600);

    public Task<IReadOnlyList<UiWindow>> ListWindowsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<UiWindow>>(Windows);
    public Task<UiWindow?> GetWindowAsync(string id, CancellationToken ct) => Task.FromResult(Windows.FirstOrDefault(w => w.Id == id));
    public Task<UiActionResult> FocusAsync(string id, CancellationToken ct) { Calls.Add("focus " + id); return Task.FromResult(new UiActionResult(true, Verified: true)); }
    public Task<UiSnapshot?> ObserveAsync(string id, string? filter, int max, CancellationToken ct) =>
        Task.FromResult<UiSnapshot?>(new UiSnapshot(id, Windows.First(w => w.Id == id).Title, Elements, false, DateTimeOffset.UtcNow));
    public Task<UiActionResult> InvokeAsync(string w, string e, string a, CancellationToken ct) { Calls.Add($"{a} {e}"); return Task.FromResult(new UiActionResult(true, Verified: true)); }
    public Task<UiActionResult> SetValueAsync(string w, string e, string t, CancellationToken ct) { Calls.Add($"set {e}"); return Task.FromResult(new UiActionResult(true, NewValue: ReadBack ?? t)); }
    public Task<UiActionResult> TypeTextAsync(string w, string e, string t, CancellationToken ct) { Calls.Add($"type {e}"); return Task.FromResult(new UiActionResult(true, Verified: true)); }
    public Task<UiCapture?> CaptureAsync(string id, CancellationToken ct) { Calls.Add("capture " + id); return Task.FromResult<UiCapture?>(new UiCapture("cap-0123456789ab", id, [1, 2, 3], 640, 480, DateTimeOffset.UtcNow)); }
    public Task<UiActionResult> ClickAsync(string w, UiCapture c, int x, int y, CancellationToken ct) { Calls.Add($"click {x},{y}"); return Task.FromResult(new UiActionResult(true)); }
}

public sealed class UiToolsTests : IDisposable
{
    private readonly Harness _h = new();
    private readonly FakeUi _ui = new();
    private ScreenCapturePolicy _capture = ScreenCapturePolicy.AskEachTime;
    private readonly List<string> _blocked = [];
    private readonly UiWindow _app = FakeUi.Win("notepad", "Untitled - Notepad", 0x10);

    public UiToolsTests()
    {
        _ui.Windows.AddRange([_app, FakeUi.Win("consent", "User Account Control", 0x20), FakeUi.Win("VoxStride", "VoxStride", 0x30), FakeUi.Win("Steam", "Steam", 0x40)]);
        _ui.Elements.AddRange([
            new UiElement("e1", "edit", "Text editor", "15", "hello", true, false, ["setvalue", "focus"], 0, 0, 10, 10, 1),
            new UiElement("e2", "button", "Save", null, null, true, false, ["invoke"], 0, 0, 10, 10, 1),
            new UiElement("e3", "button", "Delete all", null, null, true, false, ["invoke"], 0, 0, 10, 10, 1),
            new UiElement("e4", "edit", "Password", null, null, true, true, ["setvalue"], 0, 0, 10, 10, 1),
            new UiElement("e5", "button", "Greyed", null, null, false, false, ["invoke"], 0, 0, 10, 10, 1)]);
        var session = new UiSession(_ui, new UserPermissions(blockedApps: () => _blocked, screenCapture: () => _capture), _h.Time);
        _h.Registry.Add(new ListWindowsTool(session)).Add(new FocusWindowTool(session)).Add(new ObserveUiTool(session)).Add(new InvokeUiTool(session))
            .Add(new SetValueUiTool(session)).Add(new TypeTextUiTool(session)).Add(new CaptureWindowTool(session)).Add(new ClickUiTool(session));
    }

    public void Dispose() => _h.Dispose();

    private Task<ToolResult> Call(string tool, JsonObject args) => _h.Call(_h.Mint(), tool, args);
    private JsonObject W(JsonObject? extra = null) { var o = new JsonObject { ["windowId"] = _app.Id }; foreach (var (k, v) in extra ?? []) o[k] = v?.DeepClone(); return o; }

    [Fact]
    public async Task ProtectedAndBlockedWindowsAreHidden()
    {
        _blocked.Add("Steam");
        var r = await Call("apps_list_windows", []);
        var json = r.ObservedState!.ToJsonString();
        Assert.Contains("Notepad", json, StringComparison.Ordinal);
        Assert.DoesNotContain("User Account Control", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"VoxStride\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Steam", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SecurityPromptsCanNeverBeControlled()
    {
        var uac = _ui.Windows[1];
        var r = await _h.Call(_h.Mint(), "ui_observe", new JsonObject { ["windowId"] = uac.Id });
        Assert.Equal("window_not_allowed", r.ErrorCode);
    }

    [Fact]
    public async Task ActionsNeedAFreshObservation()
    {
        var before = await Call("ui_invoke", W(new JsonObject { ["elementId"] = "e2" }));
        Assert.Equal("observe_first", before.ErrorCode);
        await Call("ui_observe", W());
        var ok = await Call("ui_invoke", W(new JsonObject { ["elementId"] = "e2" }));
        Assert.Equal(ToolOutcome.Success, ok.Outcome);
        _h.Time.Advance(TimeSpan.FromMinutes(3));
        var stale = await Call("ui_invoke", W(new JsonObject { ["elementId"] = "e2" }));
        Assert.Equal("observe_first", stale.ErrorCode);
        Assert.Empty(_h.Approvals.Requests); // "Save" is an ordinary button
    }

    [Fact]
    public async Task IrreversibleButtonsNeedApprovalAndDeclineMeansNothingHappens()
    {
        await Call("ui_observe", W());
        _h.Approvals.Approve = false;
        var r = await Call("ui_invoke", W(new JsonObject { ["elementId"] = "e3" }));
        Assert.Equal("approval_declined", r.ErrorCode);
        Assert.Single(_h.Approvals.Requests);
        Assert.DoesNotContain("invoke e3", _ui.Calls);
    }

    [Fact]
    public async Task DisabledControlsAndPasswordFieldsAreRefused()
    {
        await Call("ui_observe", W());
        Assert.Equal("element_disabled", (await Call("ui_invoke", W(new JsonObject { ["elementId"] = "e5" }))).ErrorCode);
        Assert.Equal("password_field", (await Call("ui_set_value", W(new JsonObject { ["elementId"] = "e4", ["text"] = "x" }))).ErrorCode);
        Assert.Equal("password_field", (await Call("ui_type_text", W(new JsonObject { ["elementId"] = "e4", ["text"] = "x" }))).ErrorCode);
    }

    [Fact]
    public async Task SetValueIsVerifiedByReadBack()
    {
        await Call("ui_observe", W());
        var ok = await Call("ui_set_value", W(new JsonObject { ["elementId"] = "e1", ["text"] = "Dear team" }));
        Assert.True(ok.Verified);
        _ui.ReadBack = "Dear tea";
        var partial = await Call("ui_set_value", W(new JsonObject { ["elementId"] = "e1", ["text"] = "Dear team" }));
        Assert.Equal(ToolOutcome.Partial, partial.Outcome);
        Assert.False(partial.Verified);
    }

    [Fact]
    public async Task ScreenshotsAskEachTimeOrAreRefused()
    {
        var r = await Call("ui_capture_window", W(new JsonObject { ["reason"] = "the app has no accessible controls" }));
        Assert.Equal(ToolOutcome.Success, r.Outcome);
        Assert.Equal("ui.capture_window", Assert.Single(_h.Approvals.Requests).Operation);
        Assert.NotNull(CapturedImages.Take("cap-0123456789ab"));
        _capture = ScreenCapturePolicy.Never;
        var off = await Call("ui_capture_window", W(new JsonObject { ["reason"] = "x" }));
        Assert.Equal("screenshots_off", off.ErrorCode);
    }

    [Fact]
    public async Task ClicksNeedARecentCaptureAPointInsideItAndApproval()
    {
        Assert.Equal("capture_stale", (await Call("ui_click", new JsonObject { ["captureId"] = "cap-0123456789ab", ["x"] = 5, ["y"] = 5, ["target"] = "OK" })).ErrorCode);
        await Call("ui_capture_window", W(new JsonObject { ["reason"] = "r" }));
        Assert.Equal("outside_capture", (await Call("ui_click", new JsonObject { ["captureId"] = "cap-0123456789ab", ["x"] = 900, ["y"] = 5, ["target"] = "OK" })).ErrorCode);
        var ok = await Call("ui_click", new JsonObject { ["captureId"] = "cap-0123456789ab", ["x"] = 100, ["y"] = 50, ["target"] = "the OK button" });
        Assert.Equal(ToolOutcome.Success, ok.Outcome);
        Assert.Contains("click 100,50", _ui.Calls);
        Assert.Equal(2, _h.Approvals.Requests.Count); // the screenshot and the click
        _h.Time.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal("capture_stale", (await Call("ui_click", new JsonObject { ["captureId"] = "cap-0123456789ab", ["x"] = 1, ["y"] = 1, ["target"] = "OK" })).ErrorCode);
    }

    [Fact]
    public async Task ClosedWindowIsDetected()
    {
        await Call("ui_observe", W());
        _ui.Windows.Remove(_app);
        Assert.Equal("window_gone", (await Call("ui_invoke", W(new JsonObject { ["elementId"] = "e2" }))).ErrorCode);
    }

    [Theory]
    [InlineData("Send", true)]
    [InlineData("Delete all", true)]
    [InlineData("Buy now", true)]
    [InlineData("Save", false)]
    [InlineData("Open", false)]
    [InlineData("Sender address", false)]
    public void IrreversibleNameHeuristic(string name, bool expected) => Assert.Equal(expected, UiSession.IsIrreversibleName(name));
}

public sealed class ProviderImageTests
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 1, 2, 3];

    [Fact]
    public async Task ClaudeAttachesTheImageToTheToolResultTurn()
    {
        var h = new FixtureHandler(FixtureHandler.Sse(Fixtures.Claude()), FixtureHandler.Sse(Fixtures.ClaudeText("I see a dialog.")));
        var conv = Fixtures.Make(ProviderKind.Claude, h).StartConversation("sys", [Fixtures.FreeSpace]);
        conv.AddUserText("look");
        await Fixtures.Collect(conv);
        conv.AddToolResults([new ToolCallOutput("toolu_01", "system_get_free_space", new JsonObject { ["outcome"] = "success" }, false)]);
        conv.AddUserImage(Png, "Screenshot (untrusted)");
        await Fixtures.Collect(conv);
        var msgs = JsonNode.Parse(h.Requests[1].Body)!["messages"]!.AsArray();
        var last = msgs[^1]!;
        Assert.Equal("user", last["role"]!.GetValue<string>());
        var types = last["content"]!.AsArray().Select(c => c!["type"]!.GetValue<string>()).ToList();
        Assert.True(types.SequenceEqual(["tool_result", "image", "text"]), string.Join(",", types));
        Assert.Equal(Convert.ToBase64String(Png), last["content"]![1]!["source"]!["data"]!.GetValue<string>());
        Assert.Equal(msgs.Count, msgs.Select(m => m!["role"]!.GetValue<string>()).Aggregate(new List<string>(), (l, r) => { if (l.Count == 0 || l[^1] != r) l.Add(r); return l; }).Count); // roles alternate
    }

    [Fact]
    public async Task OpenAISendsAnInputImageMessage()
    {
        var h = new FixtureHandler(FixtureHandler.Sse(Fixtures.OpenAIText("ok")));
        var conv = Fixtures.Make(ProviderKind.OpenAI, h).StartConversation("sys", [Fixtures.FreeSpace]);
        conv.AddUserText("look");
        conv.AddUserImage(Png, "Screenshot (untrusted)");
        await Fixtures.Collect(conv);
        var input = JsonNode.Parse(h.Requests[0].Body)!["input"]!.AsArray();
        var img = input[^1]!["content"]![0]!;
        Assert.Equal("input_image", img["type"]!.GetValue<string>());
        Assert.StartsWith("data:image/png;base64,", img["image_url"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GeminiSendsInlineImageData()
    {
        var h = new FixtureHandler(FixtureHandler.Sse(Fixtures.GeminiText("ok")));
        var conv = Fixtures.Make(ProviderKind.Gemini, h).StartConversation("sys", [Fixtures.FreeSpace]);
        conv.AddUserText("look");
        conv.AddUserImage(Png, "Screenshot (untrusted)");
        await Fixtures.Collect(conv);
        var contents = JsonNode.Parse(h.Requests[0].Body)!["contents"]!.AsArray();
        Assert.Single(contents); // merged into the same user turn
        var parts = contents[0]!["parts"]!.AsArray();
        Assert.Equal("image/png", parts[1]!["inline_data"]!["mime_type"]!.GetValue<string>());
    }
}

public sealed class ConversationalPromptTests
{
    [Fact]
    public void PromptAsksToAnswerGreetingsAndTalkThingsThrough()
    {
        var p = VoxStride.Core.Agent.RunCoordinator.SystemInstruction;
        Assert.Contains("Respond to EVERYTHING the user said", p);
        Assert.Contains("how are you", p);
        Assert.Contains("thoughtful friend", p);
        Assert.Contains("untrusted data", p); // safety rules still present
    }
}
