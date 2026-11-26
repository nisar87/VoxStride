namespace VoxStride.Core.Voice;

/// <summary>A completed speech segment (16-bit mono PCM) with its position in the session's audio timeline.</summary>
public sealed record AudioSegment(int Index, long StartMs, long EndMs, byte[] Pcm16, int SampleRate);

/// <summary>
/// Energy-based voice-activity segmenter for cloud transcription. It cuts the microphone stream into utterances at
/// natural pauses so each one can be transcribed independently. A segment boundary is NOT a command boundary:
/// the conversation coordinator still requires an explicit finish. Adapts its noise floor to the room.
/// </summary>
public sealed class UtteranceSegmenter(int sampleRate = 16000)
{
    public int SampleRate { get; } = sampleRate;
    public TimeSpan FrameLength { get; } = TimeSpan.FromMilliseconds(20);
    public TimeSpan EndSilence { get; init; } = TimeSpan.FromMilliseconds(750);
    public TimeSpan MinSpeech { get; init; } = TimeSpan.FromMilliseconds(250);
    public TimeSpan MaxSegment { get; init; } = TimeSpan.FromSeconds(28);
    public TimeSpan PreRoll { get; init; } = TimeSpan.FromMilliseconds(300);
    /// <summary>Speech threshold as a multiple of the adaptive noise floor.</summary>
    public double Sensitivity { get; init; } = 3.0;
    public double MinRms { get; init; } = 250;

    public event Action<long>? SpeechStarted;
    public event Action<AudioSegment>? SegmentReady;
    /// <summary>Raised when detected sound turned out too short to be speech (a cough, a click).</summary>
    public event Action? SpeechDiscarded;

    private readonly List<byte> _pending = [];          // bytes not yet forming a full frame
    private readonly Queue<byte[]> _preRoll = new();
    private readonly List<byte> _current = [];
    private bool _inSpeech;
    private int _silentFrames;
    private int _speechFrames;
    private long _framesSeen;
    private long _segmentStartFrame;
    private int _index;
    private double _noiseFloor = 400;

    private int FrameBytes => SampleRate * 2 * (int)FrameLength.TotalMilliseconds / 1000;
    private int Frames(TimeSpan t) => (int)Math.Ceiling(t.TotalMilliseconds / FrameLength.TotalMilliseconds);
    public double NoiseFloor => _noiseFloor;

    public void Push(ReadOnlySpan<byte> pcm16)
    {
        foreach (var b in pcm16) _pending.Add(b);
        var fb = FrameBytes;
        while (_pending.Count >= fb)
        {
            var frame = _pending.GetRange(0, fb).ToArray();
            _pending.RemoveRange(0, fb);
            ProcessFrame(frame);
        }
    }

    /// <summary>Emits any in-progress speech as a final segment (used when the user finishes or stops).</summary>
    public void Flush()
    {
        if (_inSpeech && _speechFrames >= Frames(MinSpeech)) Emit();
        else if (_inSpeech) { Reset(); SpeechDiscarded?.Invoke(); }
        else Reset();
    }

    private void ProcessFrame(byte[] frame)
    {
        _framesSeen++;
        var rms = Rms(frame);
        var threshold = Math.Max(MinRms, _noiseFloor * Sensitivity);
        var voiced = rms > threshold;
        if (!voiced) _noiseFloor = _noiseFloor * 0.97 + rms * 0.03; // adapt only on non-speech frames

        if (!_inSpeech)
        {
            _preRoll.Enqueue(frame);
            while (_preRoll.Count > Frames(PreRoll)) _preRoll.Dequeue();
            if (!voiced) return;
            _inSpeech = true;
            _speechFrames = 1;
            _silentFrames = 0;
            _segmentStartFrame = _framesSeen - _preRoll.Count;
            foreach (var f in _preRoll) _current.AddRange(f);
            _preRoll.Clear();
            SpeechStarted?.Invoke(StartMs(_segmentStartFrame));
            return;
        }

        _current.AddRange(frame);
        if (voiced) { _speechFrames++; _silentFrames = 0; } else _silentFrames++;
        var length = _framesSeen - _segmentStartFrame;
        if (_silentFrames >= Frames(EndSilence) || length >= Frames(MaxSegment))
        {
            if (_speechFrames >= Frames(MinSpeech)) Emit(); else { Reset(); SpeechDiscarded?.Invoke(); }
        }
    }

    private void Emit()
    {
        var seg = new AudioSegment(++_index, StartMs(_segmentStartFrame), StartMs(_framesSeen), _current.ToArray(), SampleRate);
        Reset();
        SegmentReady?.Invoke(seg);
    }

    private void Reset()
    {
        _current.Clear();
        _inSpeech = false;
        _speechFrames = 0;
        _silentFrames = 0;
    }

    private long StartMs(long frame) => (long)(frame * FrameLength.TotalMilliseconds);

    public static double Rms(ReadOnlySpan<byte> pcm16)
    {
        double sum = 0;
        var n = pcm16.Length / 2;
        for (var i = 0; i < n; i++)
        {
            var s = (short)(pcm16[2 * i] | (pcm16[2 * i + 1] << 8));
            sum += (double)s * s;
        }
        return n == 0 ? 0 : Math.Sqrt(sum / n);
    }
}

public static class Wav
{
    /// <summary>Wraps 16-bit mono PCM in a RIFF/WAVE container.</summary>
    public static byte[] FromPcm16(ReadOnlySpan<byte> pcm, int sampleRate, short channels = 1)
    {
        using var ms = new MemoryStream(44 + pcm.Length);
        using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8); w.Write(36 + pcm.Length); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write(channels); w.Write(sampleRate);
        w.Write(sampleRate * 2 * channels); w.Write((short)(2 * channels)); w.Write((short)16);
        w.Write("data"u8); w.Write(pcm.Length); w.Write(pcm);
        w.Flush();
        return ms.ToArray();
    }
}
