using System.Diagnostics;
using System.Text.RegularExpressions;

namespace VoxStride.Contracts;

public enum LogLevel { Debug, Info, Warn, Error }

/// <summary>One diagnostics line. Message and exception text are already redacted.</summary>
public readonly record struct LogEntry(DateTimeOffset Time, LogLevel Level, string Area, string Message, string? Exception, int ThreadId);

public interface ILogSink
{
    void Write(in LogEntry entry);
}

/// <summary>
/// Process-wide diagnostics log used by every VoxStride component. The desktop app installs a file sink; tests can
/// install a capturing sink; with no sink, logging is a no-op. Logging never throws and never blocks on I/O
/// (the sink is responsible for queuing).
/// <para>
/// Every message and exception is passed through <see cref="Redact"/>, so API keys and bearer tokens never reach the
/// log even if they appear in an error body. What the user said and VoxStride's replies are written only through
/// <see cref="Text"/>, which honours <see cref="IncludeContent"/>.
/// </para>
/// Areas: app, ui, voice, mic, speech, wake, tts, route, run, tool, ai, http, update.
/// </summary>
public static partial class Log
{
    private static volatile ILogSink? _sink;

    public static ILogSink? Sink { get => _sink; set => _sink = value; }

    /// <summary>When false, <see cref="Text"/> logs only the length of user/assistant text, not the words.</summary>
    public static bool IncludeContent { get; set; } = true;

    public static LogLevel MinimumLevel { get; set; } = LogLevel.Debug;

    public static bool IsEnabled(LogLevel level) => _sink is not null && level >= MinimumLevel;

    public static void Debug(string area, string message) => Write(LogLevel.Debug, area, message, null);
    public static void Info(string area, string message) => Write(LogLevel.Info, area, message, null);
    public static void Warn(string area, string message, Exception? ex = null) => Write(LogLevel.Warn, area, message, ex);
    public static void Error(string area, string message, Exception? ex = null) => Write(LogLevel.Error, area, message, ex);

    public static void Write(LogLevel level, string area, string message, Exception? ex)
    {
        var sink = _sink;
        if (sink is null || level < MinimumLevel) return;
        try
        {
            sink.Write(new LogEntry(DateTimeOffset.Now, level, area, Redact(message), ex is null ? null : Redact(ex.ToString()), Environment.CurrentManagedThreadId));
        }
        catch (Exception) { /* logging must never break the app */ }
    }

    /// <summary>User words or assistant replies: quoted (and trimmed) when content logging is on, otherwise just the length.</summary>
    public static string Text(string? text, int max = 400)
    {
        if (text is null) return "(none)";
        var t = text.ReplaceLineEndings(" ").Trim();
        if (!IncludeContent) return $"<{t.Length} chars hidden>";
        if (t.Length > max) t = t[..max] + $"…(+{t.Length - max})";
        return "\"" + t + "\"";
    }

    /// <summary>Starts a timer; disposing it logs "<paramref name="what"/> took N ms".</summary>
    public static Timing Time(string area, string what, LogLevel level = LogLevel.Debug) => new(area, what, level);

    public readonly struct Timing : IDisposable
    {
        private readonly string _area, _what;
        private readonly LogLevel _level;
        private readonly long _start;
        internal Timing(string area, string what, LogLevel level) { _area = area; _what = what; _level = level; _start = Stopwatch.GetTimestamp(); }
        public double ElapsedMs => Stopwatch.GetElapsedTime(_start).TotalMilliseconds;
        public void Dispose() { if (_what is not null) Write(_level, _area, $"{_what} took {ElapsedMs:0} ms", null); }
    }

    /// <summary>Removes secrets (API keys, bearer tokens, key query parameters, password/token fields) from text.</summary>
    public static string Redact(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        text = AnthropicKey().Replace(text, "sk-ant-***");
        text = OpenAIKey().Replace(text, "sk-***");
        text = GoogleKey().Replace(text, "AIza***");
        text = Bearer().Replace(text, "$1***");
        text = KeyParam().Replace(text, "$1=***");
        text = SecretField().Replace(text, "$1***");
        return text;
    }

    [GeneratedRegex(@"sk-ant-[A-Za-z0-9_\-]{8,}")] private static partial Regex AnthropicKey();
    [GeneratedRegex(@"sk-(?!ant-)[A-Za-z0-9_\-]{16,}")] private static partial Regex OpenAIKey();
    [GeneratedRegex(@"AIza[0-9A-Za-z_\-]{20,}")] private static partial Regex GoogleKey();
    [GeneratedRegex(@"(?i)(bearer\s+)[A-Za-z0-9._~+/=\-]{10,}")] private static partial Regex Bearer();
    [GeneratedRegex(@"(?i)\b(key|api[_-]?key|access_token|token)=([^&\s""']{6,})")] private static partial Regex KeyParam();
    [GeneratedRegex(@"(?i)(""?(?:x-api-key|x-goog-api-key|api[_-]?key|authorization|password|secret)""?\s*[:=]\s*""?)(?!\*\*\*|Bearer\b)[^""\s,}]{6,}")] private static partial Regex SecretField();
}
