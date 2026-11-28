using System.Text.Json.Nodes;
using VoxStride.Contracts;
using VoxStride.Providers;
using Xunit;

namespace VoxStride.Tests;

public sealed class GeminiLiveCodecTests
{
    private static readonly LiveSetup Setup = new("Be friendly.", [Fixtures.FreeSpace], "Sulafat", true, null);

    [Fact]
    public void SetupCarriesModelVoiceToolsTranscriptionAndResumption()
    {
        var j = JsonNode.Parse(GeminiLiveCodec.Setup("gemini-3.8-live", Setup))!["setup"]!;
        Assert.Equal("models/gemini-3.8-live", j["model"]!.GetValue<string>());
        Assert.Equal("AUDIO", j["generationConfig"]!["responseModalities"]![0]!.GetValue<string>());
        Assert.Equal("Sulafat", j["generationConfig"]!["speechConfig"]!["voiceConfig"]!["prebuiltVoiceConfig"]!["voiceName"]!.GetValue<string>());
        Assert.Equal("Be friendly.", j["systemInstruction"]!["parts"]![0]!["text"]!.GetValue<string>());
        var decl = j["tools"]![0]!["functionDeclarations"]![0]!;
        Assert.Equal("system_get_free_space", decl["name"]!.GetValue<string>());
        Assert.NotNull(decl["parametersJsonSchema"]);
        Assert.NotNull(j["tools"]![1]!["googleSearch"]);
        Assert.NotNull(j["inputAudioTranscription"]);
        Assert.NotNull(j["outputAudioTranscription"]);
        Assert.NotNull(j["contextWindowCompression"]!["slidingWindow"]);
        Assert.Null(j["sessionResumption"]!["handle"]);
        var resumed = JsonNode.Parse(GeminiLiveCodec.Setup("m", Setup with { ResumeHandle = "h1", WebSearch = false }))!["setup"]!;
        Assert.Equal("h1", resumed["sessionResumption"]!["handle"]!.GetValue<string>());
        Assert.Single(resumed["tools"]!.AsArray());
    }

    [Fact]
    public void OpenApiFallbackDropsUnsupportedSchemaKeywords()
    {
        var decl = JsonNode.Parse(GeminiLiveCodec.Setup("m", Setup, "openapi"))!["setup"]!["tools"]![0]!["functionDeclarations"]![0]!;
        Assert.Null(decl["parametersJsonSchema"]);
        var p = decl["parameters"]!;
        Assert.Null(p["additionalProperties"]);
        Assert.Equal("object", p["type"]!.GetValue<string>());
        Assert.Equal("string", p["properties"]!["driveLetter"]!["type"]!.GetValue<string>());
    }

    [Fact]
    public void ClientMessages()
    {
        var a = JsonNode.Parse(GeminiLiveCodec.Audio(new byte[] { 1, 2, 3, 4 }))!["realtimeInput"]!["audio"]!;
        Assert.Equal("audio/pcm;rate=16000", a["mimeType"]!.GetValue<string>());
        Assert.Equal(Convert.ToBase64String(new byte[] { 1, 2, 3, 4 }), a["data"]!.GetValue<string>());
        Assert.True(JsonNode.Parse(GeminiLiveCodec.AudioStreamEnd())!["realtimeInput"]!["audioStreamEnd"]!.GetValue<bool>());
        Assert.Equal("image/png", JsonNode.Parse(GeminiLiveCodec.Image([1], "image/png"))!["realtimeInput"]!["video"]!["mimeType"]!.GetValue<string>());
        Assert.Equal("hi \"there\"", JsonNode.Parse(GeminiLiveCodec.Text("hi \"there\""))!["realtimeInput"]!["text"]!.GetValue<string>());
        var tr = JsonNode.Parse(GeminiLiveCodec.ToolResponse([new LiveToolResponse("id1", "test_read", new JsonObject { ["outcome"] = "Success" })]))!["toolResponse"]!["functionResponses"]![0]!;
        Assert.Equal("id1", tr["id"]!.GetValue<string>());
        Assert.Equal("Success", tr["response"]!["outcome"]!.GetValue<string>());
    }

    [Fact]
    public void ParsesAudioTranscriptionsAndTurnEvents()
    {
        var pcm = Convert.ToBase64String(new byte[480]);
        var ev = GeminiLiveCodec.Parse("""{"serverContent":{"inputTranscription":{"text":"hi"},"modelTurn":{"parts":[{"inlineData":{"mimeType":"audio/pcm;rate=24000","data":"PCM"}}]},"outputTranscription":{"text":"hello!"},"turnComplete":true}}""".Replace("PCM", pcm), "m");
        Assert.IsType<LiveInputText>(ev[0]);
        var audio = Assert.IsType<LiveAudio>(ev[1]);
        Assert.Equal(480, audio.Pcm.Length);
        Assert.Equal(24000, audio.SampleRate);
        Assert.Equal("hello!", Assert.IsType<LiveOutputText>(ev[2]).Text);
        Assert.IsType<LiveTurnComplete>(ev[3]);
        Assert.IsType<LiveInterrupted>(Assert.Single(GeminiLiveCodec.Parse("""{"serverContent":{"interrupted":true}}""", "m")));
        Assert.IsType<LiveSetupComplete>(Assert.Single(GeminiLiveCodec.Parse("""{"setupComplete":{}}""", "m")));
    }

    [Fact]
    public void ParsesToolCallsCancellationGoAwayAndResumption()
    {
        var call = Assert.IsType<LiveToolCall>(Assert.Single(GeminiLiveCodec.Parse("""{"toolCall":{"functionCalls":[{"id":"fc1","name":"files_create_document","args":{"name":"France weather","format":"xlsx"}}]}}""", "m")));
        Assert.Equal("fc1", call.Calls[0].Id);
        Assert.Equal("xlsx", call.Calls[0].Args["format"]!.GetValue<string>());
        Assert.Equal("fc1", Assert.IsType<LiveToolCancel>(Assert.Single(GeminiLiveCodec.Parse("""{"toolCallCancellation":{"ids":["fc1"]}}""", "m"))).Ids[0]);
        Assert.Equal(TimeSpan.FromSeconds(12.5), Assert.IsType<LiveGoAway>(Assert.Single(GeminiLiveCodec.Parse("""{"goAway":{"timeLeft":"12.5s"}}""", "m"))).TimeLeft);
        Assert.Equal("hh", Assert.IsType<LiveResumeHandle>(Assert.Single(GeminiLiveCodec.Parse("""{"sessionResumptionUpdate":{"newHandle":"hh","resumable":true}}""", "m"))).Handle);
        Assert.Empty(GeminiLiveCodec.Parse("""{"sessionResumptionUpdate":{"resumable":false}}""", "m"));
        Assert.Empty(GeminiLiveCodec.Parse("not json", "m"));
    }

    [Theory]
    [InlineData("audio/pcm;rate=24000", 24000)]
    [InlineData("audio/pcm;rate=16000", 16000)]
    [InlineData("audio/pcm", 24000)]
    public void SampleRateFromMime(string mime, int rate) => Assert.Equal(rate, GeminiLiveCodec.RateOf(mime));
}

public sealed class GeminiLiveUsageTests
{
    [Fact]
    public void UsageMetadataIsReported()
    {
        var u = Assert.IsType<LiveUsage>(Assert.Single(VoxStride.Providers.GeminiLiveCodec.Parse("""{"usageMetadata":{"promptTokenCount":120,"responseTokenCount":45,"totalTokenCount":165}}""", "m")));
        Assert.Equal(120, u.InputTokens);
        Assert.Equal(45, u.OutputTokens);
        var t = Assert.IsType<LiveUsage>(Assert.Single(VoxStride.Providers.GeminiLiveCodec.Parse("""{"usageMetadata":{"promptTokenCount":100,"totalTokenCount":130}}""", "m")));
        Assert.Equal(30, t.OutputTokens);
    }

    [Fact]
    public void AllThirtyVoicesHaveAStyle()
    {
        Assert.Equal(30, VoxStride.Providers.GeminiVoice.Catalogue.Count);
        Assert.Equal("Sulafat — warm", VoxStride.Providers.GeminiVoice.Describe("Sulafat"));
        Assert.Equal("marin", VoxStride.Providers.GeminiVoice.Describe("marin"));
    }
}
