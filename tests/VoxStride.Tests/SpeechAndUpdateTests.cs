using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using VoxStride.Core.Updates;
using VoxStride.Core.Voice;
using VoxStride.Providers;
using Xunit;

namespace VoxStride.Tests;

public sealed class SegmenterTests
{
    private static byte[] Tone(int ms, short amplitude, int rate = 16000)
    {
        var n = rate * ms / 1000;
        var b = new byte[n * 2];
        for (var i = 0; i < n; i++)
        {
            var s = (short)(Math.Sin(2 * Math.PI * 220 * i / rate) * amplitude);
            b[2 * i] = (byte)(s & 0xFF); b[2 * i + 1] = (byte)((s >> 8) & 0xFF);
        }
        return b;
    }

    [Fact]
    public void SpeechSeparatedByPausesBecomesSeparateSegments()
    {
        var seg = new UtteranceSegmenter();
        var segments = new List<AudioSegment>();
        var starts = 0;
        seg.SpeechStarted += _ => starts++;
        seg.SegmentReady += segments.Add;
        seg.Push(Tone(500, 30));      // quiet room
        seg.Push(Tone(900, 9000));    // speech
        seg.Push(Tone(1200, 30));     // pause > 750 ms
        seg.Push(Tone(700, 9000));    // speech
        seg.Push(Tone(1000, 30));
        Assert.Equal(2, segments.Count);
        Assert.Equal(2, starts);
        Assert.True(segments[0].StartMs < segments[1].StartMs);
        Assert.InRange(segments[0].StartMs, 150, 550); // includes pre-roll before speech onset at 500 ms
    }

    [Fact]
    public void ShortClickIsDiscardedNotSent()
    {
        var seg = new UtteranceSegmenter();
        var got = 0; var discarded = 0;
        seg.SegmentReady += _ => got++;
        seg.SpeechDiscarded += () => discarded++;
        seg.Push(Tone(400, 30));
        seg.Push(Tone(60, 12000));
        seg.Push(Tone(1200, 30));
        Assert.Equal(0, got);
        Assert.Equal(1, discarded);
    }

    [Fact]
    public void FlushEmitsSpeechInProgress()
    {
        var seg = new UtteranceSegmenter();
        var got = new List<AudioSegment>();
        seg.SegmentReady += got.Add;
        seg.Push(Tone(300, 30));
        seg.Push(Tone(600, 9000));
        seg.Flush();
        Assert.Single(got);
    }

    [Fact]
    public void LongSpeechIsSplitAtMaxSegment()
    {
        var seg = new UtteranceSegmenter { MaxSegment = TimeSpan.FromSeconds(2) };
        var got = 0;
        seg.SegmentReady += _ => got++;
        seg.Push(Tone(300, 30));
        seg.Push(Tone(5000, 9000));
        Assert.True(got >= 2);
    }

    [Fact]
    public void WavHeaderIsValid()
    {
        var wav = Wav.FromPcm16(new byte[3200], 16000);
        Assert.Equal("RIFF", Encoding.ASCII.GetString(wav, 0, 4));
        Assert.Equal("WAVE", Encoding.ASCII.GetString(wav, 8, 4));
        Assert.Equal(16000, BitConverter.ToInt32(wav, 24));
        Assert.Equal(3200, BitConverter.ToInt32(wav, 40));
        Assert.Equal(44 + 3200, wav.Length);
    }
}

public sealed class AccentTolerantFinishTests
{
    private readonly ControlPhraseDetector _d = new(new ControlPhraseOptions());

    [Theory]
    [InlineData("Box stride execute")]
    [InlineData("Voxtride exicute")]
    [InlineData("vox stride, executed")]
    [InlineData("VoxStride chalo")]
    [InlineData("VoxStride kar do")]
    [InlineData("Vokstride proceed")]
    public void AccentedFinishIsRecognized(string s) =>
        Assert.True(_d.Classify(s).Kind is ControlKind.Finish or ControlKind.FinishUncertain, s);

    [Fact]
    public void ContentBeforeAccentedFinishIsKept()
    {
        var r = _d.Classify("mera desktop organize karo, VoxStride chalo");
        Assert.Equal(ControlKind.Finish, r.Kind);
        Assert.Equal("mera desktop organize karo", r.Content);
    }

    [Theory]
    [InlineData("execute the plan")]
    [InlineData("please go")]
    [InlineData("fox tribe go")]
    [InlineData("chalo")]
    [InlineData("write that box stride is a brand")]
    public void WithoutTheNameNothingFinishes(string s) => Assert.NotEqual(ControlKind.Finish, _d.Classify(s).Kind);

    [Fact]
    public void RequestedAsContentStaysUncertain() =>
        Assert.Equal(ControlKind.FinishUncertain, _d.Classify("type the words box stride execute").Kind);
}

public sealed class UpdateLogicTests
{
    private static JsonNode Release(string tag, bool withSums = true, bool prerelease = false, string host = "github.com") => JsonNode.Parse($$"""
        { "tag_name": "{{tag}}", "draft": false, "prerelease": {{(prerelease ? "true" : "false")}}, "body": "Notes", "published_at": "2026-11-26T10:00:00Z",
          "assets": [
            { "name": "VoxStride-Setup-{{tag.TrimStart('v')}}-x64.exe", "size": 1000, "browser_download_url": "https://{{host}}/nisar87/VoxStride/releases/download/{{tag}}/VoxStride-Setup-{{tag.TrimStart('v')}}-x64.exe" }
            {{(withSums ? $", {{ \"name\": \"SHA256SUMS.txt\", \"size\": 10, \"browser_download_url\": \"https://github.com/nisar87/VoxStride/releases/download/{tag}/SHA256SUMS.txt\" }}" : "")}}
          ] }
        """)!;

    [Fact]
    public void NewerReleaseIsOffered()
    {
        var u = UpdateLogic.Evaluate(Release("v0.3.0"), new Version(0, 2, 0));
        Assert.NotNull(u);
        Assert.Equal(new Version(0, 3, 0, 0), u!.Version);
        Assert.NotNull(u.ChecksumsUrl);
    }

    [Theory]
    [InlineData("v0.2.0")]
    [InlineData("v0.1.9")]
    public void SameOrOlderIsIgnored(string tag) => Assert.Null(UpdateLogic.Evaluate(Release(tag), new Version(0, 2, 0, 0)));

    [Fact]
    public void PrereleaseAndForeignHostsAreIgnored()
    {
        Assert.Null(UpdateLogic.Evaluate(Release("v9.0.0", prerelease: true), new Version(0, 2, 0)));
        Assert.Null(UpdateLogic.Evaluate(Release("v9.0.0", host: "evil.example.com"), new Version(0, 2, 0)));
    }

    [Fact]
    public void ChecksumLookupMatchesFileName()
    {
        var sums = "aaaa  other.exe\n" + new string('b', 64) + "  VoxStride-Setup-0.3.0-x64.exe\n";
        Assert.Equal(new string('B', 64), UpdateLogic.ExpectedHash(sums, "VoxStride-Setup-0.3.0-x64.exe"));
        Assert.Null(UpdateLogic.ExpectedHash(sums, "missing.exe"));
    }

    [Theory]
    [InlineData("v1.2.3", "1.2.3.0")]
    [InlineData("1.2", "1.2.0.0")]
    public void TagsParse(string tag, string expected) => Assert.Equal(Version.Parse(expected), UpdateLogic.ParseTag(tag));

    [Fact]
    public void GarbageTagIsRejected() => Assert.Null(UpdateLogic.ParseTag("latest-build"));
}

public sealed class CloudSpeechContractTests
{
    [Fact]
    public async Task OpenAITranscriberSendsMultipartWithModelAndVocabulary()
    {
        var h = new FixtureHandler(FixtureHandler.Status(HttpStatusCode.OK, "{\"text\":\" Mera desktop organize karo \",\"languages\":[\"ur\"]}"));
        var t = new OpenAITranscriber("test-key", handler: h);
        var r = await t.TranscribeAsync(Wav.FromPcm16(new byte[320], 16000), "earlier words", CancellationToken.None);
        Assert.True(r.Ok);
        Assert.Equal("Mera desktop organize karo", r.Text);
        Assert.EndsWith("/v1/audio/transcriptions", h.Requests[0].Req.RequestUri!.AbsolutePath, StringComparison.Ordinal);
        Assert.Contains("gpt-transcribe", h.Requests[0].Body);
        Assert.Contains("VoxStride", h.Requests[0].Body);
        Assert.Equal("Bearer", h.Requests[0].Req.Headers.Authorization!.Scheme);
    }

    [Fact]
    public async Task GeminiTranscriberSendsInlineAudioAndReadsText()
    {
        var h = new FixtureHandler(FixtureHandler.Status(HttpStatusCode.OK, "{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"check C drive, VoxStride execute\"}]}}]}"));
        var t = new GeminiTranscriber("test-key", "gemini-3.8-flash", handler: h);
        var r = await t.TranscribeAsync(Wav.FromPcm16(new byte[320], 16000), null, CancellationToken.None);
        Assert.Equal("check C drive, VoxStride execute", r.Text);
        Assert.Contains("inline_data", h.Requests[0].Body);
        Assert.Contains("audio/wav", h.Requests[0].Body);
        Assert.Contains(":generateContent", h.Requests[0].Req.RequestUri!.ToString());
    }

    [Fact]
    public async Task TranscriberErrorsAreReportedNotThrown()
    {
        var h = new FixtureHandler(FixtureHandler.Status(HttpStatusCode.Unauthorized, "{\"error\":{\"message\":\"bad key\"}}"));
        var r = await new OpenAITranscriber("k", handler: h).TranscribeAsync([1, 2], null, CancellationToken.None);
        Assert.False(r.Ok);
        Assert.Contains("401", r.Error);
    }

    [Fact]
    public async Task OpenAIVoiceRequestsWavWithWarmInstructions()
    {
        var h = new FixtureHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Wav.FromPcm16(new byte[480], 24000)) });
        var wav = await new OpenAIVoice("k", "marin", handler: h).SynthesizeAsync("Hello there", CancellationToken.None);
        Assert.NotNull(wav);
        var body = JsonNode.Parse(h.Requests[0].Body)!;
        Assert.Equal("wav", body["response_format"]!.GetValue<string>());
        Assert.Equal("marin", body["voice"]!.GetValue<string>());
        Assert.Contains("warm", body["instructions"]!.GetValue<string>());
    }
}

public sealed class GeminiVoiceTests
{
    private sealed class Handler(Func<HttpRequestMessage, string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(string Url, string Body)> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.RequestUri!.ToString(), body));
            return respond(request, body);
        }
    }

    private static HttpResponseMessage Audio(byte[] pcm) => new(HttpStatusCode.OK)
    {
        Content = new StringContent("{\"candidates\":[{\"content\":{\"parts\":[{\"inlineData\":{\"mimeType\":\"audio/L16;codec=pcm;rate=24000\",\"data\":\"" + Convert.ToBase64String(pcm) + "\"}}]}}]}"),
    };

    [Fact]
    public async Task RequestsAudioWithTheChosenVoiceAndReturnsPlayableWav()
    {
        var h = new Handler((_, _) => Audio(new byte[48000]));
        var v = new GeminiVoice("test-key", "Achird", handler: h);
        var wav = await v.SynthesizeAsync("I'm doing great, thanks for asking!", CancellationToken.None);
        Assert.NotNull(wav);
        Assert.Equal(44 + 48000, wav!.Length);
        Assert.Equal("RIFF", Encoding.ASCII.GetString(wav, 0, 4));
        Assert.Equal(24000, BitConverter.ToInt32(wav, 24));
        var (url, body) = h.Requests.Single();
        Assert.Contains("gemini-3.1-flash-tts-preview:generateContent", url);
        var json = JsonNode.Parse(body)!;
        Assert.Equal("AUDIO", json["generationConfig"]!["responseModalities"]![0]!.GetValue<string>());
        Assert.Equal("Achird", json["generationConfig"]!["speechConfig"]!["voiceConfig"]!["prebuiltVoiceConfig"]!["voiceName"]!.GetValue<string>());
        Assert.Contains("thanks for asking", json["contents"]![0]!["parts"]![0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task UnknownModelFallsBackToThePreviousGeneration()
    {
        var h = new Handler((r, _) => r.RequestUri!.ToString().Contains("3.1") ? new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") } : Audio(new byte[100]));
        var wav = await new GeminiVoice("k", handler: h).SynthesizeAsync("hello", CancellationToken.None);
        Assert.NotNull(wav);
        Assert.Equal(2, h.Requests.Count);
        Assert.Contains("gemini-2.5-flash-preview-tts", h.Requests[1].Url);
    }

    [Fact]
    public async Task FailureReturnsNullSoTheWindowsVoiceIsUsed()
    {
        var h = new Handler((_, _) => new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("{}") });
        Assert.Null(await new GeminiVoice("k", handler: h).SynthesizeAsync("hello", CancellationToken.None));
    }

    [Fact]
    public void OpenAIVoiceNamesMapToADefaultGeminiVoice()
    {
        Assert.Equal(GeminiVoice.DefaultVoice, GeminiVoice.NormalizeVoice("marin"));
        Assert.Equal("Kore", GeminiVoice.NormalizeVoice("kore"));
    }
}
