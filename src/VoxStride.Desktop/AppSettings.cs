using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using VoxStride.Contracts;
using VoxStride.Providers;

namespace VoxStride.Desktop;

/// <summary>Speech engine choice. <c>WindowsOnline</c> is kept only so older settings files still load; it means Auto
/// (Windows online dictation requires an MSIX-packaged app and can never work in this installer build).</summary>
public enum SpeechEngine { Auto, Windows, OpenAI, Gemini, WindowsOnline }

public enum TalkKeyMode { Toggle, Hold }

/// <summary>Non-secret user settings (JSON in %LocalAppData%\VoxStride). API keys live in Windows Credential Manager.</summary>
public sealed class AppSettings
{
    public bool FirstRunCompleted { get; set; }
    public bool PrivacyConsent { get; set; }
    public bool MicrophoneConsent { get; set; }
    public ProviderKind? ActiveProvider { get; set; }
    /// <summary>Backup provider used when the active one is unavailable before any action ran (needs its own key).</summary>
    public ProviderKind? FallbackProvider { get; set; }
    public Dictionary<ProviderKind, string> Models { get; set; } = new()
    {
        [ProviderKind.Claude] = ProviderFactory.DefaultModel(ProviderKind.Claude),
        [ProviderKind.OpenAI] = ProviderFactory.DefaultModel(ProviderKind.OpenAI),
        [ProviderKind.Gemini] = ProviderFactory.DefaultModel(ProviderKind.Gemini),
    };
    public string SpeechCulture { get; set; } = "en-US";
    /// <summary>Auto = OpenAI/Gemini cloud speech when allowed and a key exists, else Windows offline speech.</summary>
    public SpeechEngine SpeechEngine { get; set; } = SpeechEngine.Auto;
    /// <summary>Explicit consent to send voice audio (only while giving an instruction) to the chosen speech service.</summary>
    public bool CloudSpeechConsent { get; set; }
    public bool NaturalVoice { get; set; } = true;
    public string CloudVoiceName { get; set; } = "marin";
    public double MicSensitivity { get; set; } = 3.0;
    /// <summary>Recording device name; null = Windows default microphone.</summary>
    public string? MicrophoneName { get; set; }
    public bool AutoCheckUpdates { get; set; } = true;
    public bool AutoInstallUpdates { get; set; } = true;
    public DateTimeOffset LastUpdateCheck { get; set; }
    public string? Voice { get; set; }
    public int VoiceRate { get; set; } = 1;
    public bool SpeakResults { get; set; } = true;
    public bool AudioCues { get; set; } = true;
    public bool WakeWordEnabled { get; set; } = true;
    /// <summary>Release the microphone (stop wake-word listening) while Teams, Zoom or any other app is using it.</summary>
    public bool YieldMicToOtherApps { get; set; } = true;
    /// <summary>A sentence that is only "proceed", "execute", "go ahead" or "do it" finishes the instruction. On by default (owner feedback 0.7.4).</summary>
    public bool AllowPlainProceed { get; set; } = true;

    // ---- Live companion (real-time conversation, owner decision 2026-11-28) ----
    /// <summary>Talk in real time with the on-screen companion (needs a Gemini key). On by default.</summary>
    public bool LiveMode { get; set; } = true;
    /// <summary>Start the conversation (microphone on) as soon as VoxStride starts.</summary>
    public bool LiveStartListening { get; set; } = true;
    /// <summary>Using speakers rather than a headset: VoxStride stops listening while it talks so it doesn't hear itself.</summary>
    public bool LiveUsingSpeakers { get; set; }
    /// <summary>Let the companion glance at the screen now and then and comment (needs "Looking at my screen: whenever it needs to").</summary>
    public bool LiveScreenComments { get; set; }
    /// <summary>Twitch co-host: read this channel's chat (anonymously) and react to viewers out loud.</summary>
    public bool TwitchCoHost { get; set; }
    public string? TwitchChannel { get; set; }
    public int LiveScreenCommentSeconds { get; set; } = 90;
    public bool CompanionVisible { get; set; } = true;
    /// <summary>Where the companion was last placed (null: bottom-right of the screen).</summary>
    public double? CompanionLeft { get; set; }
    public double? CompanionTop { get; set; }
    public double CompanionScale { get; set; } = 1.0;
    public string? UserName { get; set; }
    /// <summary>Which of VoxStride's characters is shown (see CompanionCharacters).</summary>
    public string CompanionCharacter { get; set; } = "nova";
    /// <summary>The user's own picture, copied into VoxStride's folder, shown instead of a drawn character.</summary>
    public string? CompanionImagePath { get; set; }
    public string? CompanionPersonality { get; set; }
    public int InactivityReminderSeconds { get; set; } = 30;
    public int InactivityDiscardSeconds { get; set; } = 120;
    public bool RetainTranscripts { get; set; }
    public bool LaunchAtLogon { get; set; }
    public long MaxTokensPerRun { get; set; } = 200_000;
    public long DailyTokenBudget { get; set; } = 2_000_000;
    public int MaxSteps { get; set; } = 30;
    public int MaxRunSeconds { get; set; } = 180;
    public bool ShowOverlay { get; set; } = true;
    /// <summary>Talk key: Toggle = press to start, press again to finish; Hold = hold Ctrl+Alt+V while speaking, release to run (a quick tap still toggles).</summary>
    public TalkKeyMode TalkKey { get; set; } = TalkKeyMode.Toggle;
    /// <summary>Opt-in natural mode: a pause ends the turn, but such turns may only read and answer (changes still need "VoxStride, execute").</summary>
    public bool NaturalMode { get; set; }
    public double NaturalPauseSeconds { get; set; } = 1.5;
    /// <summary>When the VoxStride extension is connected, act in the user's own Chrome/Edge instead of VoxStride's browser window.</summary>
    public bool UseMyBrowser { get; set; } = true;
    /// <summary>Apps VoxStride must never open or control (names or exe names).</summary>
    public List<string> BlockedApps { get; set; } = [];
    /// <summary>Websites VoxStride must never visit (domains; subdomains included).</summary>
    public List<string> BlockedSites { get; set; } = [];
    /// <summary>Window screenshots for the vision fallback: ask each time (default) or never.</summary>
    public VoxStride.Core.Policy.ScreenCapturePolicy ScreenCapture { get; set; } = VoxStride.Core.Policy.ScreenCapturePolicy.AskEachTime;
    /// <summary>Extra folders (absolute paths) the file tools may use, in addition to the default known folders.</summary>
    public List<string> AllowedFolders { get; set; } = [];
    /// <summary>Default known folders the user switched off (e.g. "Pictures").</summary>
    public List<string> DisabledKnownFolders { get; set; } = [];
    /// <summary>Include what the user says and VoxStride's replies in the local diagnostics log (never audio, never keys).</summary>
    public bool LogConversationText { get; set; } = true;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals, Converters = { new JsonStringEnumConverter() } };

    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VoxStride");
    public static string FilePath => Path.Combine(Folder, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json) ?? new AppSettings();
                if (loaded.SpeechEngine == SpeechEngine.WindowsOnline) loaded.SpeechEngine = SpeechEngine.Auto;
                loaded._saved = loaded.Snapshot();
                return loaded;
            }
        }
        catch (Exception ex) { Log.Warn("app", "settings file unreadable; using defaults", ex); /* corrupt settings: fall back to safe defaults */ }
        var fresh = new AppSettings();
        fresh._saved = fresh.Snapshot();
        return fresh;
    }

    private Dictionary<string, string> _saved = new();

    /// <summary>Every setting as text (settings hold no secrets; keys live in Windows Credential Manager).</summary>
    public Dictionary<string, string> Snapshot()
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (k, v) in JsonSerializer.SerializeToNode(this, Json)!.AsObject())
            d[k] = k is nameof(UserName) or nameof(CompanionPersonality) or nameof(CompanionImagePath) ? (v is null ? "null" : "(set, not logged)") : v?.ToJsonString() ?? "null"; // personal text stays out of the log
        return d;
    }

    public void Save()
    {
        var now = Snapshot();
        var changes = new List<string>();
        foreach (var (k, v) in now)
            if (k != nameof(LastUpdateCheck) && (!_saved.TryGetValue(k, out var old) || old != v)) changes.Add($"{k}: {(_saved.TryGetValue(k, out var o) ? o : "unset")} -> {v}");
        if (changes.Count > 0) Log.Info("app", "settings changed: " + string.Join("; ", changes));
        _saved = now;
        Directory.CreateDirectory(Folder);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        File.Move(tmp, FilePath, overwrite: true);
    }
}
