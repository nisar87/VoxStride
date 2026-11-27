using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using VoxStride.Contracts;

namespace VoxStride.Providers;

/// <summary>
/// OpenAI Responses API adapter: custom function tools, function_call / function_call_output items, SSE streaming.
/// Uses store=false and replays returned output items (including encrypted reasoning) so nothing is retained server-side by this app.
/// </summary>
public sealed class OpenAIProvider(ProviderSettings settings, HttpMessageHandler? handler = null) : ProviderBase(settings, handler)
{
    public const string DefaultBase = "https://api.openai.com/";
    public override ProviderKind Kind => ProviderKind.OpenAI;

    public override IProviderConversation StartConversation(string systemInstruction, IReadOnlyList<ToolDefinition> tools) =>
        new Conversation(this, systemInstruction, tools);

    public override Task<ProviderCheckResult> CheckAsync(CancellationToken ct)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, new Uri(BaseUri(DefaultBase), "v1/models/" + Uri.EscapeDataString(Model)));
        req.Headers.Authorization = new("Bearer", Settings.ApiKey);
        return CheckGetAsync(req, ct);
    }

    internal static JsonArray ToolsJson(IReadOnlyList<ToolDefinition> tools) =>
        new(tools.Select(t => (JsonNode)new JsonObject
        {
            ["type"] = "function",
            ["name"] = t.Name,
            ["description"] = t.Description,
            ["parameters"] = t.ParametersSchema.DeepClone(),
            ["strict"] = false, // canonical schema is validated locally; optional fields are not representable in strict mode
        }).ToArray());

    private sealed class Conversation(OpenAIProvider p, string system, IReadOnlyList<ToolDefinition> tools) : IProviderConversation
    {
        private readonly JsonArray _input = [];
        private readonly JsonArray _tools = ToolsJson(tools);

        public void AddUserText(string text) =>
            _input.Add(new JsonObject { ["type"] = "message", ["role"] = "user", ["content"] = new JsonArray(new JsonObject { ["type"] = "input_text", ["text"] = text }) });

        public void AddUserImage(byte[] png, string caption) =>
            _input.Add(new JsonObject
            {
                ["type"] = "message", ["role"] = "user",
                ["content"] = new JsonArray(
                    new JsonObject { ["type"] = "input_image", ["image_url"] = "data:image/png;base64," + Convert.ToBase64String(png) },
                    new JsonObject { ["type"] = "input_text", ["text"] = caption }),
            });

        public void AddToolResults(IReadOnlyList<ToolCallOutput> outputs)
        {
            foreach (var o in outputs)
                _input.Add(new JsonObject { ["type"] = "function_call_output", ["call_id"] = o.CallId, ["output"] = o.Result.ToJsonString() });
        }

        public async IAsyncEnumerable<ProviderEvent> StreamAsync([EnumeratorCancellation] CancellationToken ct)
        {
            var body = new JsonObject
            {
                ["model"] = p.Model,
                ["instructions"] = system,
                ["input"] = _input.DeepClone(),
                ["tools"] = _tools.DeepClone(),
                ["tool_choice"] = "auto",
                ["parallel_tool_calls"] = false,
                ["max_output_tokens"] = p.Settings.MaxOutputTokens,
                ["store"] = false,
                ["include"] = new JsonArray("reasoning.encrypted_content"),
                ["stream"] = true,
            };
            using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(p.BaseUri(DefaultBase), "v1/responses")) { Content = Json(body) };
            req.Headers.Authorization = new("Bearer", p.Settings.ApiKey);
            var (resp, error) = await p.SendStreamingAsync(req, ct).ConfigureAwait(false);
            if (error is not null) { yield return error; yield break; }

            var outputItems = new List<JsonObject>();
            var pendingCalls = new List<ToolCallCompletedEvent>();
            var argBuffers = new Dictionary<string, StringBuilder>(StringComparer.Ordinal); // item_id → args
            var itemToCall = new Dictionary<string, (string callId, string name)>(StringComparer.Ordinal);
            long inTok = 0, outTok = 0;
            var completed = false;
            string status = "completed";
            ProviderErrorEvent? streamError = null;

            using (resp)
            {
                var stream = await resp!.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                var e = SseReader.ReadAsync(stream, IdleTimeout, ct).GetAsyncEnumerator(ct);
                try
                {
                    while (true)
                    {
                        SseMessage msg;
                        try
                        {
                            if (!await e.MoveNextAsync().ConfigureAwait(false)) break;
                            msg = e.Current;
                        }
                        catch (TimeoutException) { streamError = new ProviderErrorEvent(ProviderErrorKind.Timeout, "Stream stalled."); break; }
                        catch (IOException ex) { streamError = new ProviderErrorEvent(ProviderErrorKind.Disconnected, ex.Message); break; }
                        catch (HttpRequestException ex) { streamError = new ProviderErrorEvent(ProviderErrorKind.Disconnected, ex.Message); break; }
                        if (msg.Data == "[DONE]") break;
                        JsonNode? n;
                        try { n = JsonNode.Parse(msg.Data); } catch (System.Text.Json.JsonException) { continue; }
                        var type = n?["type"]?.GetValue<string>() ?? msg.Event;
                        switch (type)
                        {
                            case "response.output_text.delta":
                                yield return new TextDeltaEvent(n!["delta"]!.GetValue<string>());
                                break;
                            case "response.output_item.added":
                            {
                                var item = n!["item"];
                                if (item?["type"]?.GetValue<string>() == "function_call")
                                {
                                    var id = item["id"]?.GetValue<string>() ?? item["call_id"]!.GetValue<string>();
                                    var callId = item["call_id"]!.GetValue<string>();
                                    var name = item["name"]!.GetValue<string>();
                                    itemToCall[id] = (callId, name);
                                    argBuffers[id] = new StringBuilder();
                                    yield return new ToolCallStartedEvent(callId, name);
                                }
                                break;
                            }
                            case "response.function_call_arguments.delta":
                            {
                                var id = n!["item_id"]!.GetValue<string>();
                                var d = n["delta"]!.GetValue<string>();
                                if (argBuffers.TryGetValue(id, out var sb)) sb.Append(d);
                                if (itemToCall.TryGetValue(id, out var c)) yield return new ToolArgsDeltaEvent(c.callId, d);
                                break;
                            }
                            case "response.output_item.done":
                            {
                                if (n!["item"] is JsonObject item)
                                {
                                    outputItems.Add((JsonObject)item.DeepClone());
                                    if (item["type"]?.GetValue<string>() == "function_call")
                                    {
                                        var id = item["id"]?.GetValue<string>() ?? "";
                                        var args = item["arguments"]?.GetValue<string>() ?? (argBuffers.TryGetValue(id, out var sb) ? sb.ToString() : "{}");
                                        pendingCalls.Add(new ToolCallCompletedEvent(item["call_id"]!.GetValue<string>(), item["name"]!.GetValue<string>(), args));
                                    }
                                }
                                break;
                            }
                            case "response.completed":
                            case "response.incomplete":
                                completed = true;
                                status = type == "response.completed" ? "completed" : "incomplete";
                                inTok = n?["response"]?["usage"]?["input_tokens"]?.GetValue<long>() ?? 0;
                                outTok = n?["response"]?["usage"]?["output_tokens"]?.GetValue<long>() ?? 0;
                                break;
                            case "response.failed":
                                streamError = new ProviderErrorEvent(ProviderErrorKind.Server, n?["response"]?["error"]?["message"]?.GetValue<string>() ?? "Response failed.");
                                break;
                            case "error":
                                var code = n?["code"]?.GetValue<string>() ?? n?["error"]?["code"]?.GetValue<string>();
                                streamError = new ProviderErrorEvent(code is "rate_limit_exceeded" ? ProviderErrorKind.RateLimited : ProviderErrorKind.Unknown,
                                    n?["message"]?.GetValue<string>() ?? n?["error"]?["message"]?.GetValue<string>() ?? "Stream error");
                                break;
                        }
                        if (completed || streamError is not null) break;
                    }
                }
                finally { await e.DisposeAsync().ConfigureAwait(false); }
            }

            yield return new UsageEvent(inTok, outTok);
            if (streamError is not null || !completed)
            {
                yield return streamError ?? new ProviderErrorEvent(ProviderErrorKind.Disconnected, "Stream ended before completion.");
                yield break;
            }
            foreach (var item in outputItems)
            {
                item.Remove("status");
                _input.Add(item);
            }
            if (status == "incomplete" && pendingCalls.Count > 0)
            {
                yield return new ProviderErrorEvent(ProviderErrorKind.InvalidRequest, "Response was cut off before the tool call finished.");
                yield break;
            }
            foreach (var c in pendingCalls) yield return c;
            yield return new CompletedEvent(status);
        }
    }
}
