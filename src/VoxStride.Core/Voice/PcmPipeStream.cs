namespace VoxStride.Core.Voice;

/// <summary>
/// Live-audio stream for SAPI-style consumers (System.Speech <c>SetInputToAudioStream</c>).
/// <para>
/// SAPI treats any read that returns fewer bytes than requested as <b>end of stream</b>. A naive producer/consumer
/// stream that hands back whatever chunk is available therefore makes the engine stop after the first capture buffer
/// and recognize nothing. This stream blocks until the full requested amount is available, and returns a short read
/// only once <see cref="Complete"/> has been called (genuine end of audio).
/// </para>
/// <para>
/// Audio is held in a bounded ring buffer: if the consumer stalls, the oldest audio is dropped (counted in
/// <see cref="DroppedBytes"/>) instead of growing memory without limit.
/// </para>
/// </summary>
public sealed class PcmPipeStream : Stream
{
    private readonly byte[] _ring;
    private int _head; // next read index
    private int _count; // bytes buffered
    private long _readTotal;
    private bool _completed;
    private readonly object _gate = new();

    /// <param name="capacityBytes">Ring size; must exceed the largest single read the consumer issues.</param>
    public PcmPipeStream(int capacityBytes = 16000 * 2 * 10)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacityBytes, 1024);
        _ring = new byte[capacityBytes];
    }

    public int Capacity => _ring.Length;
    public long WrittenBytes { get; private set; }
    public long DroppedBytes { get; private set; }
    public bool IsCompleted { get { lock (_gate) return _completed; } }
    public int Buffered { get { lock (_gate) return _count; } }

    /// <summary>Appends captured PCM. Ignored after <see cref="Complete"/>.</summary>
    public void Append(ReadOnlySpan<byte> data)
    {
        lock (_gate)
        {
            if (_completed || data.IsEmpty) return;
            if (data.Length >= _ring.Length)
            {
                DroppedBytes += _count + (data.Length - _ring.Length);
                data = data[^_ring.Length..];
                _head = 0; _count = 0;
            }
            var overflow = _count + data.Length - _ring.Length;
            if (overflow > 0)
            {
                _head = (_head + overflow) % _ring.Length;
                _count -= overflow;
                DroppedBytes += overflow;
            }
            var tail = (_head + _count) % _ring.Length;
            var first = Math.Min(data.Length, _ring.Length - tail);
            data[..first].CopyTo(_ring.AsSpan(tail));
            data[first..].CopyTo(_ring.AsSpan(0));
            _count += data.Length;
            WrittenBytes += data.Length;
            Monitor.PulseAll(_gate);
        }
    }

    public void Append(byte[] data) => Append(data.AsSpan());

    /// <summary>Marks end of audio: buffered audio is still delivered, then reads return 0.</summary>
    public void Complete()
    {
        lock (_gate) { _completed = true; Monitor.PulseAll(_gate); }
    }

    /// <summary>Ends the stream and discards anything not yet read (used on cancel: nothing more is recognized).</summary>
    public void Abort()
    {
        lock (_gate)
        {
            DroppedBytes += _count;
            _count = 0;
            _completed = true;
            Monitor.PulseAll(_gate);
        }
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (buffer.IsEmpty) return 0;
        lock (_gate)
        {
            // Wait for the whole request (capped at the ring size) — a short read would end recognition.
            var want = Math.Min(buffer.Length, _ring.Length);
            while (_count < want && !_completed) Monitor.Wait(_gate);
            var n = Math.Min(buffer.Length, _count);
            var first = Math.Min(n, _ring.Length - _head);
            _ring.AsSpan(_head, first).CopyTo(buffer);
            _ring.AsSpan(0, n - first).CopyTo(buffer[first..]);
            _head = (_head + n) % _ring.Length;
            _count -= n;
            _readTotal += n;
            return n;
        }
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    /// <summary>
    /// Always -1: System.Speech caches <c>Length</c> when the stream is attached and treats -1 as "no known end";
    /// any other value would make it clamp reads at that length. Throwing makes <c>SetInputToAudioStream</c> fail.
    /// </summary>
    public override long Length => -1;
    public override long Position { get { lock (_gate) return _readTotal; } set { } }
    public override void Flush() { }
    /// <summary>Non-seekable; returns the read position so position queries from native callers don't fail.</summary>
    public override long Seek(long offset, SeekOrigin origin) => Position;
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) Complete();
        base.Dispose(disposing);
    }
}
