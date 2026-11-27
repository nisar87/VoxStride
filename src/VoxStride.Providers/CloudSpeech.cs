using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using VoxStride.Contracts;

namespace VoxStride.Providers;

public sealed record TranscriptionResult(bool Ok, string Text, string? Error = null, string? Language = null);

/// <summary>Turns one recorded utterance (WAV) into text. Only called while the user is actively giving an instruction.</summary>
public interface ITranscriber
{
    string Name { get; }
    Task<TranscriptionResult> TranscribeAsync(byte[] wav, string? previousText, CancellationToken ct);
}

/// <summary>Speaks text with a natural cloud voice. Returns WAV bytes.</summary>
public interface ICloudVoice
{
    Task<byte[]?> SynthesizeAsync(string text, CancellationToken ct);
}

internal static class SpeechHttp
{
    public static HttpClient Client(HttpMessageHandler? handler) =>
        new(new LoggingHandler(handler ?? new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5), ConnectTimeout = TimeSpan.FromSeconds(10) }), disposeHandler: handler is null)
        { Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>Approximate duration of a 16-bit mono WAV.</summary>
    public static double Seconds(byte[] wav, int rate = 16000) => Math.Max(0, wav.Length - 44) / (rate * 2.0);

    public const string Vocabulary = "VoxStride, VoxStride execute, Notepad, Notepad++, YouTube, ChatGPT, desktop, Downloads, temp files, C drive";
}

/// <summary>OpenAI /v1/audio/transcriptions (gpt-transcribe) with keyword and language hints for accented / mixed-language speech.</summary>
public sealed class OpenAITranscriber(string apiKey, string model = "gpt-transcribe", Uri? baseUri = null, HttpMessageHandler? handler = null) : ITranscriber
{
    private readonly HttpClient _http = SpeechHttp.Client(handler);
    public string Name => "OpenAI " + model;

    public async Task<TranscriptionResult> TranscribeAsync(byte[] wav, string? previousText, CancellationToken ct)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(wav);
        file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(file, "file", "utterance.wav");
        form.Add(new StringContent(model), "model");
        form.Add(new StringContent("json"), "response_format");
        var prompt = "Voice command to a PC assistant named VoxStride. The speaker may have a South Asian accent and may mix English with Urdu or Hindi. Keep the name VoxStride spelled exactly. Terms: " + SpeechHttp.Vocabulary + ".";
        if (!string.IsNullOrWhiteSpace(previousText)) prompt += " Previous words: " + previousText[^Math.Min(previousText.Length, 300)..];
        form.Add(new StringContent(prompt), "prompt");
        using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri ?? new Uri(OpenAIProvider.DefaultBase), "v1/audio/transcriptions")) { Content = form };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        try
        {
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                Log.Warn("speech", $"{Name} transcription HTTP {(int)resp.StatusCode} body={(body.Length > 600 ? body[..600] : body)}");
                return new TranscriptionResult(false, "", $"{(int)resp.StatusCode}: {ProviderBase.SafeErrorMessage(body)}");
            }
            var n = JsonNode.Parse(body);
            var lang = n?["languages"] is JsonArray l && l.Count > 0 ? l[0]?.ToString() : null;
            var result = (n?["text"]?.GetValue<string>() ?? "").Trim();
            Log.Info("speech", $"{Name} transcribed audio={SpeechHttp.Seconds(wav):0.0}s lang={lang ?? "?"} text={Log.Text(result)}");
            return new TranscriptionResult(true, result, null, lang);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            Log.Warn("speech", $"{Name} transcription network problem", ex);
            return new TranscriptionResult(false, "", "Network problem: " + ex.Message);
        }
    }
}

/// <summary>Gemini generateContent with inline WAV audio, prompted for verbatim transcription.</summary>
public sealed class GeminiTranscriber(string apiKey, string model, Uri? baseUri = null, HttpMessageHandler? handler = null) : ITranscriber
{
    private readonly HttpClient _http = SpeechHttp.Client(handler);
    public string Name => "Gemini " + model;

    public async Task<TranscriptionResult> TranscribeAsync(byte[] wav, string? previousText, CancellationToken ct)
    {
        var instruction = "Transcribe this short voice command exactly as spoken. The speaker may have a South Asian accent and may mix English with Urdu or Hindi; " +
                          "write English words in English and Urdu/Hindi words in Roman Urdu (Latin letters). The assistant's name is VoxStride — always spell it exactly 'VoxStride'. " +
                          "Output ONLY the transcript text, nothing else. If there is no speech, output nothing. Known terms: " + SpeechHttp.Vocabulary + ".";
        if (!string.IsNullOrWhiteSpace(previousText)) instruction += " Context (earlier words): " + previousText[^Math.Min(previousText.Length, 300)..];
        var body = new JsonObject
        {
            ["contents"] = new JsonArray(new JsonObject
            {
                ["role"] = "user",
                ["parts"] = new JsonArray(
                    new JsonObject { ["text"] = instruction },
                    new JsonObject { ["inline_data"] = new JsonObject { ["mime_type"] = "audio/wav", ["data"] = Convert.ToBase64String(wav) } }),
            }),
            ["generationConfig"] = GeminiThinking.Apply(new JsonObject { ["temperature"] = 0, ["maxOutputTokens"] = 512 }, model, fast: true),
        };
        var uri = new Uri(baseUri ?? new Uri(GeminiProvider.DefaultBase), "v1beta/models/" + Uri.EscapeDataString(model) + ":generateContent");
        using var req = new HttpRequestMessage(HttpMethod.Post, uri) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        req.Headers.Add("x-goog-api-key", apiKey);
        try
        {
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                Log.Warn("speech", $"{Name} transcription HTTP {(int)resp.StatusCode} body={(text.Length > 600 ? text[..600] : text)}");
                return new TranscriptionResult(false, "", $"{(int)resp.StatusCode}: {ProviderBase.SafeErrorMessage(text)}");
            }
            var parts = JsonNode.Parse(text)?["candidates"]?[0]?["content"]?["parts"] as JsonArray;
            var sb = new StringBuilder();
            if (parts is not null)
                foreach (var p in parts)
                    if (p?["thought"]?.GetValue<bool>() != true && p?["text"] is JsonValue t) sb.Append(t.GetValue<string>());
            var transcript = sb.ToString().Trim().Trim('"');
            Log.Info("speech", $"{Name} transcribed audio={SpeechHttp.Seconds(wav):0.0}s text={Log.Text(transcript)}");
            return new TranscriptionResult(true, transcript);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            Log.Warn("speech", $"{Name} transcription network problem", ex);
            return new TranscriptionResult(false, "", "Network problem: " + ex.Message);
        }
    }
}

/// <summary>OpenAI /v1/audio/speech (gpt-4o-mini-tts) natural voice, steered to sound warm and conversational.</summary>
public sealed class OpenAIVoice(string apiKey, string voice = "marin", string model = "gpt-4o-mini-tts", Uri? baseUri = null, HttpMessageHandler? handler = null) : ICloudVoice
{
    private readonly HttpClient _http = SpeechHttp.Client(handler);
    public static readonly string[] Voices = ["marin", "cedar", "coral", "sage", "alloy", "ash", "ballad", "echo", "shimmer", "verse"];

    public async Task<byte[]?> SynthesizeAsync(string text, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var body = new JsonObject
        {
            ["model"] = model,
            ["voice"] = voice,
            ["input"] = text.Length > 4000 ? text[..4000] : text,
            ["instructions"] = "Speak like a warm, friendly personal assistant having a relaxed conversation. Natural pace, light and positive tone. If the text is in Urdu or Roman Urdu, pronounce it naturally in Urdu.",
            ["response_format"] = "wav",
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri ?? new Uri(OpenAIProvider.DefaultBase), "v1/audio/speech"))
        { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        try
        {
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                var err = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                Log.Warn("tts", $"OpenAI voice {voice} HTTP {(int)resp.StatusCode} body={(err.Length > 600 ? err[..600] : err)} (falling back to the Windows voice)");
                return null;
            }
            var audio = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            Log.Debug("tts", $"OpenAI voice {voice} synthesized {SpeechHttp.Seconds(audio, 24000):0.0}s for {text.Length} chars");
            return audio;
        }
        catch (TaskCanceledException) when (ct.IsCancellationRequested) { Log.Debug("tts", "cloud voice cancelled (interrupted or slow)"); return null; }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { Log.Warn("tts", "cloud voice network problem (falling back to the Windows voice)", ex); return null; }
    }
}

/// <summary>
/// Gemini text-to-speech (generateContent with AUDIO output) natural voice, so a Gemini key alone gives a human-like
/// voice. Gemini returns raw 16-bit mono PCM at 24 kHz, which is wrapped in a WAV header for playback. If the newest
/// TTS model isn't available to the key, the previous generation is tried once.
/// </summary>
public sealed class GeminiVoice(string apiKey, string voice = GeminiVoice.DefaultVoice, Uri? baseUri = null, HttpMessageHandler? handler = null, IReadOnlyList<string>? models = null) : ICloudVoice
{
    public const string DefaultVoice = "Sulafat";
    /// <summary>Prebuilt Gemini voices (name — character).</summary>
    public static readonly string[] Voices = ["Sulafat", "Achird", "Aoede", "Kore", "Leda", "Puck", "Zephyr", "Charon", "Fenrir", "Orus", "Callirrhoe", "Despina", "Gacrux", "Vindemiatrix", "Sadachbia", "Umbriel"];
    private static readonly string[] DefaultModels = ["gemini-3.1-flash-tts-preview", "gemini-2.5-flash-preview-tts"];
    private readonly HttpClient _http = SpeechHttp.Client(handler);
    private readonly IReadOnlyList<string> _models = models ?? DefaultModels;
    private int _modelIndex;

    public static string NormalizeVoice(string? name) =>
        Voices.FirstOrDefault(v => string.Equals(v, name, StringComparison.OrdinalIgnoreCase)) ?? DefaultVoice;

    public async Task<byte[]?> SynthesizeAsync(string text, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        while (_modelIndex < _models.Count)
        {
            var model = _models[_modelIndex];
            var (audio, retryNextModel) = await TryModelAsync(model, text.Length > 3000 ? text[..3000] : text, ct).ConfigureAwait(false);
            if (audio is not null || !retryNextModel) return audio;
            Log.Warn("tts", $"Gemini voice model {model} unavailable; trying the next one");
            Interlocked.Increment(ref _modelIndex);
        }
        return null;
    }

    private async Task<(byte[]? Audio, bool TryNext)> TryModelAsync(string model, string text, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["contents"] = new JsonArray(new JsonObject
            {
                ["parts"] = new JsonArray(new JsonObject
                {
                    ["text"] = "Say this like a warm, friendly person in a relaxed conversation, at a natural pace. If parts are in Urdu or Roman Urdu, pronounce them naturally in Urdu:\n" + text,
                }),
            }),
            ["generationConfig"] = new JsonObject
            {
                ["responseModalities"] = new JsonArray("AUDIO"),
                ["speechConfig"] = new JsonObject
                {
                    ["voiceConfig"] = new JsonObject { ["prebuiltVoiceConfig"] = new JsonObject { ["voiceName"] = NormalizeVoice(voice) } },
                },
            },
        };
        var uri = new Uri(baseUri ?? new Uri("https://generativelanguage.googleapis.com/"), "v1beta/models/" + Uri.EscapeDataString(model) + ":generateContent");
        using var req = new HttpRequestMessage(HttpMethod.Post, uri) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        req.Headers.Add("x-goog-api-key", apiKey);
        try
        {
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                Log.Warn("tts", $"Gemini voice {model} HTTP {(int)resp.StatusCode} body={(json.Length > 600 ? json[..600] : json)} (falling back to the Windows voice)");
                return (null, resp.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.BadRequest);
            }
            var data = JsonNode.Parse(json)?["candidates"]?[0]?["content"]?["parts"]?.AsArray()
                .Select(p => p?["inlineData"]?["data"]?.GetValue<string>()).FirstOrDefault(d => !string.IsNullOrEmpty(d));
            if (data is null) { Log.Warn("tts", $"Gemini voice {model} returned no audio"); return (null, false); }
            var pcm = Convert.FromBase64String(data);
            var wav = PcmToWav(pcm, 24000);
            Log.Debug("tts", $"Gemini voice {NormalizeVoice(voice)} ({model}) synthesized {SpeechHttp.Seconds(wav, 24000):0.0}s for {text.Length} chars");
            return (wav, false);
        }
        catch (TaskCanceledException) when (ct.IsCancellationRequested) { Log.Debug("tts", "Gemini voice cancelled (interrupted or slow)"); return (null, false); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or FormatException or System.Text.Json.JsonException) { Log.Warn("tts", "Gemini voice failed (falling back to the Windows voice)", ex); return (null, false); }
    }

    /// <summary>Wraps 16-bit mono PCM in a RIFF/WAVE header.</summary>
    public static byte[] PcmToWav(byte[] pcm, int sampleRate)
    {
        using var ms = new MemoryStream(44 + pcm.Length);
        using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8); w.Write(36 + pcm.Length); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(sampleRate);
        w.Write(sampleRate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(pcm.Length); w.Write(pcm);
        w.Flush();
        return ms.ToArray();
    }
}
