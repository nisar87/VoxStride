using System.Text.RegularExpressions;

namespace VoxStride.Core.Voice;

public enum ControlKind { Content, Finish, FinishUncertain, Cancel, UndoLast, Replace, Stop, Pause }

public sealed record ControlResult(ControlKind Kind, string? Content = null);

public sealed class ControlPhraseOptions
{
    /// <summary>Distinctive finish phrases (prefixed). Recognized at the end of an utterance or standalone.</summary>
    public IReadOnlyList<string> FinishPhrases { get; init; } = ["voxstride execute", "voxstride proceed", "vox stride execute", "vox stride proceed", "assistant proceed", "assistant execute", "execute now"];
    /// <summary>Plain "proceed" convenience (off by default). Only as a standalone utterance.</summary>
    public bool AllowPlainProceed { get; init; }
    public double MinConfidence { get; init; } = 0.55;
}

/// <summary>
/// Classifies a FINAL recognized utterance as content or a control command. Control is recognized only at a
/// standalone utterance boundary; quoted, requested-as-content or negated phrases are content. Uncertain finish
/// detection never executes — it asks for deliberate confirmation.
/// </summary>
public sealed partial class ControlPhraseDetector(ControlPhraseOptions options)
{
    public ControlResult Classify(string utterance, double? confidence = null)
    {
        var raw = utterance.Trim();
        var t = Normalize(raw);
        if (t.Length == 0) return new ControlResult(ControlKind.Content, "");

        // Content requests mentioning control words ("write the sentence 'please proceed'") stay content.
        var looksQuoted = raw.Contains('"', StringComparison.Ordinal) || raw.Contains('“', StringComparison.Ordinal) || QuotedSingle().IsMatch(raw);
        var contentVerb = ContentVerb().IsMatch(t);

        if (IsWhole(t, "cancel", "voxstride cancel", "vox stride cancel", "never mind", "nevermind", "cancel that", "cancel everything", "forget it"))
            return new ControlResult(ControlKind.Cancel);
        if (IsWhole(t, "stop listening", "voxstride stop", "vox stride stop", "stop", "stop stop"))
            return new ControlResult(ControlKind.Stop);
        if (IsWhole(t, "pause", "voxstride pause", "hold on"))
            return new ControlResult(ControlKind.Pause);
        if (IsWhole(t, "undo last sentence", "undo that sentence", "scratch that", "delete that", "delete last sentence", "remove last sentence", "strike that"))
            return new ControlResult(ControlKind.UndoLast);
        var rep = Replace().Match(t);
        if (rep.Success && !looksQuoted) return new ControlResult(ControlKind.Replace, RestoreTail(raw, rep.Groups["x"].Value));

        // Finish: standalone distinctive phrase.
        foreach (var p in options.FinishPhrases)
        {
            if (t == p)
                return confidence is { } c && c < options.MinConfidence ? new ControlResult(ControlKind.FinishUncertain) : new ControlResult(ControlKind.Finish);
        }
        if (options.AllowPlainProceed && IsWhole(t, "proceed", "go ahead", "execute", "do it"))
            return confidence is { } c2 && c2 < options.MinConfidence ? new ControlResult(ControlKind.FinishUncertain) : new ControlResult(ControlKind.Finish);

        // Finish at the end of a content utterance: only distinctive phrases, never quoted/requested-as-content/negated.
        foreach (var p in options.FinishPhrases)
        {
            if (!t.EndsWith(" " + p, StringComparison.Ordinal)) continue;
            var before = t[..^(p.Length + 1)].TrimEnd(' ', ',', '.');
            if (looksQuoted || contentVerb && MentionsPhraseAsContent().IsMatch(before) || Negated().IsMatch(before + " "))
                return new ControlResult(ControlKind.FinishUncertain, RestoreCase(raw, before));
            if (confidence is { } c3 && c3 < options.MinConfidence) return new ControlResult(ControlKind.FinishUncertain, RestoreCase(raw, before));
            return new ControlResult(ControlKind.Finish, RestoreCase(raw, before));
        }

        // Plain trailing "proceed"/"execute" is ambiguous: keep as content but flag so the UI can ask "Ready to execute?".
        if (TrailingPlainFinish().IsMatch(t) && !looksQuoted && !contentVerb)
            return new ControlResult(ControlKind.FinishUncertain, raw);

        return new ControlResult(ControlKind.Content, raw);
    }

    private static bool IsWhole(string t, params string[] phrases) => phrases.Any(p => t == p);

    internal static string Normalize(string s)
    {
        s = s.ToLowerInvariant().Replace("’", "'", StringComparison.Ordinal);
        s = NonWord().Replace(s, " ");
        s = Spaces().Replace(s, " ").Trim();
        s = LeadingPolite().Replace(s, "");
        return s.Trim();
    }

    /// <summary>Maps a normalized prefix back to the original-cased words of the raw utterance.</summary>
    private static string RestoreCase(string raw, string normalizedPrefix)
    {
        var words = normalizedPrefix.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        if (words == 0) return "";
        var rawWords = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        // Skip leading polite words that Normalize removed.
        var skip = rawWords.Length - Normalize(raw).Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        if (skip < 0) skip = 0;
        var take = Math.Min(words, rawWords.Length - skip);
        return string.Join(' ', rawWords.Skip(skip).Take(take)).TrimEnd(',', '.', ';');
    }

    private static string RestoreTail(string raw, string normalizedTail)
    {
        var words = normalizedTail.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        var rawWords = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', rawWords.Skip(Math.Max(0, rawWords.Length - words))).Trim();
    }

    [GeneratedRegex(@"[^\p{L}\p{N}'+#]+")]
    private static partial Regex NonWord();
    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
    [GeneratedRegex(@"^(?:okay|ok|please|so|alright|all right|now)\s+")]
    private static partial Regex LeadingPolite();
    [GeneratedRegex(@"'[^']{3,}'")]
    private static partial Regex QuotedSingle();
    [GeneratedRegex(@"\b(write|type|say|dictate|insert|enter|draft|put|add)\b")]
    private static partial Regex ContentVerb();
    [GeneratedRegex(@"\b(the (word|words|sentence|phrase|text|line)|saying|that says|reading)\s*$")]
    private static partial Regex MentionsPhraseAsContent();
    [GeneratedRegex(@"\b(don't|do not|not|never|wait to|before you)\s*$")]
    private static partial Regex Negated();
    [GeneratedRegex(@"^(?:actually\s+)?replace (?:that|the last sentence|it) with (?<x>.+)$")]
    private static partial Regex Replace();
    [GeneratedRegex(@"\b(proceed|execute|go ahead)$")]
    private static partial Regex TrailingPlainFinish();
}
