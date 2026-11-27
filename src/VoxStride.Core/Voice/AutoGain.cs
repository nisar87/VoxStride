namespace VoxStride.Core.Voice;

/// <summary>
/// Digital automatic gain for quiet microphones, applied inside VoxStride to the audio it captures. It never changes
/// the Windows microphone level or any device setting. It tracks the room's noise floor and the level of speech, then
/// raises quiet speech towards <see cref="TargetRms"/>. Gain only goes up slowly and comes down at once, it is capped
/// at <see cref="MaxGain"/>, and peaks are soft-limited so boosted speech never clips. Loud microphones pass through
/// unchanged (gain 1).
/// </summary>
public sealed class AutoGain
{
    public double TargetRms { get; init; } = 3000;
    public double MaxGain { get; init; } = 16;
    /// <summary>A buffer counts as speech when it is this many times the noise floor (and above <see cref="MinSpeechRms"/>).</summary>
    public double SpeechFactor { get; init; } = 4;
    public double MinSpeechRms { get; init; } = 30;

    private double _noise = 50;
    private double _speech;
    private double _gain = 1;

    public double Gain => _gain;
    public double NoiseFloor => _noise;
    public double SpeechLevel => _speech;

    /// <summary>Returns the (possibly boosted) 16-bit mono PCM. The input array is never modified.</summary>
    public byte[] Process(byte[] pcm16)
    {
        var rms = UtteranceSegmenter.Rms(pcm16);
        var speech = rms > Math.Max(MinSpeechRms, _noise * SpeechFactor);
        if (!speech) _noise = _noise * 0.98 + rms * 0.02;
        else _speech = _speech <= 0 ? rms : rms > _speech ? _speech * 0.7 + rms * 0.3 : _speech * 0.995 + rms * 0.005;

        if (_speech > 0)
        {
            var want = Math.Clamp(TargetRms / _speech, 1, MaxGain);
            _gain = want < _gain ? want : _gain * 0.9 + want * 0.1; // fast down, slow up
        }
        if (_gain <= 1.01) return pcm16;

        var output = new byte[pcm16.Length];
        for (var i = 0; i + 1 < pcm16.Length; i += 2)
        {
            var s = (short)(pcm16[i] | (pcm16[i + 1] << 8)) * _gain;
            var v = (int)Math.Round(SoftLimit(s));
            output[i] = (byte)v;
            output[i + 1] = (byte)(v >> 8);
        }
        return output;
    }

    private static double SoftLimit(double v)
    {
        const double knee = 24000, ceiling = 32767;
        var a = Math.Abs(v);
        if (a <= knee) return v;
        var over = a - knee;
        var limited = knee + (ceiling - knee) * (1 - Math.Exp(-over / (ceiling - knee)));
        return Math.Sign(v) * Math.Min(ceiling, limited);
    }
}
