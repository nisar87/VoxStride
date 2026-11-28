using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VoxStride.Contracts;
using VoxStride.Core.Live;
using VoxStride.Core.Voice;
using VoxStride.Speech;

namespace VoxStride.Desktop;

/// <summary>A scripted stand-in for Gemini Live: lets CI drive a whole conversation through the real app.</summary>
internal sealed class ScriptedLiveTransport : ILiveTransport
{
    public string Name => "scripted";
    public bool IsOpen { get; private set; }
    public LiveSetup? Setup { get; private set; }
    public List<LiveToolResponse> ToolResponses { get; } = [];
    public int Images;
    public event Action<LiveServerEvent>? Received;
    public Task ConnectAsync(LiveSetup setup, CancellationToken ct) { Setup = setup; IsOpen = true; Received?.Invoke(new LiveSetupComplete("scripted")); return Task.CompletedTask; }
    public Task SendAudioAsync(ReadOnlyMemory<byte> pcm16k, CancellationToken ct) => Task.CompletedTask;
    public Task SendAudioStreamEndAsync(CancellationToken ct) => Task.CompletedTask;
    public Task SendImageAsync(byte[] image, string mimeType, CancellationToken ct) { Interlocked.Increment(ref Images); return Task.CompletedTask; }
    public Task SendTextAsync(string text, CancellationToken ct) => Task.CompletedTask;
    public Task SendToolResponseAsync(IReadOnlyList<LiveToolResponse> responses, CancellationToken ct) { lock (ToolResponses) ToolResponses.AddRange(responses); return Task.CompletedTask; }
    public void Raise(LiveServerEvent e) => Received?.Invoke(e);
    public ValueTask DisposeAsync() { IsOpen = false; return ValueTask.CompletedTask; }
}

internal sealed class SilentMic : IAudioCaptureDevice
{
    public string Label => "self-test (silent)";
    public int SampleRate => 16000;
    public event Action<byte[]>? DataAvailable;
    public event Action<string>? Error;
    public void Start() { _ = DataAvailable; _ = Error; }
    public void Stop() { }
    public void Dispose() { }
}

public partial class App
{
    private sealed class NoClickApprovals : IUserApproval
    {
        public async Task<Approval?> RequestAsync(ApprovalRequest request, CancellationToken ct)
        {
            try { await Task.Delay(Timeout.Infinite, ct); } catch (OperationCanceledException) { }
            return null;
        }
    }

    /// <summary>
    /// CI self-test of the installed companion (no network, no microphone): renders the character in every state to
    /// PNGs, then drives a scripted conversation through the REAL tools, broker, approvals and speaker:
    /// greeting → Excel sheet created → screen looked at → irreversible delete asked and confirmed by voice → an action
    /// without user words refused. Writes companion-selftest.json and companion-states.png.
    /// </summary>
    private async Task<int> RunCompanionSelfTestAsync(string[] args)
    {
        var outDir = args.SkipWhile(a => a != "--out").Skip(1).FirstOrDefault() ?? Path.GetTempPath();
        Directory.CreateDirectory(outDir);
        DiagnosticsLog.Start(includeContent: true);
        Log.Info("app", "companion self-test starting");
        var checks = new List<object>();
        var ok = true;
        void Check(string name, bool pass, string detail) { ok &= pass; checks.Add(new { name, pass, detail }); Log.Info("selftest", $"{(pass ? "PASS" : "FAIL")} {name}: {detail}"); }
        async Task<bool> Until(Func<bool> cond, int ms = 10000) { var sw = Stopwatch.StartNew(); while (!cond() && sw.ElapsedMilliseconds < ms) await Task.Delay(50); return cond(); }

        try
        {
            Settings = new AppSettings { ActiveProvider = null, SpeakResults = false, WakeWordEnabled = false, LiveMode = true, CompanionVisible = true, ScreenCapture = Core.Policy.ScreenCapturePolicy.WhenNeeded };
            Host = new AppHost(Settings, new NoClickApprovals(), enableSpeech: false);
            var window = new CompanionWindow(this);
            window.Show();
            await Task.Delay(500);

            // 1) What it looks like in each state.
            var states = new (string Name, CompanionState State, double Mouth, string? Bubble, bool Ask, bool Muted)[]
            {
                ("listening", CompanionState.Listening, 0, null, false, false),
                ("hearing", CompanionState.Hearing, 0, null, false, false),
                ("thinking", CompanionState.Thinking, 0, null, false, false),
                ("speaking", CompanionState.Speaking, 0.7, "I'm doing great, thanks for asking! I've put the France forecast in an Excel sheet for you.", false, false),
                ("asking", CompanionState.AwaitingYes, 0, "Move 12 files to the Recycle Bin? Say yes or no.", true, false),
                ("muted", CompanionState.Listening, 0, null, false, true),
            };
            var frames = new List<BitmapSource>();
            foreach (var s in states)
            {
                var bmp = window.Snapshot(s.State, s.Mouth, s.Bubble, s.Ask, s.Muted);
                frames.Add(bmp);
                Save(bmp, Path.Combine(outDir, $"companion-{s.Name}.png"));
            }
            window.ClearQuestion();
            var sheet = Combine(frames, states.Select(s => s.Name).ToArray());
            Save(sheet, Path.Combine(outDir, "companion-states.png"));
            Check("rendered-states", frames.All(f => f.PixelWidth > 100 && f.PixelHeight > 200), $"{frames.Count} states, {frames[0].PixelWidth}x{frames[0].PixelHeight}");

            // Every character, plus the user's own picture.
            var looks = new List<BitmapSource>();
            var lookNames = new List<string>();
            foreach (var ch in CompanionCharacters.All)
            {
                Settings.CompanionCharacter = ch.Id; Settings.CompanionImagePath = null;
                window.ApplyLook();
                looks.Add(window.Snapshot(CompanionState.Speaking, 0.45, null, false));
                lookNames.Add(ch.Name + " · " + ch.Voice);
            }
            var picturePath = Path.Combine(outDir, "test-picture.png");
            Save(TestPicture(), picturePath);
            Settings.CompanionImagePath = picturePath;
            window.ApplyLook();
            looks.Add(window.Snapshot(CompanionState.Hearing, 0, null, false));
            lookNames.Add("Your own picture");
            Settings.CompanionImagePath = null; Settings.CompanionCharacter = "nova";
            window.ApplyLook();
            Save(Combine(looks, lookNames.ToArray()), Path.Combine(outDir, "companion-characters.png"));
            Check("characters", looks.Count == CompanionCharacters.All.Count + 1 && CompanionCharacters.All.Select(c => c.Voice).Distinct().Count() == CompanionCharacters.All.Count, $"{CompanionCharacters.All.Count} characters with distinct voices + picture avatar");

            // 2) A scripted conversation through the real app.
            var transport = new ScriptedLiveTransport();
            var hub = new AudioHub(() => new SilentMic());
            ILiveAudioOut speaker;
            try { speaker = new WaveOutStream(); } catch (Exception) { speaker = new NullSpeaker(); }
            await using var companion = new LiveCompanion(() => transport, hub, speaker, Host.Registry, Host.Broker, Host.Authority, Host.LiveApprovals, null, new LiveOptions(), TimeProvider.System);
            Companion = companion;
            window.Attach(companion);
            await companion.StartAsync();
            Check("connected", companion.State == CompanionState.Listening && transport.Setup is not null, $"state={companion.State} tools={transport.Setup?.Tools.Count}");
            Check("persona", transport.Setup?.SystemInstruction.Contains("Do not ask \"shall I proceed?\"", StringComparison.Ordinal) == true, "no-proceed instruction present");
            Check("tools-offered", new[] { "files_create_document", "screen_look", "memory_remember", "apps_launch" }.All(n => transport.Setup!.Tools.Any(t => t.Name == n)), string.Join(",", transport.Setup!.Tools.Select(t => t.Name).Take(60)));

            // greeting with a spoken reply
            transport.Raise(new LiveInputText("Hi, how are you?"));
            transport.Raise(new LiveOutputText("I'm doing great, thanks for asking!"));
            transport.Raise(new LiveAudio(VoiceLike(1.2), 24000));
            Check("speaking-state", await Until(() => companion.State == CompanionState.Speaking, 3000), $"state={companion.State}");
            transport.Raise(new LiveTurnComplete());
            await Task.Delay(300);

            // "check the weather in France and put it in an Excel sheet"
            var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            var name = "VoxStride self-test France weather " + Guid.NewGuid().ToString("N")[..6];
            transport.Raise(new LiveInputText("Check the weather in France and put it in an Excel sheet."));
            transport.Raise(new LiveToolCall([new LiveFunctionCall("t1", "files_create_document", new JsonObject
            {
                ["folder"] = "Documents", ["name"] = name, ["format"] = "xlsx", ["open"] = false, ["sheetName"] = "France weather",
                ["rows"] = new JsonArray(new JsonArray("City", "Max °C", "Min °C", "Sky"), new JsonArray("Paris", "14", "7", "Cloudy"),
                                         new JsonArray("Lyon", "16", "6", "Sunny"), new JsonArray("Marseille", "19", "11", "Sunny")),
            })]));
            await Until(() => { lock (transport.ToolResponses) return transport.ToolResponses.Count >= 1; });
            var r1 = transport.ToolResponses.FirstOrDefault();
            var xlsx = Path.Combine(docs, name + ".xlsx");
            var valid = File.Exists(xlsx) && ValidXlsx(xlsx);
            Check("excel-created-without-proceed", r1?.Response["outcome"]?.GetValue<string>() == "Success" && r1.Response["verified"]?.GetValue<bool>() == true && valid, $"response={r1?.Response.ToJsonString()} file={File.Exists(xlsx)} valid={valid}");
            if (File.Exists(xlsx)) File.Delete(xlsx);
            transport.Raise(new LiveTurnComplete());

            // "what's on my screen?"
            transport.Raise(new LiveInputText("What's on my screen right now?"));
            transport.Raise(new LiveToolCall([new LiveFunctionCall("t2", "screen_look", new JsonObject { ["reason"] = "the user asked" })]));
            await Until(() => { lock (transport.ToolResponses) return transport.ToolResponses.Count >= 2; });
            var r2 = transport.ToolResponses.ElementAtOrDefault(1);
            Check("screen-looked-at", r2?.Response["outcome"]?.GetValue<string>() == "Success" && transport.Images >= 1, $"images={transport.Images} response={r2?.Response["message"]}");
            transport.Raise(new LiveTurnComplete());

            // irreversible: asks, user says "yes"
            var victim = Path.Combine(docs, "VoxStride self-test delete me " + Guid.NewGuid().ToString("N")[..6] + ".txt");
            await File.WriteAllTextAsync(victim, "temporary file created by the VoxStride self-test");
            transport.Raise(new LiveInputText("Delete the self-test file in my Documents."));
            transport.Raise(new LiveToolCall([new LiveFunctionCall("t3", "files_recycle", new JsonObject { ["paths"] = new JsonArray(victim) })]));
            var asked = await Until(() => Host.LiveApprovals.IsWaitingForVoice);
            Check("asks-before-deleting", asked && File.Exists(victim), $"waiting={asked} state={companion.State} stillThere={File.Exists(victim)}");
            if (asked) Save(window.Snapshot(CompanionState.AwaitingYes, 0, null, false), Path.Combine(outDir, "companion-live-asking.png"));
            transport.Raise(new LiveTurnComplete());
            transport.Raise(new LiveInputText("Yes."));
            await Until(() => { lock (transport.ToolResponses) return transport.ToolResponses.Count >= 3; });
            var r3 = transport.ToolResponses.ElementAtOrDefault(2);
            Check("deleted-after-spoken-yes", r3?.Response["outcome"]?.GetValue<string>() == "Success" && !File.Exists(victim), $"response={r3?.Response["message"]} stillThere={File.Exists(victim)}");
            if (File.Exists(victim)) File.Delete(victim);
            transport.Raise(new LiveTurnComplete());

            // the model acting on its own (no user words) may not change anything
            transport.Raise(new LiveToolCall([new LiveFunctionCall("t4", "files_create_document", new JsonObject { ["name"] = "should not exist " + Guid.NewGuid().ToString("N")[..6], ["format"] = "txt", ["text"] = "x", ["open"] = false })]));
            await Until(() => { lock (transport.ToolResponses) return transport.ToolResponses.Count >= 4; });
            var r4 = transport.ToolResponses.ElementAtOrDefault(3);
            Check("no-words-no-changes", r4?.Response["error"]?.GetValue<string>() == "not_requested", $"response={r4?.Response.ToJsonString()}");

            // interruption stops the voice
            transport.Raise(new LiveInputText("Tell me a long story."));
            transport.Raise(new LiveAudio(VoiceLike(3), 24000));
            await Task.Delay(200);
            transport.Raise(new LiveInterrupted());
            Check("barge-in-stops-voice", await Until(() => !speaker.IsPlaying, 1500), $"playing={speaker.IsPlaying} speaker={speaker.GetType().Name}");

            await companion.StopAsync("self-test done");
            Companion = null;
            window.Close();
        }
        catch (Exception ex)
        {
            Log.Error("selftest", "companion self-test crashed", ex);
            Check("no-crash", false, ex.ToString());
        }
        var evidence = new { caseId = "COMPANION-01", status = ok ? "passed" : "failed", checks };
        await File.WriteAllTextAsync(Path.Combine(outDir, "companion-selftest.json"), JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }));
        Log.Info("app", "companion self-test " + (ok ? "passed" : "FAILED"));
        DiagnosticsLog.Flush(TimeSpan.FromSeconds(3));
        if (Host is not null) await Host.DisposeAsync();
        return ok ? 0 : 1;
    }

    private sealed class NullSpeaker : ILiveAudioOut
    {
        public bool IsPlaying => false;
        public double Level => 0;
        public void Enqueue(byte[] pcm, int sampleRate) { }
        public void Clear() { }
        public void Dispose() { }
    }

    /// <summary>A voice-like test signal: a pitch-varying tone with syllable-rate loudness changes.</summary>
    private static byte[] VoiceLike(double seconds)
    {
        var n = (int)(24000 * seconds);
        var b = new byte[n * 2];
        for (var i = 0; i < n; i++)
        {
            var t = i / 24000.0;
            var env = 0.5 + 0.5 * Math.Sin(2 * Math.PI * 4 * t);
            var s = (short)(9000 * env * Math.Sin(2 * Math.PI * (180 + 40 * Math.Sin(2 * Math.PI * 1.5 * t)) * t));
            b[2 * i] = (byte)s; b[2 * i + 1] = (byte)(s >> 8);
        }
        return b;
    }

    /// <summary>A simple original test picture (a round sticker-style face) standing in for the user's own image.</summary>
    private static BitmapSource TestPicture()
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawEllipse(new RadialGradientBrush(Color.FromRgb(0xFF, 0xD5, 0x4F), Color.FromRgb(0xFF, 0x9F, 0x1C)), new Pen(Brushes.White, 10), new Point(200, 200), 180, 180);
            dc.DrawEllipse(Brushes.Black, null, new Point(140, 170), 18, 24);
            dc.DrawEllipse(Brushes.Black, null, new Point(260, 170), 18, 24);
            var smile = new StreamGeometry();
            using (var g = smile.Open()) { g.BeginFigure(new Point(120, 250), false, false); g.QuadraticBezierTo(new Point(200, 320), new Point(280, 250), true, true); }
            dc.DrawGeometry(null, new Pen(Brushes.Black, 12) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, smile);
        }
        var bmp = new RenderTargetBitmap(400, 400, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(dv);
        return bmp;
    }

    private static bool ValidXlsx(string path)
    {
        try
        {
            using var z = ZipFile.OpenRead(path);
            using var r = new StreamReader(z.GetEntry("xl/worksheets/sheet1.xml")!.Open());
            var xml = r.ReadToEnd();
            System.Xml.Linq.XDocument.Parse(xml);
            return xml.Contains("Marseille", StringComparison.Ordinal) && xml.Contains("<v>19</v>", StringComparison.Ordinal);
        }
        catch (Exception) { return false; }
    }

    private static void Save(BitmapSource bmp, string path)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    /// <summary>All states side by side on a neutral desktop-like background, labelled.</summary>
    private static BitmapSource Combine(IReadOnlyList<BitmapSource> frames, string[] labels)
    {
        int w = frames.Max(f => f.PixelWidth), h = frames.Max(f => f.PixelHeight) + 28;
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(0x2F, 0x55, 0x8C), Color.FromRgb(0x6A, 0x8F, 0xC2), 90), null, new Rect(0, 0, w * frames.Count, h));
            for (var i = 0; i < frames.Count; i++)
            {
                dc.DrawImage(frames[i], new Rect(i * w, 28, frames[i].PixelWidth, frames[i].PixelHeight));
                var text = new FormattedText(labels[i], System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI Semibold"), 16, Brushes.White, 1.0);
                dc.DrawText(text, new Point(i * w + 12, 4));
            }
        }
        var bmp = new RenderTargetBitmap(w * frames.Count, h, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(dv);
        return bmp;
    }
}
