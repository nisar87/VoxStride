using System.Buffers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using VoxStride.Contracts;

namespace VoxStride.Providers;

/// <summary>
/// Encodes and decodes Gemini Live (BidiGenerateContent) WebSocket messages. Kept separate from the socket so every
/// message shape is unit-tested without a network.
/// </summary>
public static class GeminiLiveCodec
{
    public const string Endpoint = "wss://generativelanguage.googleapis.com/ws/google.ai.generativelanguage.v1beta.GenerativeService.BidiGenerateContent";

    /// <summary>Tried in order until one connects; the newest live model first.</summary>
    public static readonly string[] DefaultModels = ["gemini-3.8-live", "gemini-3.1-flash-live-preview", "gemini-2.5-flash-native-audio-preview-09-2025"];

    /// <param name="schemaStyle">"json" sends parametersJsonSchema (full JSON Schema); "openapi" sends the OpenAPI subset as parameters.</param>
    public static string Setup(string model, LiveSetup s, string schemaStyle = "json")
    {
        var declarations = new JsonArray(s.Tools.Select(t =>
        {
            var d = new JsonObject { ["name"] = t.Name, ["description"] = t.Description };
            if (schemaStyle == "json") d["parametersJsonSchema"] = t.ParametersSchema.DeepClone();
            else d["parameters"] = OpenApi(t.ParametersSchema);
            return (JsonNode)d;
        }).ToArray());
        var tools = new JsonArray(new JsonObject { ["functionDeclarations"] = declarations });
        if (s.WebSearch) tools.Add(new JsonObject { ["googleSearch"] = new JsonObject() });
        var setup = new JsonObject
        {
            ["model"] = "models/" + model,
            ["generationConfig"] = new JsonObject
            {
                ["responseModalities"] = new JsonArray("AUDIO"),
                ["speechConfig"] = new JsonObject { ["voiceConfig"] = new JsonObject { ["prebuiltVoiceConfig"] = new JsonObject { ["voiceName"] = s.Voice } } },
            },
            ["systemInstruction"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = s.SystemInstruction }) },
            ["tools"] = tools,
            ["inputAudioTranscription"] = new JsonObject(),
            ["outputAudioTranscription"] = new JsonObject(),
            ["realtimeInputConfig"] = new JsonObject
            {
                ["automaticActivityDetection"] = new JsonObject { ["silenceDurationMs"] = 700, ["prefixPaddingMs"] = 200 },
            },
            ["contextWindowCompression"] = new JsonObject { ["slidingWindow"] = new JsonObject() },
            ["sessionResumption"] = s.ResumeHandle is { Length: > 0 } h ? new JsonObject { ["handle"] = h } : new JsonObject(),
        };
        return new JsonObject { ["setup"] = setup }.ToJsonString();
    }

    /// <summary>Reduces a JSON Schema to the OpenAPI subset Gemini's "parameters" accepts.</summary>
    public static JsonObject OpenApi(JsonObject schema)
    {
        var o = new JsonObject();
        foreach (var key in new[] { "type", "description", "enum", "required", "minimum", "maximum", "maxItems", "minItems", "maxLength", "pattern", "format" })
            if (schema[key] is { } v) o[key] = v.DeepClone();
        if (schema["properties"] is JsonObject props)
        {
            var p = new JsonObject();
            foreach (var (k, v) in props) if (v is JsonObject child) p[k] = OpenApi(child);
            o["properties"] = p;
        }
        if (schema["items"] is JsonObject items) o["items"] = OpenApi(items);
        if (o["required"] is JsonArray { Count: 0 }) o.Remove("required");
        return o;
    }

    public static string Audio(ReadOnlySpan<byte> pcm16k) =>
        "{\"realtimeInput\":{\"audio\":{\"data\":\"" + Convert.ToBase64String(pcm16k) + "\",\"mimeType\":\"audio/pcm;rate=16000\"}}}";

    public static string AudioStreamEnd() => "{\"realtimeInput\":{\"audioStreamEnd\":true}}";

    public static string Image(byte[] image, string mime) =>
        new JsonObject { ["realtimeInput"] = new JsonObject { ["video"] = new JsonObject { ["data"] = Convert.ToBase64String(image), ["mimeType"] = mime } } }.ToJsonString();

    public static string Text(string text) => new JsonObject { ["realtimeInput"] = new JsonObject { ["text"] = text } }.ToJsonString();

    public static string ToolResponse(IReadOnlyList<LiveToolResponse> responses) =>
        new JsonObject
        {
            ["toolResponse"] = new JsonObject
            {
                ["functionResponses"] = new JsonArray(responses.Select(r => (JsonNode)new JsonObject
                {
                    ["id"] = r.Id,
                    ["name"] = r.Name,
                    ["response"] = r.Response.DeepClone(),
                }).ToArray()),
            },
        }.ToJsonString();

    /// <summary>Decodes one server message into zero or more events (one message can carry audio and text together).</summary>
    public static IReadOnlyList<LiveServerEvent> Parse(string json, string model)
    {
        var events = new List<LiveServerEvent>();
        JsonNode? root;
        try { root = JsonNode.Parse(json); }
        catch (JsonException) { return events; }
        if (root is not JsonObject m) return events;

        if (m["setupComplete"] is not null) events.Add(new LiveSetupComplete(model));
        if (m["serverContent"] is JsonObject sc)
        {
            if (sc["interrupted"]?.GetValue<bool>() == true) events.Add(new LiveInterrupted());
            if (sc["inputTranscription"]?["text"]?.GetValue<string>() is { Length: > 0 } it) events.Add(new LiveInputText(it));
            if (sc["modelTurn"]?["parts"] is JsonArray parts)
                foreach (var p in parts)
                {
                    if (p?["inlineData"] is JsonObject inline && inline["data"]?.GetValue<string>() is { Length: > 0 } b64)
                    {
                        var mime = inline["mimeType"]?.GetValue<string>() ?? "audio/pcm;rate=24000";
                        if (!mime.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)) continue;
                        try { events.Add(new LiveAudio(Convert.FromBase64String(b64), RateOf(mime))); }
                        catch (FormatException) { }
                    }
                }
            if (sc["outputTranscription"]?["text"]?.GetValue<string>() is { Length: > 0 } ot) events.Add(new LiveOutputText(ot));
            if (sc["turnComplete"]?.GetValue<bool>() == true) events.Add(new LiveTurnComplete());
        }
        if (m["toolCall"]?["functionCalls"] is JsonArray calls)
        {
            var list = new List<LiveFunctionCall>();
            foreach (var c in calls)
                if (c?["name"]?.GetValue<string>() is { Length: > 0 } name)
                    list.Add(new LiveFunctionCall(c["id"]?.GetValue<string>() ?? "call-" + Guid.NewGuid().ToString("N")[..8], name, c["args"] as JsonObject is { } a ? (JsonObject)a.DeepClone() : new JsonObject()));
            if (list.Count > 0) events.Add(new LiveToolCall(list));
        }
        if (m["toolCallCancellation"]?["ids"] is JsonArray ids)
            events.Add(new LiveToolCancel(ids.Select(i => i?.GetValue<string>() ?? "").Where(i => i.Length > 0).ToList()));
        if (m["goAway"] is JsonObject ga)
            events.Add(new LiveGoAway(ParseDuration(ga["timeLeft"]?.GetValue<string>())));
        if (m["sessionResumptionUpdate"] is JsonObject su && su["resumable"]?.GetValue<bool>() != false && su["newHandle"]?.GetValue<string>() is { Length: > 0 } h)
            events.Add(new LiveResumeHandle(h));
        return events;
    }

    public static int RateOf(string mime)
    {
        var i = mime.IndexOf("rate=", StringComparison.OrdinalIgnoreCase);
        if (i < 0) return 24000;
        var digits = new string(mime[(i + 5)..].TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, out var r) && r is >= 8000 and <= 48000 ? r : 24000;
    }

    public static TimeSpan? ParseDuration(string? s)
    {
        if (string.IsNullOrEmpty(s) || !s.EndsWith('s')) return null;
        return double.TryParse(s[..^1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? TimeSpan.FromSeconds(v) : null;
    }
}

/// <summary>
/// One Gemini Live session over a WebSocket. The API key goes in the x-goog-api-key header, never in the URL or the
/// log. If the newest model or the full JSON-Schema tool format is refused at setup, the next model or the OpenAPI
/// tool format is tried, and the first combination that works is remembered for later sessions.
/// </summary>
public sealed class GeminiLiveTransport(string apiKey, IReadOnlyList<string>? models = null, Uri? endpoint = null) : ILiveTransport
{
    private static string? _workingModel;
    private static string _workingSchema = "json";

    private readonly IReadOnlyList<string> _models = models ?? GeminiLiveCodec.DefaultModels;
    private readonly SemaphoreSlim _send = new(1, 1);
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private Task? _receive;
    private string _model = "";
    private long _sentAudioBytes, _receivedAudioBytes;

    public string Name => "Gemini Live " + _model;
    public bool IsOpen => _ws?.State == WebSocketState.Open;
    public event Action<LiveServerEvent>? Received;

    public async Task ConnectAsync(LiveSetup setup, CancellationToken ct)
    {
        var order = _workingModel is { } wm ? new[] { wm }.Concat(_models.Where(m => m != wm)).ToList() : _models.ToList();
        var errors = new List<string>();
        foreach (var model in order)
            foreach (var schema in _workingSchema == "json" ? new[] { "json", "openapi" } : ["openapi", "json"])
            {
                var (ok, error, retryOtherSchema) = await TryConnectAsync(model, schema, setup, ct).ConfigureAwait(false);
                if (ok) { _workingModel = model; _workingSchema = schema; return; }
                errors.Add($"{model}/{schema}: {error}");
                Log.Warn("live", $"Gemini Live setup failed for {model} ({schema} tools): {error}");
                if (!retryOtherSchema) break; // a model problem: try the next model
            }
        throw new InvalidOperationException(errors.Count > 0 ? errors[^1] : "no live model available");
    }

    private async Task<(bool Ok, string? Error, bool TrySchema)> TryConnectAsync(string model, string schema, LiveSetup setup, CancellationToken ct)
    {
        var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("x-goog-api-key", apiKey);
        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            await ws.ConnectAsync(endpoint ?? new Uri(GeminiLiveCodec.Endpoint), timeout.Token).ConfigureAwait(false);
            var json = GeminiLiveCodec.Setup(model, setup, schema);
            await ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, timeout.Token).ConfigureAwait(false);
            // The first reply must be setupComplete; an error arrives as a close frame with the reason.
            var first = await ReadMessageAsync(ws, timeout.Token).ConfigureAwait(false);
            if (first is null)
            {
                var reason = ws.CloseStatusDescription ?? ws.CloseStatus?.ToString() ?? "closed";
                ws.Dispose();
                var schemaProblem = reason.Contains("parameters", StringComparison.OrdinalIgnoreCase) || reason.Contains("schema", StringComparison.OrdinalIgnoreCase) || reason.Contains("Invalid JSON payload", StringComparison.OrdinalIgnoreCase);
                return (false, reason, schemaProblem);
            }
            var events = GeminiLiveCodec.Parse(first, model);
            if (!events.OfType<LiveSetupComplete>().Any()) { ws.Dispose(); return (false, "unexpected first message", false); }
            _ws = ws; _model = model;
            _cts = new CancellationTokenSource();
            _receive = Task.Run(() => ReceiveLoopAsync(ws, _cts.Token));
            Log.Info("live", $"connected to Gemini Live {model} ({schema} tools, {setup.Tools.Count} tools, voice {setup.Voice}, resume={(setup.ResumeHandle is null ? "no" : "yes")}) in {sw.ElapsedMilliseconds} ms");
            foreach (var e in events) Received?.Invoke(e);
            return (true, null, false);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or HttpRequestException)
        {
            ws.Dispose();
            if (ct.IsCancellationRequested) throw;
            return (false, ex.Message, false);
        }
    }

    private static async Task<string?> ReadMessageAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            using var ms = new MemoryStream();
            while (true)
            {
                var r = await ws.ReceiveAsync(buffer, ct).ConfigureAwait(false);
                if (r.MessageType == WebSocketMessageType.Close) return null;
                ms.Write(buffer, 0, r.Count);
                if (ms.Length > 16 * 1024 * 1024) throw new InvalidDataException("live message too large");
                if (r.EndOfMessage) return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length); // text or binary frames both carry JSON
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        string closeReason = "closed", detail = "";
        var error = false;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var msg = await ReadMessageAsync(ws, ct).ConfigureAwait(false);
                if (msg is null) { closeReason = ws.CloseStatusDescription ?? ws.CloseStatus?.ToString() ?? "closed by server"; error = ws.CloseStatus is not WebSocketCloseStatus.NormalClosure; break; }
                foreach (var e in GeminiLiveCodec.Parse(msg, _model))
                {
                    if (e is LiveAudio a) _receivedAudioBytes += a.Pcm.Length;
                    Received?.Invoke(e);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
        catch (Exception ex) when (ex is WebSocketException or InvalidDataException) { closeReason = "connection lost"; detail = ex.Message; error = true; }
        Log.Info("live", $"Gemini Live connection ended: {closeReason} {detail} (sent {_sentAudioBytes / 32000.0:0.0}s audio, received {_receivedAudioBytes / 48000.0:0.0}s)");
        Received?.Invoke(new LiveClosed(closeReason, error));
    }

    private async Task SendAsync(string json, CancellationToken ct)
    {
        var ws = _ws;
        if (ws is not { State: WebSocketState.Open }) return;
        await _send.WaitAsync(ct).ConfigureAwait(false);
        try { await ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, ct).ConfigureAwait(false); }
        finally { _send.Release(); }
    }

    public Task SendAudioAsync(ReadOnlyMemory<byte> pcm16k, CancellationToken ct)
    {
        _sentAudioBytes += pcm16k.Length;
        return SendAsync(GeminiLiveCodec.Audio(pcm16k.Span), ct);
    }

    public Task SendAudioStreamEndAsync(CancellationToken ct) => SendAsync(GeminiLiveCodec.AudioStreamEnd(), ct);
    public Task SendImageAsync(byte[] image, string mimeType, CancellationToken ct) => SendAsync(GeminiLiveCodec.Image(image, mimeType), ct);
    public Task SendTextAsync(string text, CancellationToken ct) => SendAsync(GeminiLiveCodec.Text(text), ct);

    public Task SendToolResponseAsync(IReadOnlyList<LiveToolResponse> responses, CancellationToken ct)
    {
        Log.Debug("live", $"tool response {string.Join(",", responses.Select(r => r.Name + ":" + r.Response["outcome"]))}");
        return SendAsync(GeminiLiveCodec.ToolResponse(responses), ct);
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        var ws = _ws; _ws = null;
        if (ws is not null)
        {
            try
            {
                if (ws.State == WebSocketState.Open)
                {
                    using var t = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", t.Token).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException) { }
            ws.Dispose();
        }
        try { if (_receive is not null) await _receive.ConfigureAwait(false); } catch (Exception ex) when (ex is OperationCanceledException or WebSocketException) { }
        _send.Dispose();
    }
}
