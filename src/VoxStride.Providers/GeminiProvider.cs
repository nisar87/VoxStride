using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using VoxStride.Contracts;

namespace VoxStride.Providers;

/// <summary>
/// Gemini generateContent adapter (streamGenerateContent?alt=sse) with function declarations and function responses.
/// Model parts are replayed exactly as returned so thought signatures are preserved.
/// </summary>
public sealed class GeminiProvider(ProviderSettings settings, HttpMessageHandler? handler = null) : ProviderBase(settings, handler)
{
    public const string DefaultBase = "https://generativelanguage.googleapis.com/";
    public override ProviderKind Kind => ProviderKind.Gemini;

    public override IProviderConversation StartConversation(string systemInstruction, IReadOnlyList<ToolDefinition> tools) =>
        new Conversation(this, systemInstruction, tools);

    public override Task<ProviderCheckResult> CheckAsync(CancellationToken ct)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, new Uri(BaseUri(DefaultBase), "v1beta/models/" + Uri.EscapeDataString(Model)));
        req.Headers.Add("x-goog-api-key", Settings.ApiKey);
        return CheckGetAsync(req, ct);
    }

    internal static JsonArray ToolsJson(IReadOnlyList<ToolDefinition> tools) =>
        new(new JsonObject
        {
            ["functionDeclarations"] = new JsonArray(tools.Select(t => (JsonNode)new JsonObject
            {
                ["name"] = t.Name,
                ["description"] = t.Description,
                ["parametersJsonSchema"] = t.ParametersSchema.DeepClone(),
            }).ToArray()),
        });

    private sealed class Conversation(GeminiProvider p, string system, IReadOnlyList<ToolDefinition> tools) : IProviderConversation
    {
        private readonly JsonArray _contents = [];
        private readonly JsonArray _tools = ToolsJson(tools);
        private int _syntheticId;

        public void AddUserText(string text) =>
            _contents.Add(new JsonObject { ["role"] = "user", ["parts"] = new JsonArray(new JsonObject { ["text"] = text }) });

        public void AddUserImage(byte[] png, string caption)
        {
            var parts = new JsonArray(
                new JsonObject { ["inline_data"] = new JsonObject { ["mime_type"] = "image/png", ["data"] = Convert.ToBase64String(png) } },
                new JsonObject { ["text"] = caption });
            if (_contents.Count > 0 && _contents[^1] is JsonObject last && last["role"]?.GetValue<string>() == "user" && last["parts"] is JsonArray existing)
                foreach (var p in parts.ToList()) { parts.Remove(p); existing.Add(p); }
            else _contents.Add(new JsonObject { ["role"] = "user", ["parts"] = parts });
        }

        public void AddToolResults(IReadOnlyList<ToolCallOutput> outputs)
        {
            var parts = new JsonArray();
            foreach (var o in outputs)
            {
                var fr = new JsonObject { ["name"] = o.Name, ["response"] = o.Result.DeepClone() };
                if (!o.CallId.StartsWith("gemini-", StringComparison.Ordinal)) fr["id"] = o.CallId;
                parts.Add(new JsonObject { ["functionResponse"] = fr });
            }
            _contents.Add(new JsonObject { ["role"] = "user", ["parts"] = parts });
        }

        public async IAsyncEnumerable<ProviderEvent> StreamAsync([EnumeratorCancellation] CancellationToken ct)
        {
            var body = new JsonObject
            {
                ["systemInstruction"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = system }) },
                ["contents"] = _contents.DeepClone(),
                ["tools"] = _tools.DeepClone(),
                ["toolConfig"] = new JsonObject { ["functionCallingConfig"] = new JsonObject { ["mode"] = "AUTO" } },
                ["generationConfig"] = GeminiThinking.Apply(new JsonObject { ["maxOutputTokens"] = p.Settings.MaxOutputTokens }, p.Model, fast: false),
            };
            var uri = new Uri(p.BaseUri(DefaultBase), "v1beta/models/" + Uri.EscapeDataString(p.Model) + ":streamGenerateContent?alt=sse");
            using var req = new HttpRequestMessage(HttpMethod.Post, uri) { Content = Json(body) };
            req.Headers.Add("x-goog-api-key", p.Settings.ApiKey);
            var (resp, error) = await p.SendStreamingAsync(req, ct).ConfigureAwait(false);
            if (error is not null) { yield return error; yield break; }

            var modelParts = new JsonArray();
            var pendingCalls = new List<ToolCallCompletedEvent>();
            long inTok = 0, outTok = 0;
            string? finish = null;
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
                        JsonNode? n;
                        try { n = JsonNode.Parse(msg.Data); } catch (System.Text.Json.JsonException) { continue; }
                        if (n?["error"] is JsonObject err)
                        {
                            streamError = new ProviderErrorEvent(err["code"]?.GetValue<int>() == 429 ? ProviderErrorKind.RateLimited : ProviderErrorKind.Unknown, err["message"]?.GetValue<string>() ?? "Stream error");
                            break;
                        }
                        if (n?["usageMetadata"] is JsonObject um)
                        {
                            inTok = um["promptTokenCount"]?.GetValue<long>() ?? inTok;
                            outTok = (um["candidatesTokenCount"]?.GetValue<long>() ?? 0) + (um["thoughtsTokenCount"]?.GetValue<long>() ?? 0);
                        }
                        var cand = n?["candidates"]?[0];
                        if (cand is null)
                        {
                            if (n?["promptFeedback"]?["blockReason"] is not null) { streamError = new ProviderErrorEvent(ProviderErrorKind.Refused, "Prompt was blocked."); break; }
                            continue;
                        }
                        if (cand["content"]?["parts"] is JsonArray parts)
                        {
                            foreach (var part in parts)
                            {
                                if (part is not JsonObject po) continue;
                                modelParts.Add(po.DeepClone());
                                if (po["thought"]?.GetValue<bool>() == true) continue;
                                if (po["text"] is JsonValue tv) yield return new TextDeltaEvent(tv.GetValue<string>());
                                if (po["functionCall"] is JsonObject fc)
                                {
                                    var name = fc["name"]!.GetValue<string>();
                                    var id = fc["id"]?.GetValue<string>() ?? "gemini-" + (++_syntheticId);
                                    var args = fc["args"] is JsonObject a ? a.ToJsonString() : "{}";
                                    yield return new ToolCallStartedEvent(id, name);
                                    pendingCalls.Add(new ToolCallCompletedEvent(id, name, args));
                                }
                            }
                        }
                        finish = cand["finishReason"]?.GetValue<string>() ?? finish;
                    }
                }
                finally { await e.DisposeAsync().ConfigureAwait(false); }
            }

            yield return new UsageEvent(inTok, outTok);
            if (streamError is not null || finish is null)
            {
                yield return streamError ?? new ProviderErrorEvent(ProviderErrorKind.Disconnected, "Stream ended before completion.");
                yield break;
            }
            if (finish is "SAFETY" or "PROHIBITED_CONTENT" or "BLOCKLIST" or "SPII" or "RECITATION")
            {
                yield return new ProviderErrorEvent(ProviderErrorKind.Refused, "Response was blocked (" + finish + ").");
                yield break;
            }
            if (finish is "MALFORMED_FUNCTION_CALL" or "UNEXPECTED_TOOL_CALL")
            {
                yield return new ProviderErrorEvent(ProviderErrorKind.InvalidRequest, "The model produced an invalid tool call.");
                yield break;
            }
            if (modelParts.Count > 0) _contents.Add(new JsonObject { ["role"] = "model", ["parts"] = modelParts });
            if (finish == "MAX_TOKENS" && pendingCalls.Count > 0)
            {
                yield return new ProviderErrorEvent(ProviderErrorKind.InvalidRequest, "Output limit reached during a tool call.");
                yield break;
            }
            foreach (var c in pendingCalls) yield return c;
            yield return new CompletedEvent(finish);
        }
    }
}

/// <summary>
/// Gemini 3 models think at a high level by default, which made a simple voice request take ~14 s before the first
/// token. A voice assistant needs fast turns, so VoxStride asks for little thinking: "low" for the agent (supported
/// by Gemini 3 Flash and Pro) and "minimal" for transcription on Flash models. Gemini 2.5 Flash gets a zero
/// thinking budget. Other models are left as they are.
/// </summary>
public static class GeminiThinking
{
    public static JsonObject Apply(JsonObject generationConfig, string model, bool fast)
    {
        var m = model.ToLowerInvariant();
        if (m.StartsWith("gemini-3", StringComparison.Ordinal) || m.StartsWith("gemini-4", StringComparison.Ordinal))
        {
            var level = fast && (m.Contains("flash", StringComparison.Ordinal)) ? "minimal" : "low";
            generationConfig["thinkingConfig"] = new JsonObject { ["thinkingLevel"] = level };
        }
        else if (m.StartsWith("gemini-2.5-flash", StringComparison.Ordinal))
            generationConfig["thinkingConfig"] = new JsonObject { ["thinkingBudget"] = 0 };
        return generationConfig;
    }
}
