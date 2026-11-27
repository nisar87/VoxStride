using VoxStride.Contracts;

namespace VoxStride.Core.Voice;

/// <summary>
/// Lets the speech engine change while VoxStride runs (e.g. a Gemini or OpenAI key was just saved) without a restart.
/// The swap happens only between listening sessions: if a session is active, the new engine takes over when it stops,
/// so an instruction in progress is never cut or mixed across engines. Events from the old engine stop at the swap.
/// </summary>
public sealed class SwitchableRecognizer : ISpeechRecognizer, ISpeechDiagnostics
{
    private readonly Lock _gate = new();
    private readonly Func<ISpeechRecognizer, TimeSpan>? _flushLimit;
    private ISpeechRecognizer _inner;
    private ISpeechRecognizer? _pending;
    private bool _running;

    /// <param name="flushLimit">Optional per-engine cap on the flush wait (cloud engines need longer than local ones).</param>
    public SwitchableRecognizer(ISpeechRecognizer initial, Func<ISpeechRecognizer, TimeSpan>? flushLimit = null)
    {
        _inner = initial;
        _flushLimit = flushLimit;
        Attach(initial);
    }

    public ISpeechRecognizer Current { get { lock (_gate) return _inner; } }
    public string Name => Current.Name;
    public event Action<TranscriptEvent>? Transcript;
    public event Action<string>? Error;
    public event Action<string>? Hint;

    /// <summary>Raised after a new engine has taken over (argument: its name).</summary>
    public event Action<string>? Switched;

    /// <summary>Uses <paramref name="next"/> from the next listening session on (immediately if idle).</summary>
    public async Task ReplaceAsync(ISpeechRecognizer next)
    {
        ISpeechRecognizer? old = null, dropped;
        lock (_gate)
        {
            dropped = _pending;
            if (_running) { _pending = next; }
            else { old = _inner; Detach(old); _inner = next; Attach(next); _pending = null; }
        }
        if (dropped is not null && !ReferenceEquals(dropped, next)) await dropped.DisposeAsync().ConfigureAwait(false);
        if (old is not null)
        {
            await old.DisposeAsync().ConfigureAwait(false);
            Switched?.Invoke(next.Name);
        }
    }

    public async Task StartAsync(Guid sessionId, CancellationToken ct)
    {
        ISpeechRecognizer r;
        lock (_gate) { _running = true; r = _inner; }
        try { await r.StartAsync(sessionId, ct).ConfigureAwait(false); }
        catch { lock (_gate) _running = false; await ApplyPendingAsync().ConfigureAwait(false); throw; }
    }

    public async Task StopAsync(TimeSpan flushTimeout, CancellationToken ct)
    {
        var r = Current;
        if (_flushLimit is not null && flushTimeout > TimeSpan.Zero)
        {
            var cap = _flushLimit(r);
            if (cap < flushTimeout) flushTimeout = cap;
        }
        try { await r.StopAsync(flushTimeout, ct).ConfigureAwait(false); }
        finally
        {
            lock (_gate) _running = false;
            await ApplyPendingAsync().ConfigureAwait(false);
        }
    }

    private async Task ApplyPendingAsync()
    {
        ISpeechRecognizer? old = null, next;
        lock (_gate)
        {
            next = _pending;
            if (next is null || _running) return;
            _pending = null;
            old = _inner; Detach(old); _inner = next; Attach(next);
        }
        await old.DisposeAsync().ConfigureAwait(false);
        Switched?.Invoke(next.Name);
    }

    private void Attach(ISpeechRecognizer r)
    {
        r.Transcript += OnTranscript;
        r.Error += OnError;
        if (r is ISpeechDiagnostics d) d.Hint += OnHint;
    }

    private void Detach(ISpeechRecognizer r)
    {
        r.Transcript -= OnTranscript;
        r.Error -= OnError;
        if (r is ISpeechDiagnostics d) d.Hint -= OnHint;
    }

    private void OnTranscript(TranscriptEvent e) => Transcript?.Invoke(e);
    private void OnError(string m) => Error?.Invoke(m);
    private void OnHint(string m) => Hint?.Invoke(m);

    public async ValueTask DisposeAsync()
    {
        ISpeechRecognizer inner; ISpeechRecognizer? pending;
        lock (_gate) { inner = _inner; pending = _pending; _pending = null; Detach(inner); }
        await inner.DisposeAsync().ConfigureAwait(false);
        if (pending is not null) await pending.DisposeAsync().ConfigureAwait(false);
    }
}
