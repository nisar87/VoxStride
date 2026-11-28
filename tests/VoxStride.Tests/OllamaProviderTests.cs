using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using VoxStride.Contracts;
using VoxStride.Providers;
using Xunit;

namespace VoxStride.Tests;

public sealed class OllamaProviderTests
{
    private sealed class Handler(params string[] bodies) : HttpMessageHandler
    {
        private int _i;
        public List<(string Url, string Body)> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.RequestUri!.ToString(), request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken)));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(bodies[Math.Min(_i++, bodies.Length - 1)], Encoding.UTF8, "application/x-ndjson") };
        }
    }

    private static IReasoningProvider Make(Handler h, string address = "http://localhost:11434") =>
        ProviderFactory.Create(new ProviderSettings(ProviderKind.Ollama, "qwen3:8b", address), h);

    [Fact]
    public async Task StreamsTextAndUsage()
    {
        var h = new Handler("""
            {"message":{"role":"assistant","content":"Hello"},"done":false}
            {"message":{"role":"assistant","content":" there!"},"done":false}
            {"message":{"role":"assistant","content":""},"done":true,"done_reason":"stop","prompt_eval_count":42,"eval_count":7}
            """);
        var conv = Make(h).StartConversation("sys", [Fixtures.FreeSpace]);
        conv.AddUserText("hi");
        var ev = await Fixtures.Collect(conv);
        Assert.Equal("Hello there!", string.Concat(ev.OfType<TextDeltaEvent>().Select(e => e.Text)));
        var u = Assert.Single(ev.OfType<UsageEvent>());
        Assert.Equal((42, 7), (u.InputTokens, u.OutputTokens));
        Assert.Equal("end_turn", Assert.Single(ev.OfType<CompletedEvent>()).StopReason);
        var (url, body) = h.Requests[0];
        Assert.Equal("http://localhost:11434/api/chat", url);
        var j = JsonNode.Parse(body)!;
        Assert.Equal("qwen3:8b", j["model"]!.GetValue<string>());
        Assert.Equal("system", j["messages"]![0]!["role"]!.GetValue<string>());
        Assert.Equal("system_get_free_space", j["tools"]![0]!["function"]!["name"]!.GetValue<string>());
        Assert.True(j["stream"]!.GetValue<bool>());
    }

    [Fact]
    public async Task ToolCallsAndResultsRoundTrip()
    {
        var h = new Handler(
            """{"message":{"role":"assistant","content":"","tool_calls":[{"function":{"name":"system_get_free_space","arguments":{"driveLetter":"C"}}}]},"done":true,"done_reason":"stop"}""",
            """{"message":{"role":"assistant","content":"You have 9 GB free."},"done":true,"done_reason":"stop"}""");
        var conv = Make(h).StartConversation("sys", [Fixtures.FreeSpace]);
        conv.AddUserText("space on C?");
        var first = await Fixtures.Collect(conv);
        var call = Assert.Single(first.OfType<ToolCallCompletedEvent>());
        Assert.Equal("system_get_free_space", call.Name);
        Assert.Equal("C", JsonNode.Parse(call.RawArguments)!["driveLetter"]!.GetValue<string>());
        conv.AddToolResults([new ToolCallOutput(call.CallId, call.Name, new JsonObject { ["freeGb"] = 9 }, false)]);
        var second = await Fixtures.Collect(conv);
        Assert.Contains("9 GB", string.Concat(second.OfType<TextDeltaEvent>().Select(e => e.Text)));
        var msgs = JsonNode.Parse(h.Requests[1].Body)!["messages"]!.AsArray();
        Assert.Equal("assistant", msgs[2]!["role"]!.GetValue<string>());
        Assert.NotNull(msgs[2]!["tool_calls"]);
        Assert.Equal("tool", msgs[3]!["role"]!.GetValue<string>());
        Assert.Equal("system_get_free_space", msgs[3]!["tool_name"]!.GetValue<string>());
    }

    [Fact]
    public async Task MissingModelIsReportedClearly()
    {
        var h = new Handler("""{"error":"model 'qwen3:8b' not found, try pulling it first"}""");
        var conv = Make(h).StartConversation("sys", []);
        conv.AddUserText("hi");
        var err = Assert.Single((await Fixtures.Collect(conv)).OfType<ProviderErrorEvent>());
        Assert.Equal(ProviderErrorKind.UnsupportedModel, err.Kind);
    }

    [Theory]
    [InlineData(null, "http://localhost:11434/")]
    [InlineData("http://192.168.1.20:11434", "http://192.168.1.20:11434/")]
    [InlineData("not a url", "http://localhost:11434/")]
    [InlineData("file:///c:/x", "http://localhost:11434/")]
    public void ServerAddress(string? stored, string expected) => Assert.Equal(expected, OllamaProvider.ServerFrom(stored).ToString());

    [Fact]
    public async Task CheckFindsTheDownloadedModel()
    {
        var ok = await Make(new Handler("""{"models":[{"name":"qwen3:8b"},{"name":"llama3.1:8b"}]}""")).CheckAsync(CancellationToken.None);
        Assert.True(ok.Ok);
        var missing = await ProviderFactory.Create(new ProviderSettings(ProviderKind.Ollama, "gemma9", "http://localhost:11434"), new Handler("""{"models":[{"name":"qwen3:8b"}]}""")).CheckAsync(CancellationToken.None);
        Assert.False(missing.Ok);
        Assert.Contains("ollama pull gemma9", missing.Message);
    }
}
