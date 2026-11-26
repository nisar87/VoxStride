using VoxStride.Contracts;

namespace VoxStride.Core.Voice;

/// <summary>
/// Revision-aware transcript assembly (spec §14.2.2). Interim revisions replace earlier text for the same segment;
/// finals are ordered by audio time, not arrival order; stale revisions are ignored; edits are explicit.
/// A pause or VAD boundary never commits anything — this buffer has no notion of "done".
/// </summary>
public sealed class CommandBuffer(Guid sessionId)
{
    private sealed class Segment
    {
        public required string Id;
        public int Revision;
        public bool IsFinal;
        public string Text = "";
        public long StartMs;
        public long Sequence;
        public bool Removed;
        public bool IsControl;
    }

    private readonly Dictionary<string, Segment> _segments = new(StringComparer.Ordinal);
    private readonly List<string> _manualEdits = [];
    private long _arrival;

    public Guid SessionId { get; } = sessionId;
    public int Version { get; private set; }

    /// <summary>Returns true if the event changed the buffer.</summary>
    public bool Apply(TranscriptEvent e)
    {
        if (e.SessionId != SessionId) return false;
        if (_segments.TryGetValue(e.SegmentId, out var s))
        {
            if (s.Removed) return false;
            if (s.IsFinal && !e.IsFinal) return false;           // late interim after final
            if (e.Revision < s.Revision) return false;           // stale revision
            if (e.Revision == s.Revision && s.IsFinal == e.IsFinal && s.Text == e.Text) return false; // duplicate
        }
        else
        {
            s = new Segment { Id = e.SegmentId, Sequence = Interlocked.Increment(ref _arrival) };
            _segments[e.SegmentId] = s;
        }
        s.Revision = e.Revision;
        s.IsFinal = e.IsFinal;
        s.Text = e.Text.Trim();
        s.StartMs = e.StartMs;
        Version++;
        return true;
    }

    /// <summary>Marks a final segment as a control utterance (not task content), e.g. the finish phrase.</summary>
    public void MarkControl(string segmentId, string? remainingContent = null)
    {
        if (!_segments.TryGetValue(segmentId, out var s)) return;
        if (string.IsNullOrWhiteSpace(remainingContent)) s.IsControl = true;
        else s.Text = remainingContent.Trim();
        Version++;
    }

    public int PendingCount => _segments.Values.Count(s => !s.IsFinal && !s.Removed && s.Text.Length > 0);

    public IEnumerable<(string Id, long StartMs)> FinalSegments =>
        Ordered().Where(s => s.IsFinal && !s.Removed && !s.IsControl).Select(s => (s.Id, s.StartMs));

    private IEnumerable<Segment> Ordered() => _segments.Values.OrderBy(s => s.StartMs).ThenBy(s => s.Sequence);

    /// <summary>Removes the most recent content sentence ("undo last sentence").</summary>
    public bool UndoLast()
    {
        if (_manualEdits.Count > 0) { _manualEdits.RemoveAt(_manualEdits.Count - 1); Version++; return true; }
        var last = Ordered().LastOrDefault(s => s.IsFinal && !s.Removed && !s.IsControl && s.Text.Length > 0);
        if (last is null) return false;
        last.Removed = true; Version++;
        return true;
    }

    /// <summary>"Replace that with …" — replaces the most recent content sentence.</summary>
    public bool ReplaceLast(string replacement)
    {
        var last = Ordered().LastOrDefault(s => s.IsFinal && !s.Removed && !s.IsControl && s.Text.Length > 0);
        if (last is null) return false;
        last.Text = replacement.Trim(); Version++;
        return true;
    }

    /// <summary>Typed edits from the UI (transcript correction). Replaces the whole content text.</summary>
    public void SetManualText(string text)
    {
        foreach (var s in _segments.Values) s.Removed = true;
        _manualEdits.Clear();
        _manualEdits.Add(text.Trim());
        Version++;
    }

    public string FinalText(long? beforeStartMs = null)
    {
        var parts = Ordered().Where(s => s.IsFinal && !s.Removed && !s.IsControl && s.Text.Length > 0 && (beforeStartMs is null || s.StartMs < beforeStartMs))
            .Select(s => s.Text).Concat(_manualEdits);
        return Join(parts);
    }

    public string InterimText => Join(Ordered().Where(s => !s.IsFinal && !s.Removed && s.Text.Length > 0).Select(s => s.Text));

    public string DisplayText => Join([FinalText(), InterimText]);

    private static string Join(IEnumerable<string> parts)
    {
        var list = parts.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()).ToList();
        if (list.Count == 0) return "";
        var sb = new System.Text.StringBuilder();
        foreach (var p in list)
        {
            if (sb.Length > 0)
            {
                var last = sb[^1];
                sb.Append(last is '.' or '?' or '!' or ',' ? " " : ". ");
            }
            sb.Append(p);
        }
        return sb.ToString();
    }
}
