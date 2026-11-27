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

    public AudioHub(Func<IAudioCaptureDevice> deviceFactory, Action<string>? log = null)
    {
        _factory = deviceFactory;
        _log = log;
    }

    public bool IsOpen { get { lock (_gate) return _device is not null; } }
    public int LeaseCount { get { lock (_gate) return _leases.Length; } }
    public string? DeviceLabel { get { lock (_gate) return _device?.Label; } }

    /// <summary>RMS level (0–32767) of each captured buffer while the device is open.</summary>
    public event Action<double>? Level;

    /// <summary>Raised when the device reports a capture error.</summary>
    public event Action<string>? DeviceError;

    /// <summary>Starts receiving microphone audio. Opens the device if this is the first lease.</summary>
    /// <exception cref="InvalidOperationException">The device could not be opened.</exception>
    public AudioLease Acquire(string purpose)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_device is null) Open();
            var lease = new AudioLease(this, purpose, _device!.Label, _device.SampleRate);
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
        foreach (var lease in Volatile.Read(ref _leases)) lease.Deliver(data, rms);
        Level?.Invoke(rms);
    }

    private void OnError(string message)
    {
        _log?.Invoke("mic-error: " + message);
        DeviceError?.Invoke(message);
    }

    internal void Release(AudioLease lease)
    {
        lock (_gate)
        {
            var before = _leases.Length;
            _leases = [.. _leases.Where(l => !ReferenceEquals(l, lease))];
            if (_leases.Length == before) return;
            _log?.Invoke($"mic-lease purpose={lease.Purpose} device=\"{lease.DeviceLabel}\" ms={(long)lease.Elapsed.TotalMilliseconds} buffers={lease.Buffers} peak={lease.Peak:0}");
            if (_leases.Length == 0) Close("no listeners");
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

    /// <summary>Raised on the capture thread with each PCM buffer. Handlers must be quick and must not block.</summary>
    public event Action<byte[]>? Data;

    /// <summary>Health of what this lease has received so far.</summary>
    public MicHealth Health(TimeSpan minimumObservation) => MicHealthCheck.Evaluate(Buffers, Peak, Elapsed, minimumObservation);

    internal void Rebind(string label, int rate) { DeviceLabel = label; SampleRate = rate; }

    internal void Deliver(byte[] data, double rms)
    {
        if (IsReleased) return;
        Interlocked.Increment(ref _buffers);
        Interlocked.Add(ref _bytes, data.Length);
        if (rms > Volatile.Read(ref _peak)) Volatile.Write(ref _peak, rms);
        Data?.Invoke(data);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Data = null;
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
