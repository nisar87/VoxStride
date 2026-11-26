using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using VoxStride.Contracts;
using VoxStride.Providers;

namespace VoxStride.Desktop;

/// <summary>Non-secret user settings (JSON in %LocalAppData%\VoxStride). API keys live in Windows Credential Manager.</summary>
public sealed class AppSettings
{
    public bool FirstRunCompleted { get; set; }
    public bool PrivacyConsent { get; set; }
    public bool MicrophoneConsent { get; set; }
    public ProviderKind? ActiveProvider { get; set; }
    public Dictionary<ProviderKind, string> Models { get; set; } = new()
    {
        [ProviderKind.Claude] = ProviderFactory.DefaultModel(ProviderKind.Claude),
        [ProviderKind.OpenAI] = ProviderFactory.DefaultModel(ProviderKind.OpenAI),
        [ProviderKind.Gemini] = ProviderFactory.DefaultModel(ProviderKind.Gemini),
    };
    public string SpeechCulture { get; set; } = "en-US";
    public string? Voice { get; set; }
    public int VoiceRate { get; set; } = 1;
    public bool SpeakResults { get; set; } = true;
    public bool AudioCues { get; set; } = true;
    public bool WakeWordEnabled { get; set; } = true;
    public bool AllowPlainProceed { get; set; }
    public int InactivityReminderSeconds { get; set; } = 30;
    public int InactivityDiscardSeconds { get; set; } = 120;
    public bool RetainTranscripts { get; set; }
    public bool LaunchAtLogon { get; set; }
    public long MaxTokensPerRun { get; set; } = 200_000;
    public long DailyTokenBudget { get; set; } = 2_000_000;
    public int MaxSteps { get; set; } = 30;
    public int MaxRunSeconds { get; set; } = 180;
    public bool ShowOverlay { get; set; } = true;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VoxStride");
    public static string FilePath => Path.Combine(Folder, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath)) return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json) ?? new AppSettings();
        }
        catch (Exception) { /* corrupt settings: fall back to safe defaults */ }
        return new AppSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(Folder);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        File.Move(tmp, FilePath, overwrite: true);
    }
}
