namespace VoxStride.Core.Voice;

/// <summary>A physical capture device delivering 16-bit mono PCM buffers.</summary>
public interface IAudioCaptureDevice : IDisposable
{
    /// <summary>Human-readable device name, for logs and messages.</summary>
    string Label { get; }
    int SampleRate { get; }
    event Action<byte[]>? DataAvailable;
    event Action<string>? Error;
    void Start();
    void Stop();
}

/// <summary>
/// The single owner of the microphone. Every consumer (wake word, dictation, cloud speech, the speech test)
/// takes a <see cref="AudioLease"/> instead of opening the device itself, so:
/// <list type="bullet">
/// <item>the device is opened once and shared — handing over from wake word to dictation never reopens it;</item>
/// <item>the device is closed as soon as the last lease is released — nothing can leak an open microphone;</item>
/// <item>each lease records what it actually received (buffers, peak level), which is the evidence used to tell a
/// silent microphone from a stalled one and is written to the diagnostics log.</item>
/// </list>
/// </summary>
public sealed class AudioHub : IDisposable
{
    private readonly Func<IAudioCaptureDevice> _factory;
    private readonly Action<string>? _log;
    private readonly Lock _gate = new();
    private IAudioCaptureDevice? _device;
    private AudioLease[] _leases = [];
    private bool _disposed;
    private readonly byte[] _history;
    private int _historyCount;
    private int _historyHead; // next write index
    private long _position;   // total bytes delivered since the hub was created
    private long _lastDataTick;
    private readonly Timer _watch;
    private int _recoveryAttempt;
    private long _nextRecoveryTick;

    /// <param name="prerollBytes">Audio history kept while the device is open, replayable to a new lease (default 3 s at 16 kHz/16-bit).</param>
    public AudioHub(Func<IAudioCaptureDevice> deviceFactory, Action<string>? log = null, int prerollBytes = 16000 * 2 * 3)
    {
        _factory = deviceFactory;
        _log = log;
        _history = new byte[Math.Max(1024, prerollBytes)];
        _watch = new Timer(_ => Watch(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    /// <summary>How long without any buffer from an open device before it is treated as stalled and re-opened.</summary>
    public TimeSpan StallTimeout { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>Total bytes captured so far; pass to <see cref="Acquire(string, long?)"/> to replay audio from this point.</summary>
    public long Position { get { lock (_gate) return _position; } }

    private (long From, long Expires)? _armed;

    /// <summary>
    /// The next listening lease (within <paramref name="validFor"/>) will start with the audio captured since
    /// <paramref name="from"/>. Used at the wake-word hand-over so the first words of the command are not clipped.
    /// </summary>
    public void ArmReplay(long from, TimeSpan validFor)
    {
        lock (_gate) _armed = (from, Environment.TickCount64 + (long)validFor.TotalMilliseconds);
    }

    /// <summary>Number of times the device was re-opened after an error or stall.</summary>
    public int Recoveries { get; private set; }

    public bool IsOpen { get { lock (_gate) return _device is not null; } }
    public int LeaseCount { get { lock (_gate) return _leases.Length; } }
    public string? DeviceLabel { get { lock (_gate) return _device?.Label; } }

    /// <summary>RMS level (0–32767) of each captured buffer while the device is open.</summary>
    public event Action<double>? Level;

    /// <summary>Raised when the device reports a capture error.</summary>
    public event Action<string>? DeviceError;

    /// <summary>Starts receiving microphone audio. Opens the device if this is the first lease.</summary>
    /// <exception cref="InvalidOperationException">The device could not be opened.</exception>
    /// <param name="replayFrom">A <see cref="Position"/> taken earlier: audio captured since then (up to the pre-roll length) is
    /// delivered to the new lease first, so words spoken during a hand-over (wake word → dictation) aren't lost.</param>
    public AudioLease Acquire(string purpose, long? replayFrom = null)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (replayFrom is null && _armed is { } armed && purpose != "handoff")
            {
                _armed = null;
                if (Environment.TickCount64 <= armed.Expires) replayFrom = armed.From;
            }
            if (_device is null) Open();
            var lease = new AudioLease(this, purpose, _device!.Label, _device.SampleRate);
            if (replayFrom is { } from && from < _position)
            {
                var want = (int)Math.Min(_position - from, _historyCount);
                want -= want % 2; // keep 16-bit samples aligned
                if (want > 0)
                {
                    var chunk = new byte[want];
                    var start = (_historyHead - want + _history.Length) % _history.Length;
                    var first = Math.Min(want, _history.Length - start);
                    Array.Copy(_history, start, chunk, 0, first);
                    Array.Copy(_history, 0, chunk, first, want - first);
                    lease.Preload(chunk);
                    _log?.Invoke($"mic-preroll purpose={purpose} replayed={want * 1000L / (lease.SampleRate * 2)}ms");
                }
            }
            _leases = [.. _leases, lease];
            return lease;
        }
    }

    /// <summary>Re-opens the device (e.g. after the user picks another microphone). Active leases keep receiving audio.</summary>
    public void Reopen()
    {
        lock (_gate)
        {
            if (_device is null) return;
            Close("device changed");
            Open();
            foreach (var l in _leases) l.Rebind(_device!.Label, _device.SampleRate);
        }
    }

    private void Open()
    {
        var device = _factory();
        device.DataAvailable += OnData;
        device.Error += OnError;
        try { device.Start(); }
        catch
        {
            device.DataAvailable -= OnData;
            device.Error -= OnError;
            device.Dispose();
            throw;
        }
        _device = device;
        _lastDataTick = Environment.TickCount64;
        _historyCount = 0; _historyHead = 0;
        _log?.Invoke($"mic-open device=\"{device.Label}\" rate={device.SampleRate}");
    }

    private void Close(string reason)
    {
        var device = _device;
        if (device is null) return;
        _device = null;
        device.DataAvailable -= OnData;
        device.Error -= OnError;
        try { device.Stop(); } catch (Exception ex) { _log?.Invoke("mic-close error: " + ex.Message); }
        device.Dispose();
        _log?.Invoke($"mic-close device=\"{device.Label}\" reason={reason}");
    }

    private void OnData(byte[] data)
    {
        var rms = UtteranceSegmenter.Rms(data);
        AudioLease[] leases;
        lock (_gate)
        {
            // History ring for pre-roll; the lease list is read under the same lock so a new lease's replay and live
            // audio never overlap or leave a gap.
            var n = Math.Min(data.Length, _history.Length);
            var src = data.Length - n;
            var first = Math.Min(n, _history.Length - _historyHead);
            Array.Copy(data, src, _history, _historyHead, first);
            Array.Copy(data, src + first, _history, 0, n - first);
            _historyHead = (_historyHead + n) % _history.Length;
            _historyCount = Math.Min(_history.Length, _historyCount + n);
            _position += data.Length;
            _lastDataTick = Environment.TickCount64;
            _recoveryAttempt = 0;
            leases = _leases;
        }
        // Delivered outside the hub lock (consumers take their own locks). Still gap- and duplicate-free: a lease's replay
        // was cut under the same lock that recorded this buffer, and only leases that existed then receive it live.
        foreach (var lease in leases) lease.Deliver(data, rms);
        Level?.Invoke(rms);
    }

    private void OnError(string message)
    {
        _log?.Invoke("mic-error: " + message);
        DeviceError?.Invoke(message);
        lock (_gate) if (_device is not null && _nextRecoveryTick == 0) _nextRecoveryTick = Environment.TickCount64; // recover on the next watch tick
    }

    /// <summary>
    /// Device recovery: if the open device reports an error or stops delivering audio, it is closed and re-opened
    /// (the factory re-resolves the chosen microphone, falling back to the Windows default if it was unplugged),
    /// with backoff 1 s, 2 s, 4 s … up to 30 s. Leases keep working across the swap.
    /// </summary>
    private void Watch()
    {
        lock (_gate)
        {
            if (_disposed || _leases.Length == 0) { _nextRecoveryTick = 0; return; }
            var now = Environment.TickCount64;
            if (_device is not null && _nextRecoveryTick == 0 && now - _lastDataTick > StallTimeout.TotalMilliseconds)
            {
                _log?.Invoke($"mic-stall device=\"{_device.Label}\" no audio for {(now - _lastDataTick) / 1000.0:0.0}s");
                _nextRecoveryTick = now;
            }
            if (_nextRecoveryTick == 0 || now < _nextRecoveryTick) return;
            _recoveryAttempt++;
            try
            {
                Close("recovering");
                Open();
                foreach (var l in _leases) l.Rebind(_device!.Label, _device.SampleRate);
                Recoveries++;
                _nextRecoveryTick = 0;
                _log?.Invoke($"mic-recovered attempt={_recoveryAttempt} device=\"{_device!.Label}\"");
            }
            catch (Exception ex)
            {
                var delay = Math.Min(30_000, 1000 << Math.Min(_recoveryAttempt - 1, 5));
                _nextRecoveryTick = now + delay;
                _log?.Invoke($"mic-recovery-failed attempt={_recoveryAttempt} retryIn={delay / 1000}s: {ex.Message}");
            }
        }
    }

    internal void Release(AudioLease lease)
    {
        lock (_gate)
        {
            var before = _leases.Length;
            _leases = [.. _leases.Where(l => !ReferenceEquals(l, lease))];
            if (_leases.Length == before) return;
            _log?.Invoke($"mic-lease purpose={lease.Purpose} device=\"{lease.DeviceLabel}\" ms={(long)lease.Elapsed.TotalMilliseconds} buffers={lease.Buffers} peak={lease.Peak:0}");
            if (_leases.Length == 0) { Close("no listeners"); _nextRecoveryTick = 0; }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _leases = [];
            Close("shutdown");
            _watch.Dispose();
        }
    }
}

/// <summary>One consumer's share of the microphone. Dispose to stop receiving audio.</summary>
public sealed class AudioLease : IDisposable
{
    private readonly AudioHub _hub;
    private readonly DateTimeOffset _started = DateTimeOffset.UtcNow;
    private long _buffers;
    private long _bytes;
    private double _peak;
    private int _disposed;

    internal AudioLease(AudioHub hub, string purpose, string deviceLabel, int sampleRate)
    {
        _hub = hub;
        Purpose = purpose;
        DeviceLabel = deviceLabel;
        SampleRate = sampleRate;
    }

    public string Purpose { get; }
    public string DeviceLabel { get; private set; }
    public int SampleRate { get; private set; }
    public long Buffers => Interlocked.Read(ref _buffers);
    public long Bytes => Interlocked.Read(ref _bytes);
    public double Peak => Volatile.Read(ref _peak);
    public TimeSpan Elapsed => DateTimeOffset.UtcNow - _started;
    public bool IsReleased => Volatile.Read(ref _disposed) != 0;

    private readonly object _deliver = new();
    private Action<byte[]>? _data;
    private List<byte[]>? _pending = [];
    private int _pendingBytes;
    private const int MaxPendingBytes = 16000 * 2 * 10;

    /// <summary>
    /// Raised on the capture thread with each PCM buffer, in order. Audio that arrived before the first handler was attached
    /// (including any replayed pre-roll) is delivered to that handler first, so nothing is lost between Acquire and subscribe.
    /// Handlers must be quick and must not block.
    /// </summary>
    public event Action<byte[]>? Data
    {
        add
        {
            if (value is null) return;
            lock (_deliver)
            {
                _data += value;
                if (_pending is { } p) { _pending = null; foreach (var c in p) value(c); }
            }
        }
        remove { lock (_deliver) _data -= value; }
    }

    internal void Preload(byte[] chunk) { lock (_deliver) { _pending?.Add(chunk); _pendingBytes += chunk.Length; } }

    /// <summary>Health of what this lease has received so far.</summary>
    public MicHealth Health(TimeSpan minimumObservation) => MicHealthCheck.Evaluate(Buffers, Peak, Elapsed, minimumObservation);

    internal void Rebind(string label, int rate) { DeviceLabel = label; SampleRate = rate; }

    internal void Deliver(byte[] data, double rms)
    {
        if (IsReleased) return;
        Interlocked.Increment(ref _buffers);
        Interlocked.Add(ref _bytes, data.Length);
        if (rms > Volatile.Read(ref _peak)) Volatile.Write(ref _peak, rms);
        lock (_deliver)
        {
            if (_pending is { } p)
            {
                if (_pendingBytes + data.Length <= MaxPendingBytes) { p.Add(data); _pendingBytes += data.Length; } // a lease nobody reads (meter/handoff) can't grow without bound
                return;
            }
            _data?.Invoke(data);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        lock (_deliver) { _data = null; _pending = null; }
        _hub.Release(this);
    }
}

public enum MicHealth
{
    /// <summary>Not enough time has passed to judge.</summary>
    Unknown,
    /// <summary>Audio is arriving and contains sound.</summary>
    Ok,
    /// <summary>The device is open but delivers no buffers at all (in exclusive use elsewhere, unplugged, or blocked).</summary>
    NoAudio,
    /// <summary>Buffers arrive but are digital silence (muted, wrong input, privacy-blocked).</summary>
    Silent,
}

public static class MicHealthCheck
{
    /// <summary>RMS below this across a whole observation window is treated as digital silence.</summary>
    public const double SilenceThreshold = 30;

    public static MicHealth Evaluate(long buffers, double peak, TimeSpan elapsed, TimeSpan minimumObservation)
    {
        if (peak >= SilenceThreshold) return MicHealth.Ok;
        if (elapsed < minimumObservation) return MicHealth.Unknown;
        return buffers == 0 ? MicHealth.NoAudio : MicHealth.Silent;
    }
}
