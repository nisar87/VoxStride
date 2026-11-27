using System.Diagnostics;
using VoxStride.Contracts;

namespace VoxStride.Providers;

/// <summary>
/// Logs every outbound AI / speech HTTP call (area "http"): method, host and path (query removed, so keys in
/// query strings never appear), status, time to response headers, request size and the provider's request id,
/// which is what provider support asks for. Failures include the exception. Headers and bodies are not logged here;
/// error bodies are logged (redacted) by the caller.
/// </summary>
public sealed class LoggingHandler : DelegatingHandler
{
    public LoggingHandler(HttpMessageHandler inner) : base(inner) { }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var target = Describe(request.RequestUri);
        var size = request.Content?.Headers.ContentLength;
        var sw = Stopwatch.StartNew();
        try
        {
            var response = await base.SendAsync(request, ct).ConfigureAwait(false);
            var id = RequestId(response);
            Log.Write(response.IsSuccessStatusCode ? LogLevel.Info : LogLevel.Warn, "http",
                $"{request.Method} {target} -> {(int)response.StatusCode} {response.ReasonPhrase} in {sw.ElapsedMilliseconds} ms" +
                (size is { } s ? $" sent={s / 1024.0:0.#}KB" : "") + (id is null ? "" : " requestId=" + id), null);
            return response;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Log.Info("http", $"{request.Method} {target} cancelled after {sw.ElapsedMilliseconds} ms");
            throw;
        }
        catch (Exception ex)
        {
            Log.Warn("http", $"{request.Method} {target} failed after {sw.ElapsedMilliseconds} ms: {ex.GetType().Name}: {ex.Message}", ex);
            throw;
        }
    }

    /// <summary>Host + path only: the query string (which may carry an API key) is never logged.</summary>
    public static string Describe(Uri? uri) => uri is null ? "(no uri)" : uri.Host + uri.AbsolutePath;

    private static string? RequestId(HttpResponseMessage r)
    {
        foreach (var h in new[] { "request-id", "x-request-id", "x-goog-request-id", "cf-ray" })
            if (r.Headers.TryGetValues(h, out var v)) return v.FirstOrDefault();
        return null;
    }
}
