using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using VoxStride.Contracts;

namespace VoxStride.Providers;

public sealed record SseMessage(string? Event, string Data);

/// <summary>Incremental, UTF-8 safe Server-Sent Events reader.</summary>
public static class SseReader
{
    public static async IAsyncEnumerable<SseMessage> ReadAsync(Stream stream, TimeSpan idleTimeout, [EnumeratorCancellation] CancellationToken ct)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 8192);
        string? ev = null;
        var data = new StringBuilder();
        while (true)
        {
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
            idle.CancelAfter(idleTimeout);
            string? line;
            try { line = await reader.ReadLineAsync(idle.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new TimeoutException("Stream idle timeout."); }
            if (line is null)
            {
                if (data.Length > 0) yield return new SseMessage(ev, data.ToString());
                yield break;
            }
            if (line.Length == 0)
            {
                if (data.Length > 0) yield return new SseMessage(ev, data.ToString());
                ev = null; data.Clear();
                continue;
            }
            if (line.StartsWith(':')) continue;
            var idx = line.IndexOf(':', StringComparison.Ordinal);
            var field = idx < 0 ? line : line[..idx];
            var value = idx < 0 ? "" : line[(idx + 1)..].TrimStart(' ');
            if (field == "event") ev = value;
            else if (field == "data") { if (data.Length > 0) data.Append('\n'); data.Append(value); }
        }
    }
}

/// <summary>Shared HTTP plumbing: one reusable HttpClient per provider, redacted errors, status → error kind mapping.</summary>
public abstract class ProviderBase : IReasoningProvider
{
    protected ProviderBase(ProviderSettings settings, HttpMessageHandler? handler)
    {
        if (string.IsNullOrWhiteSpace(settings.ApiKey)) throw new ArgumentException("API key is required.", nameof(settings));
        if (string.IsNullOrWhiteSpace(settings.Model)) throw new ArgumentException("Model is required.", nameof(settings));
        Settings = settings;
        Http = new HttpClient(new LoggingHandler(handler ?? new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5), AutomaticDecompression = DecompressionMethods.All, ConnectTimeout = TimeSpan.FromSeconds(10) }), disposeHandler: handler is null)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        Http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("VoxStride", "0.1"));
    }

    protected ProviderSettings Settings { get; }
    protected HttpClient Http { get; }
    protected TimeSpan FirstByteTimeout => Settings.RequestTimeout ?? TimeSpan.FromSeconds(45);
    protected static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(60);

    public abstract ProviderKind Kind { get; }
    public string Model => Settings.Model;
    public virtual ProviderCapabilities Capabilities => new(true, true, true, 64);
    public abstract IProviderConversation StartConversation(string systemInstruction, IReadOnlyList<ToolDefinition> tools);
    public abstract Task<ProviderCheckResult> CheckAsync(CancellationToken ct);

    protected Uri BaseUri(string defaultBase) => Settings.BaseUri ?? new Uri(defaultBase);

    internal static ProviderErrorKind MapStatus(HttpStatusCode code) => (int)code switch
    {
        401 or 403 => ProviderErrorKind.Unauthorized,
        404 => ProviderErrorKind.UnsupportedModel,
        408 => ProviderErrorKind.Timeout,
        429 => ProviderErrorKind.RateLimited,
        400 or 413 or 422 => ProviderErrorKind.InvalidRequest,
        >= 500 => ProviderErrorKind.Server,
        _ => ProviderErrorKind.Unknown,
    };

    /// <summary>Extracts a short provider error message without echoing request content or headers.</summary>
    internal static string SafeErrorMessage(string body)
    {
        try
        {
            var n = JsonNode.Parse(body);
            var msg = n?["error"]?["message"]?.GetValue<string>() ?? (n?["error"] is JsonValue v ? v.ToString() : null) ?? n?["message"]?.GetValue<string>();
            if (msg is not null) return msg.Length > 300 ? msg[..300] : msg;
        }
        catch (Exception) { }
        return body.Length > 200 ? body[..200] : body;
    }

    /// <summary>Sends a streaming request. Returns either an open response stream or a provider error.</summary>
    protected async Task<(HttpResponseMessage? response, ProviderErrorEvent? error)> SendStreamingAsync(HttpRequestMessage req, CancellationToken ct)
    {
        using var firstByte = CancellationTokenSource.CreateLinkedTokenSource(ct);
        firstByte.CancelAfter(FirstByteTimeout);
        HttpResponseMessage resp;
        try { resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, firstByte.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Log.Warn("ai", $"{Kind} no response within {FirstByteTimeout.TotalSeconds:0}s ({LoggingHandler.Describe(req.RequestUri)})");
            return (null, new ProviderErrorEvent(ProviderErrorKind.Timeout, "No response before timeout."));
        }
        catch (HttpRequestException ex)
        {
            Log.Warn("ai", $"{Kind} network error: {ex.Message}", ex);
            return (null, new ProviderErrorEvent(ProviderErrorKind.Disconnected, ex.Message));
        }
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            Log.Warn("ai", $"{Kind} model={Model} HTTP {(int)resp.StatusCode} body={(body.Length > 600 ? body[..600] + "…" : body)}");
            var kind = MapStatus(resp.StatusCode);
            var status = (int)resp.StatusCode;
            resp.Dispose();
            return (null, new ProviderErrorEvent(kind, SafeErrorMessage(body), status));
        }
        return (resp, null);
    }

    protected async Task<ProviderCheckResult> CheckGetAsync(HttpRequestMessage req, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(15));
            using var resp = await Http.SendAsync(req, cts.Token).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode) { Log.Info("ai", $"{Kind} key check ok model={Model}"); return new ProviderCheckResult(true, $"Connected. Model {Model} is available."); }
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            Log.Warn("ai", $"{Kind} key check failed model={Model} HTTP {(int)resp.StatusCode} body={(body.Length > 600 ? body[..600] + "…" : body)}");
            var kind = MapStatus(resp.StatusCode);
            return new ProviderCheckResult(false, kind switch
            {
                ProviderErrorKind.Unauthorized => "The API key was rejected.",
                ProviderErrorKind.UnsupportedModel => $"Model '{Model}' was not found for this key.",
                _ => $"Check failed ({(int)resp.StatusCode}): {SafeErrorMessage(body)}",
            }, kind);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { Log.Warn("ai", $"{Kind} key check timed out"); return new ProviderCheckResult(false, "Timed out.", ProviderErrorKind.Timeout); }
        catch (HttpRequestException ex) { Log.Warn("ai", $"{Kind} key check network error", ex); return new ProviderCheckResult(false, "Network error: " + ex.Message, ProviderErrorKind.Disconnected); }
    }

    protected static StringContent Json(JsonNode body) => new(body.ToJsonString(), Encoding.UTF8, "application/json");
}
