using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using VoxStride.Contracts;

namespace VoxStride.Providers;

/// <summary>Anthropic Messages API adapter: client tools, tool_use/tool_result loop, SSE streaming.</summary>
public sealed class ClaudeProvider(ProviderSettings settings, HttpMessageHandler? handler = null) : ProviderBase(settings, handler)
{
    public const string DefaultBase = "https://api.anthropic.com/";
    public const string ApiVersion = "2023-06-01";
    public override ProviderKind Kind => ProviderKind.Claude;

    public override IProviderConversation StartConversation(string systemInstruction, IReadOnlyList<ToolDefinition> tools) =>
        new Conversation(this, systemInstruction, tools);

    public override Task<ProviderCheckResult> CheckAsync(CancellationToken ct)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, new Uri(BaseUri(DefaultBase), "v1/models/" + Uri.EscapeDataString(Model)));
        AddHeaders(req);
        return CheckGetAsync(req, ct);
    }

    private void AddHeaders(HttpRequestMessage req)
    {
        req.Headers.Add("x-api-key", Settings.ApiKey);
        req.Headers.Add("anthropic-version", ApiVersion);
    }

    internal static JsonArray ToolsJson(IReadOnlyList<ToolDefinition> tools) =>
        new(tools.Select(t => (JsonNode)new JsonObject
        {
            ["name"] = t.Name,
            ["description"] = t.Description,
            ["input_schema"] = t.ParametersSchema.DeepClone(),
        }).ToArray());

    private sealed class Conversation(ClaudeProvider p, string system, IReadOnlyList<ToolDefinition> tools) : IProviderConversation
    {
        private readonly JsonArray _messages = [];
        private readonly JsonArray _tools = ToolsJson(tools);

        public void AddUserText(string text) => _messages.Add(new JsonObject { ["role"] = "user", ["content"] = text });

        public void AddToolResults(IReadOnlyList<ToolCallOutput> outputs)
        {
            var content = new JsonArray();
            foreach (var o in outputs)
                content.Add(new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = o.CallId, ["content"] = o.Result.ToJsonString(), ["is_error"] = o.IsError });
            _messages.Add(new JsonObject { ["role"] = "user", ["content"] = content });
        }

        public async IAsyncEnumerable<ProviderEvent> StreamAsync([EnumeratorCancellation] CancellationToken ct)
        {
            var body = new JsonObject
            {
                ["model"] = p.Model,
                ["max_tokens"] = p.Settings.MaxOutputTokens,
                ["system"] = system,
                ["messages"] = _messages.DeepClone(),
                ["tools"] = _tools.DeepClone(),
                ["stream"] = true,
            };
            using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(p.BaseUri(DefaultBase), "v1/messages")) { Content = Json(body) };
            p.AddHeaders(req);
            var (resp, error) = await p.SendStreamingAsync(req, ct).ConfigureAwait(false);
            if (error is not null) { yield return error; yield break; }

            var blocks = new SortedDictionary<int, (string type, string? id, string? name, StringBuilder buf)>();
            var pendingCalls = new List<ToolCallCompletedEvent>();
            long inTok = 0, outTok = 0;
            string? stop = null;
            var completed = false;
            ProviderErrorEvent? streamError = null;

            using (resp)
            {
                var stream = await resp!.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                var enumerator = SseReader.ReadAsync(stream, IdleTimeout, ct).GetAsyncEnumerator(ct);
                try
                {
                    while (true)
                    {
                        SseMessage msg;
                        try
                        {
                            if (!await enumerator.MoveNextAsync().ConfigureAwait(false)) break;
                            msg = enumerator.Current;
                        }
                        catch (TimeoutException) { streamError = new ProviderErrorEvent(ProviderErrorKind.Timeout, "Stream stalled."); break; }
                        catch (IOException ex) { streamError = new ProviderErrorEvent(ProviderErrorKind.Disconnected, ex.Message); break; }
                        catch (HttpRequestException ex) { streamError = new ProviderErrorEvent(ProviderErrorKind.Disconnected, ex.Message); break; }

                        JsonNode? n;
                        try { n = JsonNode.Parse(msg.Data); } catch (System.Text.Json.JsonException) { continue; }
                        var type = n?["type"]?.GetValue<string>() ?? msg.Event;
                        switch (type)
                        {
                            case "message_start":
                                inTok += n?["message"]?["usage"]?["input_tokens"]?.GetValue<long>() ?? 0;
                                break;
                            case "content_block_start":
                            {
                                var idx = n!["index"]!.GetValue<int>();
                                var cb = n["content_block"]!;
                                var bt = cb["type"]!.GetValue<string>();
                                blocks[idx] = (bt, cb["id"]?.GetValue<string>(), cb["name"]?.GetValue<string>(), new StringBuilder(bt == "text" ? cb["text"]?.GetValue<string>() ?? "" : ""));
                                if (bt == "tool_use") yield return new ToolCallStartedEvent(blocks[idx].id!, blocks[idx].name!);
                                break;
                            }
                            case "content_block_delta":
                            {
                                var idx = n!["index"]!.GetValue<int>();
                                if (!blocks.TryGetValue(idx, out var b)) break;
                                var d = n["delta"]!;
                                switch (d["type"]?.GetValue<string>())
                                {
                                    case "text_delta":
                                        var t = d["text"]!.GetValue<string>();
                                        b.buf.Append(t);
                                        yield return new TextDeltaEvent(t);
                                        break;
                                    case "input_json_delta":
                                        var pj = d["partial_json"]!.GetValue<string>();
                                        b.buf.Append(pj);
                                        yield return new ToolArgsDeltaEvent(b.id!, pj);
                                        break;
                                }
                                break;
                            }
                            case "content_block_stop":
                            {
                                var idx = n!["index"]!.GetValue<int>();
                                if (blocks.TryGetValue(idx, out var b) && b.type == "tool_use")
                                    pendingCalls.Add(new ToolCallCompletedEvent(b.id!, b.name!, b.buf.Length == 0 ? "{}" : b.buf.ToString()));
                                break;
                            }
                            case "message_delta":
                                stop = n?["delta"]?["stop_reason"]?.GetValue<string>() ?? stop;
                                outTok = n?["usage"]?["output_tokens"]?.GetValue<long>() ?? outTok;
                                break;
                            case "message_stop":
                                completed = true;
                                break;
                            case "error":
                                var et = n?["error"]?["type"]?.GetValue<string>();
                                streamError = new ProviderErrorEvent(et == "overloaded_error" ? ProviderErrorKind.Server : et == "rate_limit_error" ? ProviderErrorKind.RateLimited : ProviderErrorKind.Unknown,
                                    n?["error"]?["message"]?.GetValue<string>() ?? "Stream error");
                                break;
                        }
                        if (completed || streamError is not null) break;
                    }
                }
                finally { await enumerator.DisposeAsync().ConfigureAwait(false); }
            }

            yield return new UsageEvent(inTok, outTok);
            if (streamError is not null || !completed)
            {
                // Truncated or failed stream: incomplete tool calls are discarded, history is not advanced.
                yield return streamError ?? new ProviderErrorEvent(ProviderErrorKind.Disconnected, "Stream ended before completion.");
                yield break;
            }

            // Append the assistant turn exactly as produced so tool_result ids line up.
            var content = new JsonArray();
            foreach (var (_, b) in blocks)
            {
                if (b.type == "text" && b.buf.Length > 0) content.Add(new JsonObject { ["type"] = "text", ["text"] = b.buf.ToString() });
                else if (b.type == "tool_use")
                {
                    JsonNode input;
                    try { input = JsonNode.Parse(b.buf.Length == 0 ? "{}" : b.buf.ToString()) as JsonObject ?? new JsonObject(); }
                    catch (System.Text.Json.JsonException) { input = new JsonObject(); }
                    content.Add(new JsonObject { ["type"] = "tool_use", ["id"] = b.id, ["name"] = b.name, ["input"] = input });
                }
            }
            if (content.Count > 0) _messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = content });
            if (stop == "refusal") { yield return new ProviderErrorEvent(ProviderErrorKind.Refused, "The model declined."); yield break; }
            if (stop == "max_tokens" && pendingCalls.Count > 0)
            {
                yield return new ProviderErrorEvent(ProviderErrorKind.InvalidRequest, "Output limit reached before the tool call finished.");
                yield break;
            }
            foreach (var c in pendingCalls) yield return c;
            yield return new CompletedEvent(stop ?? "end_turn");
        }
    }
}
