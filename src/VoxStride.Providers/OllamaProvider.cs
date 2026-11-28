using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using VoxStride.Contracts;

namespace VoxStride.Providers;

/// <summary>
/// Offline AI through Ollama running on this PC (or a server the user names): the native /api/chat endpoint with
/// tool calling and streaming (newline-delimited JSON). Nothing leaves the PC. There's no API key; the "key" slot
/// holds the server address, defaulting to http://localhost:11434. Local models start slowly the first time, so the
/// first-byte wait is longer than for cloud providers.
/// </summary>
public sealed class OllamaProvider(ProviderSettings settings, HttpMessageHandler? handler = null)
    : ProviderBase(settings with { RequestTimeout = settings.RequestTimeout ?? TimeSpan.FromMinutes(3) }, handler)
{
    public const string DefaultBase = "http://localhost:11434/";
    public override ProviderKind Kind => ProviderKind.Ollama;

    /// <summary>The address stored in the key slot, if it is one; otherwise the local default.</summary>
    public static Uri ServerFrom(string? keyOrAddress) =>
        Uri.TryCreate(keyOrAddress?.Trim(), UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps)
            ? new Uri(u.GetLeftPart(UriPartial.Authority) + "/") : new Uri(DefaultBase);

    private Uri Server => Settings.BaseUri ?? ServerFrom(Settings.ApiKey);

    public override IProviderConversation StartConversation(string systemInstruction, IReadOnlyList<ToolDefinition> tools) =>
        new Conversation(this, systemInstruction, tools);

    public override async Task<ProviderCheckResult> CheckAsync(CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, new Uri(Server, "api/tags"));
            using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return new ProviderCheckResult(false, $"Ollama answered {(int)resp.StatusCode}.", MapStatus(resp.StatusCode));
            var json = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            var names = json?["models"]?.AsArray().Select(m => m?["name"]?.GetValue<string>() ?? "").ToList() ?? [];
            var have = names.Any(n => n == Model || n == Model + ":latest" || n.Split(':')[0] == Model);
            return have
                ? new ProviderCheckResult(true, $"Ollama is running and has {Model}.")
                : new ProviderCheckResult(false, $"Ollama is running, but the model {Model} isn't downloaded. Run: ollama pull {Model}", ProviderErrorKind.UnsupportedModel);
        }
        catch (HttpRequestException) { return new ProviderCheckResult(false, "Ollama isn't running. Install it from ollama.com and start it.", ProviderErrorKind.Disconnected); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { return new ProviderCheckResult(false, "Ollama didn't answer in time.", ProviderErrorKind.Timeout); }
    }

    internal static JsonArray ToolsJson(IReadOnlyList<ToolDefinition> tools) =>
        new(tools.Select(t => (JsonNode)new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject { ["name"] = t.Name, ["description"] = t.Description, ["parameters"] = t.ParametersSchema.DeepClone() },
        }).ToArray());

    private sealed class Conversation(OllamaProvider p, string system, IReadOnlyList<ToolDefinition> tools) : IProviderConversation
    {
        private readonly JsonArray _messages = [new JsonObject { ["role"] = "system", ["content"] = system }];
        private readonly JsonArray _tools = ToolsJson(tools);
        private int _ids;

        public void AddUserText(string text) => _messages.Add(new JsonObject { ["role"] = "user", ["content"] = text });

        public void AddUserImage(byte[] png, string caption) =>
            _messages.Add(new JsonObject { ["role"] = "user", ["content"] = caption, ["images"] = new JsonArray(Convert.ToBase64String(png)) });

        public void AddToolResults(IReadOnlyList<ToolCallOutput> outputs)
        {
            foreach (var o in outputs)
                _messages.Add(new JsonObject { ["role"] = "tool", ["tool_name"] = o.Name, ["content"] = o.Result.ToJsonString() });
        }

        public async IAsyncEnumerable<ProviderEvent> StreamAsync([EnumeratorCancellation] CancellationToken ct)
        {
            var body = new JsonObject
            {
                ["model"] = p.Model,
                ["messages"] = _messages.DeepClone(),
                ["tools"] = _tools.DeepClone(),
                ["stream"] = true,
                ["options"] = new JsonObject { ["num_predict"] = p.Settings.MaxOutputTokens },
            };
            var req = new HttpRequestMessage(HttpMethod.Post, new Uri(p.Server, "api/chat")) { Content = Json(body) };
            var (resp, error) = await p.SendStreamingAsync(req, ct).ConfigureAwait(false);
            if (error is not null || resp is null)
            {
                yield return error is { Kind: ProviderErrorKind.Disconnected } ? error with { Message = "Ollama isn't running (start the Ollama app)." } : error ?? new ProviderErrorEvent(ProviderErrorKind.Unknown, "No response.");
                yield break;
            }

            var text = new System.Text.StringBuilder();
            var calls = new JsonArray();
            var pending = new List<ToolCallCompletedEvent>();
            long inTok = 0, outTok = 0;
            string? doneReason = null;
            ProviderErrorEvent? streamError = null;
            using (resp)
            {
                using var reader = new StreamReader(await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false));
                while (true)
                {
                    string? line;
                    try
                    {
                        var read = reader.ReadLineAsync(ct).AsTask();
                        if (await Task.WhenAny(read, Task.Delay(IdleTimeout, ct)).ConfigureAwait(false) != read) { streamError = new ProviderErrorEvent(ProviderErrorKind.Timeout, "Ollama stopped responding."); break; }
                        line = await read.ConfigureAwait(false);
                    }
                    catch (IOException ex) { streamError = new ProviderErrorEvent(ProviderErrorKind.Disconnected, ex.Message); break; }
                    if (line is null) break;
                    if (line.Length == 0) continue;
                    JsonNode? n;
                    try { n = JsonNode.Parse(line); } catch (JsonException) { continue; }
                    if (n?["error"]?.GetValue<string>() is { } err) { streamError = new ProviderErrorEvent(err.Contains("not found", StringComparison.OrdinalIgnoreCase) ? ProviderErrorKind.UnsupportedModel : ProviderErrorKind.Unknown, err); break; }
                    if (n?["message"] is JsonObject m)
                    {
                        if (m["content"]?.GetValue<string>() is { Length: > 0 } delta) { text.Append(delta); yield return new TextDeltaEvent(delta); }
                        if (m["tool_calls"] is JsonArray tc)
                            foreach (var c in tc)
                            {
                                if (c?["function"] is not JsonObject f || f["name"]?.GetValue<string>() is not { Length: > 0 } name) continue;
                                var id = "ollama-" + (++_ids);
                                var args = f["arguments"] switch { JsonObject o => o.ToJsonString(), JsonValue v when v.TryGetValue<string>(out var s) => s, _ => "{}" };
                                calls.Add(new JsonObject { ["function"] = new JsonObject { ["name"] = name, ["arguments"] = JsonNode.Parse(args) } });
                                yield return new ToolCallStartedEvent(id, name);
                                pending.Add(new ToolCallCompletedEvent(id, name, args));
                            }
                    }
                    if (n?["done"]?.GetValue<bool>() == true)
                    {
                        doneReason = n["done_reason"]?.GetValue<string>() ?? "stop";
                        inTok = n["prompt_eval_count"]?.GetValue<long>() ?? 0;
                        outTok = n["eval_count"]?.GetValue<long>() ?? 0;
                        break;
                    }
                }
            }
            yield return new UsageEvent(inTok, outTok);
            if (streamError is not null || doneReason is null) { yield return streamError ?? new ProviderErrorEvent(ProviderErrorKind.Disconnected, "Ollama ended the reply early."); yield break; }
            var assistant = new JsonObject { ["role"] = "assistant", ["content"] = text.ToString() };
            if (calls.Count > 0) assistant["tool_calls"] = calls;
            _messages.Add(assistant);
            foreach (var c in pending) yield return c;
            yield return new CompletedEvent(pending.Count > 0 ? "tool_use" : doneReason == "length" ? "max_tokens" : "end_turn");
        }
    }
}
