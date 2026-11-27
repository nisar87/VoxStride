namespace VoxStride.Core.Voice;

/// <summary>Splits a spoken reply into a short first part and the rest, so a natural voice can start sooner.</summary>
public static class SpeechChunks
{
    public static (string First, string? Remainder) Split(string text, int minFirst = 12, int maxFirst = 160)
    {
        text = text.Trim();
        if (text.Length <= maxFirst / 2) return (text, null);
        for (var i = minFirst; i < Math.Min(text.Length - 1, maxFirst); i++)
        {
            if (text[i] is '.' or '!' or '?' && char.IsWhiteSpace(text[i + 1]))
            {
                var rest = text[(i + 1)..].Trim();
                return rest.Length == 0 ? (text, null) : (text[..(i + 1)], rest);
            }
        }
        // No early sentence end: split at a comma, else keep it whole.
        var comma = text.IndexOf(", ", minFirst, Math.Min(text.Length - minFirst, maxFirst - minFirst), StringComparison.Ordinal);
        return comma > 0 ? (text[..(comma + 1)], text[(comma + 2)..].Trim()) : (text, null);
    }
}
