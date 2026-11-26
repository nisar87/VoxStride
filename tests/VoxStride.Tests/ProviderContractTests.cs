using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using VoxStride.Contracts;
using VoxStride.Core.Json;
using VoxStride.Providers;
using Xunit;

namespace VoxStride.Tests;

/// <summary>Replays recorded-shape SSE fixtures through each adapter (no network). Live smoke tests are opt-in (docs/acceptance-tests.md).</summary>
public sealed class FixtureHandler(params Func<HttpRequestMessage, string, HttpResponseMessage>[] responses) : HttpMessageHandler
{
    private int _i;
    public List<(HttpRequestMessage Req, string Body)> Requests { get; } = [];
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
        Requests.Add((request, body));
        return responses[Math.Min(_i++, responses.Length - 1)](request, body);
    }

    public static Func<HttpRequestMessage, string, HttpResponseMessage> Sse(string sse) => (_, _) =>
        new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sse, Encoding.UTF8, "text/event-stream") };
    public static Func<HttpRequestMessage, string, HttpResponseMessage> Status(HttpStatusCode code, string body) => (_, _) =>
        new HttpResponseMessage(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}

public static class Fixtures
{
    public static readonly ToolDefinition FreeSpace = new("system_get_free_space", "Read free space",
        Schema.Object(("driveLetter", Schema.Str("Drive", "^[A-Za-z]$", 1), true)), RiskTier.Read, false);

    public static string Claude(bool complete = true, string argsA = "{\"driveL", string argsB = "etter\": \"C\"}") =>
        "event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"id\":\"msg_1\",\"usage\":{\"input_tokens\":120,\"output_tokens\":1}}}\n\n" +
        "event: ping\ndata: {\"type\":\"ping\"}\n\n" +
        "event: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}\n\n" +
        "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"Checking C.\"}}\n\n" +
        "event: content_block_stop\ndata: {\"type\":\"content_block_stop\",\"index\":0}\n\n" +
        "event: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":1,\"content_block\":{\"type\":\"tool_use\",\"id\":\"toolu_01\",\"name\":\"system_get_free_space\",\"input\":{}}}\n\n" +
        $"event: content_block_delta\ndata: {{\"type\":\"content_block_delta\",\"index\":1,\"delta\":{{\"type\":\"input_json_delta\",\"partial_json\":{JsonValue.Create(argsA)!.ToJsonString()}}}}}\n\n" +
        $"event: content_block_delta\ndata: {{\"type\":\"content_block_delta\",\"index\":1,\"delta\":{{\"type\":\"input_json_delta\",\"partial_json\":{JsonValue.Create(argsB)!.ToJsonString()}}}}}\n\n" +
        "event: content_block_stop\ndata: {\"type\":\"content_block_stop\",\"index\":1}\n\n" +
        (complete
            ? "event: message_delta\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"tool_use\"},\"usage\":{\"output_tokens\":42}}\n\n" +
              "event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n"
            : "");

    public static string ClaudeText(string text) =>
        "event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"usage\":{\"input_tokens\":200}}}\n\n" +
        "event: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}\n\n" +
        $"event: content_block_delta\ndata: {{\"type\":\"content_block_delta\",\"index\":0,\"delta\":{{\"type\":\"text_delta\",\"text\":{JsonValue.Create(text)!.ToJsonString()}}}}}\n\n" +
        "event: content_block_stop\ndata: {\"type\":\"content_block_stop\",\"index\":0}\n\n" +
        "event: message_delta\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"},\"usage\":{\"output_tokens\":10}}\n\n" +
        "event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n";

    public static string OpenAI(bool complete = true) =>
        "event: response.created\ndata: {\"type\":\"response.created\",\"sequence_number\":0}\n\n" +
        "event: response.output_item.added\ndata: {\"type\":\"response.output_item.added\",\"output_index\":0,\"item\":{\"type\":\"reasoning\",\"id\":\"rs_1\",\"summary\":[]}}\n\n" +
        "event: response.output_item.done\ndata: {\"type\":\"response.output_item.done\",\"output_index\":0,\"item\":{\"type\":\"reasoning\",\"id\":\"rs_1\",\"summary\":[],\"encrypted_content\":\"gAAA\"}}\n\n" +
        "event: response.output_item.added\ndata: {\"type\":\"response.output_item.added\",\"output_index\":1,\"item\":{\"type\":\"function_call\",\"id\":\"fc_1\",\"call_id\":\"call_abc\",\"name\":\"system_get_free_space\",\"arguments\":\"\",\"status\":\"in_progress\"}}\n\n" +
        "event: response.function_call_arguments.delta\ndata: {\"type\":\"response.function_call_arguments.delta\",\"item_id\":\"fc_1\",\"output_index\":1,\"delta\":\"{\\\"driveLetter\\\":\"}\n\n" +
        "event: response.function_call_arguments.delta\ndata: {\"type\":\"response.function_call_arguments.delta\",\"item_id\":\"fc_1\",\"output_index\":1,\"delta\":\"\\\"C\\\"}\"}\n\n" +
        "event: response.function_call_arguments.done\ndata: {\"type\":\"response.function_call_arguments.done\",\"item_id\":\"fc_1\",\"arguments\":\"{\\\"driveLetter\\\":\\\"C\\\"}\"}\n\n" +
        "event: response.output_item.done\ndata: {\"type\":\"response.output_item.done\",\"output_index\":1,\"item\":{\"type\":\"function_call\",\"id\":\"fc_1\",\"call_id\":\"call_abc\",\"name\":\"system_get_free_space\",\"arguments\":\"{\\\"driveLetter\\\":\\\"C\\\"}\",\"status\":\"completed\"}}\n\n" +
        (complete ? "event: response.completed\ndata: {\"type\":\"response.completed\",\"response\":{\"status\":\"completed\",\"usage\":{\"input_tokens\":150,\"output_tokens\":30}}}\n\n" : "");

    public static string OpenAIText(string text) =>
        $"event: response.output_text.delta\ndata: {{\"type\":\"response.output_text.delta\",\"delta\":{JsonValue.Create(text)!.ToJsonString()}}}\n\n" +
        $"event: response.output_item.done\ndata: {{\"type\":\"response.output_item.done\",\"item\":{{\"type\":\"message\",\"id\":\"msg_2\",\"role\":\"assistant\",\"status\":\"completed\",\"content\":[{{\"type\":\"output_text\",\"text\":{JsonValue.Create(text)!.ToJsonString()}}}]}}}}\n\n" +
        "event: response.completed\ndata: {\"type\":\"response.completed\",\"response\":{\"usage\":{\"input_tokens\":10,\"output_tokens\":5}}}\n\n";

    public static string Gemini(bool complete = true) =>
        "data: {\"candidates\":[{\"content\":{\"role\":\"model\",\"parts\":[{\"text\":\"Checking C.\"}]}}]}\n\n" +
        "data: {\"candidates\":[{\"content\":{\"role\":\"model\",\"parts\":[{\"functionCall\":{\"name\":\"system_get_free_space\",\"args\":{\"driveLetter\":\"C\"}},\"thoughtSignature\":\"SIG-abc\"}]}" +
        (complete ? ",\"finishReason\":\"STOP\"" : "") + "}],\"usageMetadata\":{\"promptTokenCount\":90,\"candidatesTokenCount\":12}}\n\n";

    public static string GeminiText(string text) =>
        $"data: {{\"candidates\":[{{\"content\":{{\"role\":\"model\",\"parts\":[{{\"text\":{JsonValue.Create(text)!.ToJsonString()}}}]}},\"finishReason\":\"STOP\"}}],\"usageMetadata\":{{\"promptTokenCount\":5,\"candidatesTokenCount\":3}}}}\n\n";

    public static IReasoningProvider Make(ProviderKind kind, FixtureHandler h) =>
        ProviderFactory.Create(new ProviderSettings(kind, ProviderFactory.DefaultModel(kind), "test-key-not-real"), h);

    public static string ToolCallSse(ProviderKind k) => k switch { ProviderKind.Claude => Claude(), ProviderKind.OpenAI => OpenAI(), _ => Gemini() };
    public static string TextSse(ProviderKind k, string t) => k switch { ProviderKind.Claude => ClaudeText(t), ProviderKind.OpenAI => OpenAIText(t), _ => GeminiText(t) };

    public static async Task<List<ProviderEvent>> Collect(IProviderConversation c)
    {
        var list = new List<ProviderEvent>();
        await foreach (var e in c.StreamAsync(CancellationToken.None)) list.Add(e);
        return list;
    }
}

public sealed class ProviderContractTests
{
    [Theory]
    [InlineData(ProviderKind.Claude)]
    [InlineData(ProviderKind.OpenAI)]
    [InlineData(ProviderKind.Gemini)]
    public async Task P01_AllProvidersYieldTheSameCanonicalToolCall(ProviderKind kind)
    {
        var h = new FixtureHandler(FixtureHandler.Sse(Fixtures.ToolCallSse(kind)));
        var conv = Fixtures.Make(kind, h).StartConversation("sys", [Fixtures.FreeSpace]);
        conv.AddUserText("How much space on C?");
        var events = await Fixtures.Collect(conv);
        var call = Assert.Single(events.OfType<ToolCallCompletedEvent>());
        Assert.Equal("system_get_free_space", call.Name);
        Assert.True(SchemaValidator.TryParseArguments(call.RawArguments, out var args, out _));
        Assert.Empty(SchemaValidator.Validate(args, Fixtures.FreeSpace.ParametersSchema));
        Assert.Equal("C", args!["driveLetter"]!.GetValue<string>());
        Assert.True(events.OfType<UsageEvent>().Single().InputTokens > 0);
        Assert.IsType<CompletedEvent>(events[^1]);
    }

    [Theory]
    [InlineData(ProviderKind.Claude)]
    [InlineData(ProviderKind.OpenAI)]
    [InlineData(ProviderKind.Gemini)]
    public async Task L02_TruncatedStreamYieldsNoToolCall(ProviderKind kind)
    {
        var sse = kind switch { ProviderKind.Claude => Fixtures.Claude(complete: false), ProviderKind.OpenAI => Fixtures.OpenAI(complete: false), _ => Fixtures.Gemini(complete: false) };
        var conv = Fixtures.Make(kind, new FixtureHandler(FixtureHandler.Sse(sse))).StartConversation("sys", [Fixtures.FreeSpace]);
        conv.AddUserText("x");
        var events = await Fixtures.Collect(conv);
        Assert.Empty(events.OfType<ToolCallCompletedEvent>());
        Assert.Equal(ProviderErrorKind.Disconnected, events.OfType<ProviderErrorEvent>().Single().Kind);
    }

    [Theory]
    [InlineData(ProviderKind.Claude, HttpStatusCode.Unauthorized, ProviderErrorKind.Unauthorized)]
    [InlineData(ProviderKind.OpenAI, HttpStatusCode.TooManyRequests, ProviderErrorKind.RateLimited)]
    [InlineData(ProviderKind.Gemini, HttpStatusCode.NotFound, ProviderErrorKind.UnsupportedModel)]
    [InlineData(ProviderKind.Claude, HttpStatusCode.InternalServerError, ProviderErrorKind.Server)]
    [InlineData(ProviderKind.OpenAI, HttpStatusCode.BadRequest, ProviderErrorKind.InvalidRequest)]
    public async Task P02_HttpErrorsMapToActionableKinds(ProviderKind kind, HttpStatusCode code, ProviderErrorKind expected)
    {
        var h = new FixtureHandler(FixtureHandler.Status(code, "{\"error\":{\"message\":\"nope\",\"type\":\"x\"}}"));
        var conv = Fixtures.Make(kind, h).StartConversation("sys", [Fixtures.FreeSpace]);
        conv.AddUserText("x");
        var ev = Assert.Single(await Fixtures.Collect(conv));
        var err = Assert.IsType<ProviderErrorEvent>(ev);
        Assert.Equal(expected, err.Kind);
        Assert.Equal("nope", err.Message);
    }

    [Fact]
    public async Task Claude_ContinuationSendsToolUseAndToolResultWithMatchingIds()
    {
        var h = new FixtureHandler(FixtureHandler.Sse(Fixtures.Claude()), FixtureHandler.Sse(Fixtures.ClaudeText("C has 100 GB free.")));
        var conv = Fixtures.Make(ProviderKind.Claude, h).StartConversation("sys", [Fixtures.FreeSpace]);
        conv.AddUserText("space on C?");
        await Fixtures.Collect(conv);
        conv.AddToolResults([new ToolCallOutput("toolu_01", "system_get_free_space", new JsonObject { ["outcome"] = "success" }, false)]);
        var second = await Fixtures.Collect(conv);
        Assert.Equal("C has 100 GB free.", string.Concat(second.OfType<TextDeltaEvent>().Select(t => t.Text)));
        var body = JsonNode.Parse(h.Requests[1].Body)!;
        var msgs = body["messages"]!.AsArray();
        Assert.Equal("assistant", msgs[1]!["role"]!.GetValue<string>());
        Assert.Equal("toolu_01", msgs[1]!["content"]![1]!["id"]!.GetValue<string>());
        Assert.Equal("C", msgs[1]!["content"]![1]!["input"]!["driveLetter"]!.GetValue<string>());
        Assert.Equal("toolu_01", msgs[2]!["content"]![0]!["tool_use_id"]!.GetValue<string>());
        Assert.Equal("test-key-not-real", h.Requests[0].Req.Headers.GetValues("x-api-key").Single());
        Assert.Equal(ClaudeProvider.ApiVersion, h.Requests[0].Req.Headers.GetValues("anthropic-version").Single());
        Assert.DoesNotContain("test-key-not-real", h.Requests[0].Body);
    }

    [Fact]
    public async Task OpenAI_ContinuationReplaysOutputItemsIncludingReasoningAndFunctionOutput()
    {
        var h = new FixtureHandler(FixtureHandler.Sse(Fixtures.OpenAI()), FixtureHandler.Sse(Fixtures.OpenAIText("Done.")));
        var conv = Fixtures.Make(ProviderKind.OpenAI, h).StartConversation("sys", [Fixtures.FreeSpace]);
        conv.AddUserText("space on C?");
        await Fixtures.Collect(conv);
        conv.AddToolResults([new ToolCallOutput("call_abc", "system_get_free_space", new JsonObject { ["outcome"] = "success" }, false)]);
        await Fixtures.Collect(conv);
        var first = JsonNode.Parse(h.Requests[0].Body)!;
        Assert.False(first["store"]!.GetValue<bool>());
        Assert.Equal("function", first["tools"]![0]!["type"]!.GetValue<string>());
        var input = JsonNode.Parse(h.Requests[1].Body)!["input"]!.AsArray();
        Assert.Equal("reasoning", input[1]!["type"]!.GetValue<string>());
        Assert.Equal("gAAA", input[1]!["encrypted_content"]!.GetValue<string>());
        Assert.Equal("function_call", input[2]!["type"]!.GetValue<string>());
        Assert.Equal("function_call_output", input[3]!["type"]!.GetValue<string>());
        Assert.Equal("call_abc", input[3]!["call_id"]!.GetValue<string>());
        Assert.Equal("Bearer", h.Requests[0].Req.Headers.Authorization!.Scheme);
    }

    [Fact]
    public async Task Gemini_ContinuationPreservesThoughtSignatureAndSendsFunctionResponse()
    {
        var h = new FixtureHandler(FixtureHandler.Sse(Fixtures.Gemini()), FixtureHandler.Sse(Fixtures.GeminiText("Done.")));
        var conv = Fixtures.Make(ProviderKind.Gemini, h).StartConversation("sys", [Fixtures.FreeSpace]);
        conv.AddUserText("space on C?");
        var ev = await Fixtures.Collect(conv);
        var call = ev.OfType<ToolCallCompletedEvent>().Single();
        conv.AddToolResults([new ToolCallOutput(call.CallId, call.Name, new JsonObject { ["outcome"] = "success" }, false)]);
        await Fixtures.Collect(conv);
        Assert.Contains(":streamGenerateContent?alt=sse", h.Requests[0].Req.RequestUri!.ToString());
        Assert.Contains("parametersJsonSchema", h.Requests[0].Body);
        var contents = JsonNode.Parse(h.Requests[1].Body)!["contents"]!.AsArray();
        Assert.Equal("model", contents[1]!["role"]!.GetValue<string>());
        var fcPart = contents[1]!["parts"]!.AsArray().First(p => p!["functionCall"] is not null)!;
        Assert.Equal("SIG-abc", fcPart["thoughtSignature"]!.GetValue<string>());
        Assert.Equal("system_get_free_space", contents[2]!["parts"]![0]!["functionResponse"]!["name"]!.GetValue<string>());
        Assert.Equal("test-key-not-real", h.Requests[0].Req.Headers.GetValues("x-goog-api-key").Single());
        Assert.DoesNotContain("key=", h.Requests[0].Req.RequestUri!.Query);
    }

    [Fact]
    public async Task FragmentedInvalidJsonArgumentsAreDeliveredRawAndRejectedLocally()
    {
        var h = new FixtureHandler(FixtureHandler.Sse(Fixtures.Claude(argsA: "{\"driveLetter\": \"C", argsB: "")));
        var conv = Fixtures.Make(ProviderKind.Claude, h).StartConversation("sys", [Fixtures.FreeSpace]);
        conv.AddUserText("x");
        var call = (await Fixtures.Collect(conv)).OfType<ToolCallCompletedEvent>().Single();
        Assert.False(SchemaValidator.TryParseArguments(call.RawArguments, out _, out var err));
        Assert.Contains("malformed", err);
    }

    [Fact]
    public async Task SseReaderHandlesCommentsMultilineDataAndCrLf()
    {
        var raw = ": keepalive\r\nevent: x\r\ndata: line1\r\ndata: line2\r\n\r\ndata: tail";
        var list = new List<SseMessage>();
        await foreach (var m in SseReader.ReadAsync(new MemoryStream(Encoding.UTF8.GetBytes(raw)), TimeSpan.FromSeconds(5), CancellationToken.None)) list.Add(m);
        Assert.Equal(2, list.Count);
        Assert.Equal("x", list[0].Event);
        Assert.Equal("line1\nline2", list[0].Data);
        Assert.Equal("tail", list[1].Data);
    }

    [Theory]
    [InlineData(ProviderKind.Claude, "v1/models/claude-sonnet-5-5")]
    [InlineData(ProviderKind.OpenAI, "v1/models/gpt-6.1-terra")]
    [InlineData(ProviderKind.Gemini, "v1beta/models/gemini-3.8-flash")]
    public async Task KeyCheckUsesModelEndpointAndSendsNoUserData(ProviderKind kind, string path)
    {
        var h = new FixtureHandler(FixtureHandler.Status(HttpStatusCode.OK, "{}"));
        var r = await Fixtures.Make(kind, h).CheckAsync(CancellationToken.None);
        Assert.True(r.Ok);
        Assert.Equal(HttpMethod.Get, h.Requests[0].Req.Method);
        Assert.EndsWith(path, h.Requests[0].Req.RequestUri!.AbsolutePath, StringComparison.Ordinal);
        Assert.Equal("", h.Requests[0].Body);
    }

    [Fact]
    public void ProviderRefusesMissingKey() =>
        Assert.Throws<ArgumentException>(() => ProviderFactory.Create(new ProviderSettings(ProviderKind.Claude, "m", "")));
}
