using System.Runtime.InteropServices;
using VoxStride.Contracts;

namespace VoxStride.Speech;

/// <summary>
/// Streams 16-bit mono PCM to the default speakers with the classic waveOut API (no exclusive mode, no change to any
/// Windows sound setting). Chunks play back-to-back as they arrive; <see cref="Clear"/> stops instantly (barge-in).
/// <see cref="Level"/> follows the chunk that is actually playing, for the character's lip sync.
/// </summary>
public sealed class WaveOutStream : ILiveAudioOut
{
    private const int WHDR_DONE = 0x1, WAVE_MAPPER = -1, CALLBACK_NULL = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct WAVEFORMATEX { public ushort wFormatTag, nChannels; public uint nSamplesPerSec, nAvgBytesPerSec; public ushort nBlockAlign, wBitsPerSample, cbSize; }

    [StructLayout(LayoutKind.Sequential)]
    private struct WAVEHDR { public nint lpData; public uint dwBufferLength, dwBytesRecorded; public nint dwUser; public uint dwFlags, dwLoops; public nint lpNext, reserved; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MMTIME { public uint wType; public uint u; public uint pad; }

    [DllImport("winmm.dll")] private static extern int waveOutOpen(out nint h, nint device, ref WAVEFORMATEX fmt, nint callback, nint instance, int flags);
    [DllImport("winmm.dll")] private static extern int waveOutPrepareHeader(nint h, nint hdr, int size);
    [DllImport("winmm.dll")] private static extern int waveOutUnprepareHeader(nint h, nint hdr, int size);
    [DllImport("winmm.dll")] private static extern int waveOutWrite(nint h, nint hdr, int size);
    [DllImport("winmm.dll")] private static extern int waveOutReset(nint h);
    [DllImport("winmm.dll")] private static extern int waveOutClose(nint h);
    [DllImport("winmm.dll")] private static extern int waveOutGetPosition(nint h, ref MMTIME t, int size);

    private sealed record Chunk(nint Header, nint Data, long StartSample, long Samples, double[] Levels);
    private const int LevelWindow = 960; // 40 ms at 24 kHz: smooth mouth movement

    private readonly Lock _gate = new();
    private readonly List<Chunk> _queue = [];
    private nint _h;
    private int _rate;
    private long _queuedSamples;

    public bool IsPlaying
    {
        get { lock (_gate) { Reap(); return _queue.Count > 0; } }
    }

    public double Level
    {
        get
        {
            lock (_gate)
            {
                if (_h == 0 || _queue.Count == 0) return 0;
                var t = new MMTIME { wType = 2 /* TIME_SAMPLES */ };
                if (waveOutGetPosition(_h, ref t, Marshal.SizeOf<MMTIME>()) != 0) return 0;
                var pos = (long)t.u;
                foreach (var c in _queue) if (pos >= c.StartSample && pos < c.StartSample + c.Samples) return c.Levels[Math.Min(c.Levels.Length - 1, (int)((pos - c.StartSample) / LevelWindow))];
                return 0;
            }
        }
    }

    public void Enqueue(byte[] pcm, int sampleRate)
    {
        if (pcm.Length < 2) return;
        lock (_gate)
        {
            if (_h != 0 && _rate != sampleRate) CloseDevice();
            if (_h == 0 && !OpenDevice(sampleRate)) return;
            Reap();
            if (_queue.Count == 0) { waveOutReset(_h); _queuedSamples = 0; } // restart the sample clock when idle
            var data = Marshal.AllocHGlobal(pcm.Length);
            Marshal.Copy(pcm, 0, data, pcm.Length);
            var hdr = Marshal.AllocHGlobal(Marshal.SizeOf<WAVEHDR>());
            Marshal.StructureToPtr(new WAVEHDR { lpData = data, dwBufferLength = (uint)pcm.Length }, hdr, false);
            if (waveOutPrepareHeader(_h, hdr, Marshal.SizeOf<WAVEHDR>()) != 0 || waveOutWrite(_h, hdr, Marshal.SizeOf<WAVEHDR>()) != 0)
            {
                Marshal.FreeHGlobal(hdr); Marshal.FreeHGlobal(data);
                Log.Warn("tts", "speaker refused audio; reopening");
                CloseDevice();
                return;
            }
            var samples = pcm.Length / 2;
            _queue.Add(new Chunk(hdr, data, _queuedSamples, samples, Loudness(pcm)));
            _queuedSamples += samples;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            if (_h == 0) return;
            waveOutReset(_h); // returns every buffer immediately
            foreach (var c in _queue) Free(c);
            _queue.Clear();
            _queuedSamples = 0;
        }
    }

    private bool OpenDevice(int rate)
    {
        var fmt = new WAVEFORMATEX { wFormatTag = 1, nChannels = 1, nSamplesPerSec = (uint)rate, wBitsPerSample = 16, nBlockAlign = 2, nAvgBytesPerSec = (uint)rate * 2 };
        var r = waveOutOpen(out _h, WAVE_MAPPER, ref fmt, 0, 0, CALLBACK_NULL);
        if (r != 0) { _h = 0; Log.Warn("tts", $"could not open the speakers (waveOutOpen error {r})"); return false; }
        _rate = rate;
        Log.Info("tts", $"speaker stream open at {rate} Hz");
        return true;
    }

    private void CloseDevice()
    {
        if (_h == 0) return;
        waveOutReset(_h);
        foreach (var c in _queue) Free(c);
        _queue.Clear();
        waveOutClose(_h);
        _h = 0;
    }

    /// <summary>Frees buffers the device has finished playing.</summary>
    private void Reap()
    {
        for (var i = _queue.Count - 1; i >= 0; i--)
        {
            var hdr = Marshal.PtrToStructure<WAVEHDR>(_queue[i].Header);
            if ((hdr.dwFlags & WHDR_DONE) == 0) continue;
            Free(_queue[i]);
            _queue.RemoveAt(i);
        }
    }

    private void Free(Chunk c)
    {
        waveOutUnprepareHeader(_h, c.Header, Marshal.SizeOf<WAVEHDR>());
        Marshal.FreeHGlobal(c.Header);
        Marshal.FreeHGlobal(c.Data);
    }

    private static double[] Loudness(byte[] pcm)
    {
        var n = pcm.Length / 2;
        var levels = new double[Math.Max(1, (n + LevelWindow - 1) / LevelWindow)];
        for (var w = 0; w < levels.Length; w++)
        {
            double sum = 0; var count = 0;
            for (var i = w * LevelWindow; i < Math.Min(n, (w + 1) * LevelWindow); i++) { var s = (short)(pcm[2 * i] | (pcm[2 * i + 1] << 8)); sum += (double)s * s; count++; }
            levels[w] = Math.Min(1, Math.Sqrt(sum / Math.Max(1, count)) / 7000);
        }
        return levels;
    }

    public void Dispose() { lock (_gate) CloseDevice(); }
}
