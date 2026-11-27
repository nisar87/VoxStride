using System.IO.Pipes;
using System.Text.Json.Nodes;
using VoxStride.Core.Tools;
using Xunit;

namespace VoxStride.Tests;

public sealed class ExtensionBridgeTests
{
    private const string GoodId = "ljfgkidknpjooifilobgenemahnnefop";

    [Fact]
    public async Task FramingRoundTripsAndRejectsOversizedFrames()
    {
        var ms = new MemoryStream();
        await NativeMessaging.WriteAsync(ms, new JsonObject { ["a"] = "ü" }, CancellationToken.None);
        ms.Position = 0;
        Assert.Equal("ü", (await NativeMessaging.ReadAsync(ms, CancellationToken.None))!["a"]!.GetValue<string>());
        Assert.Null(await NativeMessaging.ReadAsync(ms, CancellationToken.None)); // end of stream
        var bad = new MemoryStream([0xFF, 0xFF, 0xFF, 0x7F]);
        await Assert.ThrowsAsync<InvalidDataException>(() => NativeMessaging.ReadAsync(bad, CancellationToken.None));
    }

    [Fact]
    public void ParsedObservationsAreValidatedAndCapped()
    {
        var page = JsonNode.Parse("""
            {"url":"https://example.com/a","title":"T","text":"hi","truncated":false,"elements":[
              {"id":"b1","role":"button","name":"OK","enabled":true},
              {"id":"<script>","role":"button","name":"bad id"},
              {"id":"b2","role":"textbox","name":"Card","isSensitive":true,"value":"4111"}]}
            """)!;
        var o = NativeMessaging.ParseObservation(page, "7", "ext");
        Assert.Equal(2, o.Elements.Count); // the element with a page-controlled bogus id is dropped
        Assert.Equal("https://example.com", o.Origin);
        Assert.True(o.Elements[1].IsSensitive);
    }

    /// <summary>A scripted stand-in for the browser extension talking through the real relay and named pipe.</summary>
    private sealed class FakeExtension : IAsyncDisposable
    {
        private readonly AnonymousPipeServerStream _toRelay = new(PipeDirection.Out);
        private readonly AnonymousPipeServerStream _fromRelay = new(PipeDirection.In);
        public Stream RelayStdin { get; }
        public Stream RelayStdout { get; }
        public FakeExtension()
        {
            RelayStdin = new AnonymousPipeClientStream(PipeDirection.In, _toRelay.ClientSafePipeHandle);
            RelayStdout = new AnonymousPipeClientStream(PipeDirection.Out, _fromRelay.ClientSafePipeHandle);
        }
        public Task Send(JsonNode m) => NativeMessaging.WriteAsync(_toRelay, m, CancellationToken.None);
        public Task<JsonNode?> Receive() => NativeMessaging.ReadAsync(_fromRelay, CancellationToken.None);
        public async ValueTask DisposeAsync() { await _toRelay.DisposeAsync(); await _fromRelay.DisposeAsync(); }
    }

    private static async Task<(ExtensionBrowser Browser, BrowserBridgeServer Server, FakeExtension Ext, Task<int> Relay)> Connect(string origin, int version = 1)
    {
        var pipe = "VoxStride.Test." + Guid.NewGuid().ToString("N")[..10];
        var browser = new ExtensionBrowser([GoodId]);
        var server = new BrowserBridgeServer(browser, pipe);
        server.Start();
        await Task.Delay(100);
        var ext = new FakeExtension();
        var relay = Task.Run(() => NativeHostRelay.RunAsync(origin, ext.RelayStdin, ext.RelayStdout, pipe));
        await ext.Send(new JsonObject { ["type"] = "hello", ["version"] = version, ["browser"] = "Edge 140" });
        return (browser, server, ext, relay);
    }

    [Fact]
    public async Task KnownExtensionConnectsAndAnswersRequests()
    {
        var (browser, server, ext, _) = await Connect($"chrome-extension://{GoodId}/");
        await using var _ext = ext;
        using var _s = server;
        Assert.Equal("welcome", (await ext.Receive())!["type"]!.GetValue<string>());
        for (var i = 0; i < 50 && !browser.Connected; i++) await Task.Delay(20);
        Assert.True(browser.IsAvailable);

        var observe = browser.ObserveAsync(null, 50, CancellationToken.None);
        var req = await ext.Receive();
        Assert.Equal("observe", req!["op"]!.GetValue<string>());
        await ext.Send(new JsonObject
        {
            ["id"] = req["id"]!.GetValue<string>(), ["ok"] = true, ["tabId"] = "42",
            ["page"] = JsonNode.Parse("""{"url":"https://mail.example.com/","title":"Inbox","text":"","truncated":false,"elements":[{"id":"b1","role":"button","name":"Compose","enabled":true}]}"""),
        });
        var o = await observe;
        Assert.NotNull(o);
        Assert.Equal("42", o.TabId);
        Assert.Equal("Compose", Assert.Single(o.Elements).Name);

        var click = browser.ClickAsync(o, "b1", CancellationToken.None);
        var creq = await ext.Receive();
        Assert.Equal("https://mail.example.com", creq!["args"]!["origin"]!.GetValue<string>()); // origin-bound request
        await ext.Send(new JsonObject { ["id"] = creq["id"]!.GetValue<string>(), ["ok"] = false, ["error"] = "not_allowed_on_site" });
        var cr = await click;
        Assert.False(cr.Ok);
        Assert.Contains("Allow on this site", cr.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownExtensionIsRefused()
    {
        var (browser, server, ext, relay) = await Connect("chrome-extension://aaaabbbbccccddddeeeeffffgggghhhh/");
        await using var _ext = ext;
        using var _s = server;
        await Task.WhenAny(relay, Task.Delay(3000));
        Assert.False(browser.Connected);
    }

    [Fact]
    public async Task ProtocolVersionMismatchIsRefused()
    {
        var (browser, server, ext, _) = await Connect($"chrome-extension://{GoodId}/", version: 99);
        await using var _ext = ext;
        using var _s = server;
        var reply = await ext.Receive();
        Assert.Equal("version_mismatch", reply!["error"]!.GetValue<string>());
        Assert.False(browser.Connected);
    }

    [Fact]
    public async Task RelayReportsWhenVoxStrideIsNotRunning()
    {
        await using var ext = new FakeExtension();
        var code = await NativeHostRelay.RunAsync($"chrome-extension://{GoodId}/", ext.RelayStdin, ext.RelayStdout, "VoxStride.Test.nobody-" + Guid.NewGuid().ToString("N")[..6]);
        Assert.Equal(1, code);
        Assert.Equal("voxstride_not_running", (await ext.Receive())!["error"]!.GetValue<string>());
    }

    [Fact]
    public void ExtensionAndAppShareTheSamePageScript()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "VoxStride.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var app = File.ReadAllText(Path.Combine(dir.FullName, "src", "VoxStride.Browser", "Scripts", "voxstride-page.js"));
        var ext = File.ReadAllText(Path.Combine(dir.FullName, "extension", "voxstride-page.js"));
        Assert.Equal(app, ext);
    }
}
