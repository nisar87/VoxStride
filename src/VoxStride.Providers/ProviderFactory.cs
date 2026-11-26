using VoxStride.Contracts;

namespace VoxStride.Providers;

public static class ProviderFactory
{
    /// <summary>Default models (Nov 2026, user-overridable in Settings and validated with the key check).</summary>
    public static string DefaultModel(ProviderKind kind) => kind switch
    {
        ProviderKind.Claude => "claude-sonnet-5-5",
        ProviderKind.OpenAI => "gpt-6.1-terra",
        ProviderKind.Gemini => "gemini-3.8-flash",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public static IReadOnlyList<string> SuggestedModels(ProviderKind kind) => kind switch
    {
        ProviderKind.Claude => ["claude-sonnet-5-5", "claude-haiku-5-5", "claude-opus-5-5"],
        ProviderKind.OpenAI => ["gpt-6.1-terra", "gpt-6.1-sol", "gpt-6-astra"],
        ProviderKind.Gemini => ["gemini-3.8-flash", "gemini-3.5-flash-lite", "gemini-4-argon"],
        _ => [],
    };

    public static IReasoningProvider Create(ProviderSettings s, HttpMessageHandler? handler = null) => s.Kind switch
    {
        ProviderKind.Claude => new ClaudeProvider(s, handler),
        ProviderKind.OpenAI => new OpenAIProvider(s, handler),
        ProviderKind.Gemini => new GeminiProvider(s, handler),
        _ => throw new ArgumentOutOfRangeException(nameof(s)),
    };
}
