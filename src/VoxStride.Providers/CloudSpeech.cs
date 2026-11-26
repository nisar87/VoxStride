using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

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
        new(handler ?? new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5), ConnectTimeout = TimeSpan.FromSeconds(10) }, disposeHandler: handler is null)
        { Timeout = TimeSpan.FromSeconds(30) };

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
            if (!resp.IsSuccessStatusCode) return new TranscriptionResult(false, "", $"{(int)resp.StatusCode}: {ProviderBase.SafeErrorMessage(body)}");
            var n = JsonNode.Parse(body);
            var lang = n?["languages"] is JsonArray l && l.Count > 0 ? l[0]?.ToString() : null;
            return new TranscriptionResult(true, (n?["text"]?.GetValue<string>() ?? "").Trim(), null, lang);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
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
            ["generationConfig"] = new JsonObject { ["temperature"] = 0, ["maxOutputTokens"] = 512 },
        };
        var uri = new Uri(baseUri ?? new Uri(GeminiProvider.DefaultBase), "v1beta/models/" + Uri.EscapeDataString(model) + ":generateContent");
        using var req = new HttpRequestMessage(HttpMethod.Post, uri) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        req.Headers.Add("x-goog-api-key", apiKey);
        try
        {
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return new TranscriptionResult(false, "", $"{(int)resp.StatusCode}: {ProviderBase.SafeErrorMessage(text)}");
            var parts = JsonNode.Parse(text)?["candidates"]?[0]?["content"]?["parts"] as JsonArray;
            var sb = new StringBuilder();
            if (parts is not null)
                foreach (var p in parts)
                    if (p?["thought"]?.GetValue<bool>() != true && p?["text"] is JsonValue t) sb.Append(t.GetValue<string>());
            return new TranscriptionResult(true, sb.ToString().Trim().Trim('"'));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
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
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { return null; }
    }
}
