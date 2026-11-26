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

        // Accent-tolerant match: "<voxstride-like> <execute-like>" at the end (e.g. "box stride exicute", "VoxStride chalo").
        if (FuzzyFinish(t) is { } fuzzy)
        {
            var before = fuzzy.Before;
            if (looksQuoted || contentVerb && MentionsPhraseAsContent().IsMatch(before) || Negated().IsMatch(before + " "))
                return new ControlResult(ControlKind.FinishUncertain, before.Length == 0 ? null : RestoreCase(raw, before));
            var sure = fuzzy.Score >= 0.84 && (confidence is null || confidence >= options.MinConfidence);
            return new ControlResult(sure ? ControlKind.Finish : ControlKind.FinishUncertain, before.Length == 0 ? null : RestoreCase(raw, before));
        }

        // Plain trailing "proceed"/"execute" is ambiguous: keep as content but flag so the UI can ask "Ready to execute?".
        if (TrailingPlainFinish().IsMatch(t) && !looksQuoted && !contentVerb)
            return new ControlResult(ControlKind.FinishUncertain, raw);

        return new ControlResult(ControlKind.Content, raw);
    }

    private static readonly string[] FinishVerbs = ["execute", "proceed", "chalo", "karo", "kardo", "shuru", "start", "go"];

    internal sealed record FuzzyMatch(string Before, double Score);

    /// <summary>
    /// Matches a distinctive wake-name token (1–2 words resembling "voxstride") followed by a finish verb at the end of
    /// the utterance. Requires the name: a bare "execute" never matches here.
    /// </summary>
    internal static FuzzyMatch? FuzzyFinish(string t)
    {
        var w = t.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (w.Length < 2) return null;
        // finish verb may be one word ("execute") or "kar do"/"shuru karo"
        var verbWords = 1;
        var verb = w[^1];
        if (w.Length >= 3 && (w[^2] + w[^1]) is "kardo" or "shurukaro") { verbWords = 2; verb = w[^2] + w[^1]; }
        var verbScore = FinishVerbs.Max(v => Similarity(verb, v));
        if (verbScore < 0.72) return null;
        if (verb.Length <= 3 && verbScore < 1) return null; // short verbs ("go") must be exact
        for (var nameWords = 2; nameWords >= 1; nameWords--)
        {
            var nameEnd = w.Length - verbWords;
            var nameStart = nameEnd - nameWords;
            if (nameStart < 0) continue;
            var name = string.Concat(w[nameStart..nameEnd]);
            var nameScore = Similarity(name, "voxstride");
            if (nameScore < 0.7) continue;
            var score = Math.Min(nameScore, verbScore);
            return new FuzzyMatch(string.Join(' ', w[..nameStart]).TrimEnd(',', '.'), score);
        }
        return null;
    }

    /// <summary>Normalized Levenshtein similarity (1 = identical).</summary>
    internal static double Similarity(string a, string b)
    {
        if (a == b) return 1;
        if (a.Length == 0 || b.Length == 0) return 0;
        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (var j = 0; j <= b.Length; j++) d[0, j] = j;
        for (var i = 1; i <= a.Length; i++)
            for (var j = 1; j <= b.Length; j++)
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
        return 1.0 - (double)d[a.Length, b.Length] / Math.Max(a.Length, b.Length);
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
