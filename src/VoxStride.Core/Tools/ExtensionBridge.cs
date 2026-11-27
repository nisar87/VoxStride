using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;
using VoxStride.Contracts;

namespace VoxStride.Core.Tools;

/// <summary>Chrome/Edge native-messaging framing: 4-byte little-endian length, then UTF-8 JSON.</summary>
public static class NativeMessaging
{
    /// <summary>Browsers cap host→extension messages at 1 MB; we cap both directions.</summary>
    public const int MaxMessageBytes = 1024 * 1024;

    public static async Task WriteAsync(Stream s, JsonNode message, CancellationToken ct)
    {
        var body = Encoding.UTF8.GetBytes(message.ToJsonString());
        if (body.Length > MaxMessageBytes) throw new InvalidOperationException("message too large");
        var len = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(len, body.Length);
        await s.WriteAsync(len, ct).ConfigureAwait(false);
        await s.WriteAsync(body, ct).ConfigureAwait(false);
        await s.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Reads one message; null at end of stream. Oversized or invalid frames throw (the connection is then dropped).</summary>
    public static async Task<JsonNode?> ReadAsync(Stream s, CancellationToken ct)
    {
        var len = new byte[4];
        if (!await ReadExactly(s, len, ct).ConfigureAwait(false)) return null;
        var n = BinaryPrimitives.ReadInt32LittleEndian(len);
        if (n <= 0 || n > MaxMessageBytes) throw new InvalidDataException($"invalid message length {n}");
        var body = new byte[n];
        if (!await ReadExactly(s, body, ct).ConfigureAwait(false)) return null;
        return JsonNode.Parse(body);
    }

    private static async Task<bool> ReadExactly(Stream s, byte[] buf, CancellationToken ct)
    {
        var off = 0;
        while (off < buf.Length)
        {
            var r = await s.ReadAsync(buf.AsMemory(off), ct).ConfigureAwait(false);
            if (r == 0) return false;
            off += r;
        }
        return true;
    }

    /// <summary>Converts the shared page script's observe() result into a <see cref="WebObservation"/>.</summary>
    public static WebObservation ParseObservation(JsonNode o, string tabId, string source)
    {
        var els = new List<WebElement>();
        foreach (var e in o["elements"]?.AsArray() ?? [])
        {
            if (e is null) continue;
            string? S(string k) => e[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
            bool B(string k) => e[k] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
            int I(string k) => e[k] is JsonValue v && v.TryGetValue<double>(out var d) ? (int)d : 0;
            var id = S("id");
            if (id is null || !System.Text.RegularExpressions.Regex.IsMatch(id, "^b[0-9]{1,6}$")) continue; // page-controlled data: validate
            els.Add(new WebElement(id, Cap(S("role"), 30), Cap(S("name"), 160), S("value") is { } val ? Cap(val, 300) : null, S("href") is { } h ? Cap(h, 2000) : null,
                B("enabled"), B("isPassword"), B("isSensitive"), B("isSearch"), B("submitsForm"), I("x"), I("y"), I("w"), I("h")));
            if (els.Count >= 400) break;
        }
        var url = o["url"]?.GetValue<string>() ?? "";
        var origin = Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.GetLeftPart(UriPartial.Authority) : url;
        return new WebObservation(tabId, url, origin, Cap(o["title"]?.GetValue<string>(), 300), els, Cap(o["text"]?.GetValue<string>(), 4000),
            o["truncated"]?.GetValue<bool>() ?? false, DateTimeOffset.UtcNow, source);
    }

    private static string Cap(string? s, int n) => s is null ? "" : s.Length <= n ? s : s[..n];
}

/// <summary>
/// The user's own Chrome/Edge, through the VoxStride extension (spec P5b). The extension connects to a native host
/// (VoxStride.exe in relay mode) which forwards messages over a per-user named pipe to this class.
/// <list type="bullet">
/// <item>Only the known VoxStride extension id is accepted (checked from the browser-supplied caller origin) and the
/// protocol version must match.</item>
/// <item>Each request carries a run token; the extension acts only on the active tab of a site the user explicitly
/// allowed, and refuses if the tab's origin changed. Page content is data only and never authorizes anything.</item>
/// <item>No cookies, passwords, storage or other tabs are ever requested.</item>
/// </list>
/// </summary>
public sealed class ExtensionBrowser(IReadOnlyCollection<string> allowedExtensionIds, Func<bool>? enabled = null) : IWebAutomation, IDisposable
{
    public const int ProtocolVersion = 1;
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonNode>> _pending = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _write = new(1, 1);
    private Stream? _conn;
    private int _seq;
    private CancellationTokenSource? _readLoop;

    public string Name => "Your browser (extension)";
    public bool Connected { get; private set; }
    public string? BrowserVersion { get; private set; }
    public bool IsAvailable => Connected && (enabled?.Invoke() ?? true);
    public event Action<bool>? ConnectionChanged;

    /// <summary>Takes over a connection from the native-host relay. Returns false (and closes it) if the caller isn't allowed.</summary>
    public async Task<bool> AttachAsync(Stream connection, CancellationToken ct)
    {
        try
        {
            using var hsTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            hsTimeout.CancelAfter(TimeSpan.FromSeconds(10));
            var relay = await NativeMessaging.ReadAsync(connection, hsTimeout.Token).ConfigureAwait(false);
            var origin = relay?["origin"]?.GetValue<string>() ?? "";
            var id = origin.Replace("chrome-extension://", "", StringComparison.Ordinal).TrimEnd('/');
            if (relay?["type"]?.GetValue<string>() != "relay-hello" || !allowedExtensionIds.Contains(id))
            {
                Log.Warn("http", $"browser bridge: refused caller origin '{origin}'");
                await connection.DisposeAsync().ConfigureAwait(false);
                return false;
            }
            var hello = await NativeMessaging.ReadAsync(connection, hsTimeout.Token).ConfigureAwait(false);
            if (hello?["type"]?.GetValue<string>() != "hello" || hello["version"]?.GetValue<int>() != ProtocolVersion)
            {
                Log.Warn("http", $"browser bridge: protocol mismatch (got {hello?["version"]?.ToJsonString() ?? "none"}, need {ProtocolVersion})");
                await NativeMessaging.WriteAsync(connection, new JsonObject { ["type"] = "error", ["error"] = "version_mismatch", ["need"] = ProtocolVersion }, ct).ConfigureAwait(false);
                await connection.DisposeAsync().ConfigureAwait(false);
                return false;
            }
            BrowserVersion = Cap(hello["browser"]?.GetValue<string>(), 80);
            await NativeMessaging.WriteAsync(connection, new JsonObject { ["type"] = "welcome", ["version"] = ProtocolVersion }, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or InvalidDataException or IOException or System.Text.Json.JsonException)
        {
            Log.Warn("http", "browser bridge: handshake failed: " + ex.Message);
            await connection.DisposeAsync().ConfigureAwait(false);
            return false;
        }
        Detach();
        _conn = connection;
        Connected = true;
        _readLoop = new CancellationTokenSource();
        _ = Task.Run(() => ReadLoopAsync(connection, _readLoop.Token));
        Log.Info("http", $"browser extension connected ({BrowserVersion})");
        ConnectionChanged?.Invoke(true);
        return true;
    }

    private async Task ReadLoopAsync(Stream conn, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var msg = await NativeMessaging.ReadAsync(conn, ct).ConfigureAwait(false);
                if (msg is null) break;
                if (msg["id"]?.GetValue<string>() is { } id && _pending.TryRemove(id, out var tcs)) tcs.TrySetResult(msg);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidDataException or System.Text.Json.JsonException or ObjectDisposedException)
        {
            if (!ct.IsCancellationRequested) Log.Warn("http", "browser bridge: connection error: " + ex.Message);
        }
        if (ReferenceEquals(_conn, conn)) Detach();
    }

    private void Detach()
    {
        var was = Connected;
        Connected = false;
        _readLoop?.Cancel();
        try { _conn?.Dispose(); } catch (Exception) { }
        _conn = null;
        foreach (var p in _pending) if (_pending.TryRemove(p.Key, out var t)) t.TrySetResult(new JsonObject { ["ok"] = false, ["error"] = "disconnected" });
        if (was) { Log.Info("http", "browser extension disconnected"); ConnectionChanged?.Invoke(false); }
    }

    private async Task<JsonNode> RequestAsync(string op, JsonObject args, CancellationToken ct)
    {
        var conn = _conn;
        if (conn is null || !Connected) return new JsonObject { ["ok"] = false, ["error"] = "not_connected" };
        var id = "r" + Interlocked.Increment(ref _seq).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var tcs = new TaskCompletionSource<JsonNode>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        await _write.WaitAsync(ct).ConfigureAwait(false);
        try { await NativeMessaging.WriteAsync(conn, new JsonObject { ["type"] = "request", ["id"] = id, ["op"] = op, ["args"] = args }, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { _pending.TryRemove(id, out _); Detach(); return new JsonObject { ["ok"] = false, ["error"] = "disconnected" }; }
        finally { _write.Release(); }
        var done = await Task.WhenAny(tcs.Task, Task.Delay(RequestTimeout, ct)).ConfigureAwait(false);
        if (done != tcs.Task) { _pending.TryRemove(id, out _); return new JsonObject { ["ok"] = false, ["error"] = "timeout" }; }
        return await tcs.Task.ConfigureAwait(false);
    }

    private static WebActionResult Result(JsonNode r)
    {
        var ok = r["ok"]?.GetValue<bool>() ?? false;
        var err = r["error"]?.GetValue<string>();
        return new WebActionResult(ok, err, Message(err), r["url"]?.GetValue<string>(), r["title"]?.GetValue<string>(), r["value"]?.GetValue<string>(), ok && (r["verified"]?.GetValue<bool>() ?? false));
    }

    private static string? Message(string? error) => error switch
    {
        null => null,
        "not_allowed_on_site" => "VoxStride isn't allowed on this site yet. Click the VoxStride icon in your browser and choose \"Allow on this site\".",
        "origin_changed" => "The tab moved to a different site. Observe again.",
        "element_gone" => "That element is gone (the page changed). Observe again.",
        "no_tab" => "There's no normal web page in the active tab.",
        "not_connected" or "disconnected" => "Your browser's VoxStride extension isn't connected.",
        "timeout" => "Your browser didn't answer in time.",
        "sensitive_field" => "I never fill in passwords or card fields.",
        _ => "The browser extension said: " + error,
    };

    public async Task<WebObservation?> ObserveAsync(string? tabId, int maxElements, CancellationToken ct)
    {
        var r = await RequestAsync("observe", new JsonObject { ["tabId"] = tabId, ["max"] = maxElements }, ct).ConfigureAwait(false);
        if (r["ok"]?.GetValue<bool>() != true || r["page"] is not JsonNode page) { Log.Info("http", "extension observe: " + (r["error"]?.GetValue<string>() ?? "no page")); return null; }
        return NativeMessaging.ParseObservation(page, r["tabId"]?.ToString() ?? "active", Name);
    }

    public async Task<WebActionResult> NavigateAsync(string? tabId, Uri url, CancellationToken ct) =>
        Result(await RequestAsync("navigate", new JsonObject { ["tabId"] = tabId, ["url"] = url.ToString() }, ct).ConfigureAwait(false));

    public async Task<WebActionResult> ClickAsync(WebObservation o, string elementId, CancellationToken ct) =>
        Result(await RequestAsync("click", new JsonObject { ["tabId"] = o.TabId, ["origin"] = o.Origin, ["elementId"] = elementId }, ct).ConfigureAwait(false));

    public async Task<WebActionResult> FillAsync(WebObservation o, string elementId, string text, bool pressEnter, CancellationToken ct) =>
        Result(await RequestAsync("fill", new JsonObject { ["tabId"] = o.TabId, ["origin"] = o.Origin, ["elementId"] = elementId, ["text"] = text, ["enter"] = pressEnter }, ct).ConfigureAwait(false));

    public async Task<WebActionResult> BackAsync(string? tabId, CancellationToken ct) =>
        Result(await RequestAsync("back", new JsonObject { ["tabId"] = tabId }, ct).ConfigureAwait(false));

    private static string Cap(string? s, int n) => s is null ? "" : s.Length <= n ? s : s[..n];

    public void Dispose() => Detach();
}

/// <summary>Per-user named pipe that the native-host relay connects to (other Windows users can't open it).</summary>
public sealed class BrowserBridgeServer(ExtensionBrowser browser, string? pipeName = null) : IDisposable
{
    public string PipeName { get; } = pipeName ?? DefaultPipeName();
    private readonly CancellationTokenSource _stop = new();

    public static string DefaultPipeName() => "VoxStride.Bridge." + new string(Environment.UserName.Where(char.IsLetterOrDigit).Take(40).ToArray());

    public void Start() => _ = Task.Run(LoopAsync);

    private async Task LoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
                Log.Info("http", "browser bridge: native host connected");
                if (await browser.AttachAsync(server, _stop.Token).ConfigureAwait(false))
                    while (browser.Connected && !_stop.IsCancellationRequested) await Task.Delay(500, _stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { server?.Dispose(); break; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn("http", "browser bridge: pipe error: " + ex.Message);
                server?.Dispose();
                try { await Task.Delay(1000, _stop.Token).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
            }
        }
    }

    public void Dispose() { _stop.Cancel(); browser.Dispose(); }
}

/// <summary>
/// Native-host relay (VoxStride.exe started by Chrome/Edge with "chrome-extension://…/"): forwards framed messages between
/// the browser (stdin/stdout) and the running VoxStride over the per-user pipe. Holds no logic and no authority.
/// </summary>
public static class NativeHostRelay
{
    public static async Task<int> RunAsync(string callerOrigin, Stream stdin, Stream stdout, string? pipeName = null, CancellationToken ct = default)
    {
        using var pipe = new NamedPipeClientStream(".", pipeName ?? BrowserBridgeServer.DefaultPipeName(), PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try { await pipe.ConnectAsync(3000, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is TimeoutException or IOException)
        {
            await NativeMessaging.WriteAsync(stdout, new JsonObject { ["type"] = "error", ["error"] = "voxstride_not_running" }, ct).ConfigureAwait(false);
            return 1;
        }
        await NativeMessaging.WriteAsync(pipe, new JsonObject { ["type"] = "relay-hello", ["origin"] = callerOrigin }, ct).ConfigureAwait(false);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var up = Pump(stdin, pipe, stop.Token);
        var down = Pump(pipe, stdout, stop.Token);
        await Task.WhenAny(up, down).ConfigureAwait(false);
        await stop.CancelAsync().ConfigureAwait(false);
        return 0;
    }

    private static async Task Pump(Stream from, Stream to, CancellationToken ct)
    {
        try
        {
            while (await NativeMessaging.ReadAsync(from, ct).ConfigureAwait(false) is { } msg)
                await NativeMessaging.WriteAsync(to, msg, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidDataException or System.Text.Json.JsonException or ObjectDisposedException) { }
    }
}
