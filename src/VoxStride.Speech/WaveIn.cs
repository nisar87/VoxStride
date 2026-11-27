using System.Runtime.InteropServices;
using VoxStride.Contracts;
using VoxStride.Core.Voice;

namespace VoxStride.Speech;

/// <summary>
/// Microphone capture through winmm waveIn (16 kHz, mono, 16-bit), shared-mode so other apps keep working.
/// Normally opened only by <see cref="AudioHub"/>; consumers take leases from the hub.
/// </summary>
public sealed class WaveInRecorder : IAudioCaptureDevice
{
    private const int CALLBACK_EVENT = 0x50000, WHDR_DONE = 1, BufferMs = 50, BufferCount = 8;
    public const int WaveMapper = -1;
    private const uint DRVM_MAPPER_PREFERRED_GET = 0x2000 + 21;
    // WAVE_MAPPER is ((UINT)-1) passed as UINT_PTR: on x64 that is 0x00000000FFFFFFFF, not a sign-extended -1.
    private static nint DeviceArg(int id) => id < 0 ? unchecked((nint)0xFFFFFFFFu) : id;

    [StructLayout(LayoutKind.Sequential)]
    private struct WAVEFORMATEX { public ushort wFormatTag, nChannels; public uint nSamplesPerSec, nAvgBytesPerSec; public ushort nBlockAlign, wBitsPerSample, cbSize; }

    [StructLayout(LayoutKind.Sequential)]
    private struct WAVEHDR { public nint lpData; public uint dwBufferLength, dwBytesRecorded; public nint dwUser; public uint dwFlags, dwLoops; public nint lpNext, reserved; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WAVEINCAPS { public ushort wMid, wPid; public uint vDriverVersion; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szPname; public uint dwFormats; public ushort wChannels, wReserved1; }

    [DllImport("winmm.dll")] private static extern int waveInOpen(out nint h, nint device, ref WAVEFORMATEX fmt, nint callback, nint instance, int flags);
    [DllImport("winmm.dll")] private static extern int waveInPrepareHeader(nint h, nint hdr, int size);
    [DllImport("winmm.dll")] private static extern int waveInUnprepareHeader(nint h, nint hdr, int size);
    [DllImport("winmm.dll")] private static extern int waveInAddBuffer(nint h, nint hdr, int size);
    [DllImport("winmm.dll")] private static extern int waveInStart(nint h);
    [DllImport("winmm.dll")] private static extern int waveInReset(nint h);
    [DllImport("winmm.dll")] private static extern int waveInClose(nint h);
    [DllImport("winmm.dll")] private static extern int waveInGetNumDevs();
    [DllImport("winmm.dll")] private static extern int waveInMessage(nint h, uint msg, out uint dw1, out uint dw2);
    [DllImport("winmm.dll", CharSet = CharSet.Unicode, EntryPoint = "waveInGetDevCapsW")] private static extern int waveInGetDevCaps(nint id, ref WAVEINCAPS caps, int size);

    private readonly Lock _gate = new();
    private nint _handle;
    private readonly List<nint> _headers = [];
    private AutoResetEvent? _signal;
    private Thread? _thread;
    private volatile bool _running;
    private bool _errorReported;

    /// <param name="deviceId">waveIn device index, or <see cref="WaveMapper"/> for the Windows default microphone.</param>
    public WaveInRecorder(int deviceId = WaveMapper)
    {
        DeviceId = deviceId;
        Label = DescribeDevice(deviceId);
    }

    public int DeviceId { get; }
    public string Label { get; }
    public int SampleRate => 16000;
    public event Action<byte[]>? DataAvailable;
    public event Action<string>? Error;

    /// <summary>Recording devices in waveIn order (index = device id). Windows truncates names to 31 characters.</summary>
    public static IReadOnlyList<string> Devices()
    {
        var list = new List<string>();
        var n = waveInGetNumDevs();
        for (var i = 0; i < n; i++)
        {
            var caps = new WAVEINCAPS();
            list.Add(waveInGetDevCaps(i, ref caps, Marshal.SizeOf<WAVEINCAPS>()) == 0 ? caps.szPname : $"Microphone {i + 1}");
        }
        return list;
    }

    /// <summary>The waveIn index Windows currently uses as the default recording device, or -1 if unknown.</summary>
    public static int DefaultDeviceIndex()
    {
        try { return waveInMessage(DeviceArg(WaveMapper), DRVM_MAPPER_PREFERRED_GET, out var id, out _) == 0 && id < (uint)waveInGetNumDevs() ? (int)id : -1; }
        catch (Exception) { return -1; }
    }

    /// <summary>"HyperX…" for a specific device; "Windows default (HyperX…)" for the default.</summary>
    public static string DescribeDevice(int deviceId)
    {
        var devices = Devices();
        if (deviceId >= 0) return deviceId < devices.Count ? devices[deviceId] : $"Microphone {deviceId + 1}";
        var d = DefaultDeviceIndex();
        return d >= 0 && d < devices.Count ? $"Windows default ({devices[d]})" : "Windows default microphone";
    }

    /// <summary>Device id for a saved microphone name (prefix match, since Windows truncates names); <see cref="WaveMapper"/> if not found.</summary>
    public static int ResolveDevice(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return WaveMapper;
        var devices = Devices();
        for (var i = 0; i < devices.Count; i++)
            if (devices[i].Equals(name, StringComparison.OrdinalIgnoreCase) || name.StartsWith(devices[i], StringComparison.OrdinalIgnoreCase)) return i;
        return WaveMapper;
    }

    public static bool HasMicrophone() => waveInGetNumDevs() > 0;

    /// <summary>Listens to every microphone at once for a moment and returns each device's peak level.</summary>
    public static IReadOnlyList<(string Name, double Peak)> ProbeAll(TimeSpan duration)
    {
        var names = Devices();
        var probes = new List<(string Name, WaveInRecorder Rec, double[] Peak)>();
        for (var i = 0; i < names.Count; i++)
        {
            var r = new WaveInRecorder(i);
            var peak = new double[1];
            r.DataAvailable += d => peak[0] = Math.Max(peak[0], UtteranceSegmenter.Rms(d));
            try { r.Start(); probes.Add((names[i], r, peak)); } catch (Exception) { r.Dispose(); }
        }
        Thread.Sleep(duration);
        foreach (var p in probes) p.Rec.Dispose();
        return probes.Select(p => (p.Name, p.Peak[0])).ToList();
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_running) return;
            if (!HasMicrophone()) throw new InvalidOperationException("No microphone was found. Plug one in, or type your instruction.");
            var fmt = new WAVEFORMATEX { wFormatTag = 1, nChannels = 1, nSamplesPerSec = (uint)SampleRate, wBitsPerSample = 16, nBlockAlign = 2, nAvgBytesPerSec = (uint)SampleRate * 2 };
            _signal = new AutoResetEvent(false);
            var rc = waveInOpen(out _handle, DeviceArg(DeviceId), ref fmt, _signal.SafeWaitHandle.DangerousGetHandle(), 0, CALLBACK_EVENT);
            if (rc != 0)
            {
                Log.Warn("mic", $"waveInOpen failed device={DeviceId} \"{Label}\" code={rc}");
                _signal.Dispose(); _signal = null; _handle = 0;
                throw new InvalidOperationException(rc switch
                {
                    4 => $"\"{Label}\" is busy or blocked. Check Windows Settings › Privacy & security › Microphone allows desktop apps.",
                    2 or 6 => $"\"{Label}\" isn't available any more. Check it's plugged in, or pick another microphone in Voice settings.",
                    _ => $"Could not open \"{Label}\" (code {rc}). Check it's connected and allowed in Windows microphone privacy settings.",
                });
            }
            var size = Marshal.SizeOf<WAVEHDR>();
            var bytes = SampleRate * 2 * BufferMs / 1000;
            for (var i = 0; i < BufferCount; i++)
            {
                var hdr = Marshal.AllocHGlobal(size);
                Marshal.StructureToPtr(new WAVEHDR { lpData = Marshal.AllocHGlobal(bytes), dwBufferLength = (uint)bytes }, hdr, false);
                waveInPrepareHeader(_handle, hdr, size);
                waveInAddBuffer(_handle, hdr, size);
                _headers.Add(hdr);
            }
            _errorReported = false;
            _running = true;
            _thread = new Thread(Pump) { IsBackground = true, Name = "VoxStride mic" };
            _thread.Start();
            rc = waveInStart(_handle);
            if (rc != 0) { StopCore(); throw new InvalidOperationException($"Could not start recording from \"{Label}\" (code {rc})."); }
        }
    }

    private void Pump()
    {
        var size = Marshal.SizeOf<WAVEHDR>();
        while (_running)
        {
            _signal!.WaitOne(200);
            foreach (var hdr in _headers)
            {
                if (!_running) break;
                var h = Marshal.PtrToStructure<WAVEHDR>(hdr);
                if ((h.dwFlags & WHDR_DONE) == 0) continue;
                if (h.dwBytesRecorded > 0)
                {
                    var data = new byte[h.dwBytesRecorded];
                    Marshal.Copy(h.lpData, data, 0, data.Length);
                    try { DataAvailable?.Invoke(data); } catch (Exception ex) { ReportError("audio handler failed: " + ex.Message); }
                }
                h.dwFlags &= ~(uint)WHDR_DONE;
                h.dwBytesRecorded = 0;
                Marshal.StructureToPtr(h, hdr, false);
                if (_running)
                {
                    var rc = waveInAddBuffer(_handle, hdr, size);
                    if (rc != 0) ReportError($"\"{Label}\" stopped delivering audio (code {rc}). It may have been unplugged.");
                }
            }
        }
    }

    private void ReportError(string message)
    {
        Log.Warn("mic", $"capture problem on \"{Label}\": {message}");
        if (_errorReported) return;
        _errorReported = true;
        Error?.Invoke(message);
    }

    public void Stop() { lock (_gate) StopCore(); }

    private void StopCore()
    {
        if (!_running && _handle == 0) return;
        _running = false;
        _signal?.Set();
        if (_thread is not null && _thread != Thread.CurrentThread) _thread.Join(1000);
        _thread = null;
        if (_handle != 0)
        {
            waveInReset(_handle);
            var size = Marshal.SizeOf<WAVEHDR>();
            foreach (var hdr in _headers)
            {
                waveInUnprepareHeader(_handle, hdr, size);
                Marshal.FreeHGlobal(Marshal.PtrToStructure<WAVEHDR>(hdr).lpData);
                Marshal.FreeHGlobal(hdr);
            }
            _headers.Clear();
            waveInClose(_handle);
            _handle = 0;
        }
        _signal?.Dispose();
        _signal = null;
    }

    public void Dispose() => Stop();
}

/// <summary>
/// Watches what a listening session has actually received and explains a microphone problem once, in plain words.
/// "No buffers at all" is a real fault (reported as an error); "only silence" may just be a quiet room or a headset
/// that gates silence to zero, so it is a gentle hint and never interrupts the instruction.
/// </summary>
internal sealed class MicWatchdog : IDisposable
{
    public static readonly TimeSpan NoAudioAfter = TimeSpan.FromSeconds(4);
    public static readonly TimeSpan SilenceHintAfter = TimeSpan.FromSeconds(10);
    private readonly AudioLease _lease;
    private readonly Action<string> _error;
    private readonly Action<string> _hint;
    private readonly Timer _timer;
    private int _done;

    public MicWatchdog(AudioLease lease, Action<string> error, Action<string> hint)
    {
        _lease = lease;
        _error = error;
        _hint = hint;
        _timer = new Timer(_ => Check(), null, NoAudioAfter, TimeSpan.FromSeconds(2));
    }

    private void Check()
    {
        if (Volatile.Read(ref _done) != 0) return;
        if (_lease.IsReleased) { Finish(); return; }
        if (_lease.Buffers == 0 && _lease.Elapsed >= NoAudioAfter)
        {
            if (Finish())
            {
                Log.Warn("mic", $"watchdog: no audio buffers from \"{_lease.DeviceLabel}\" after {_lease.Elapsed.TotalSeconds:0.0}s ({_lease.Purpose})");
                _error($"\"{_lease.DeviceLabel}\" is open but isn't sending any audio. Another app may be using it exclusively, or it was disconnected. Pick another microphone in Voice settings, or type your instruction");
            }
            return;
        }
        switch (_lease.Health(SilenceHintAfter))
        {
            case MicHealth.Ok: Finish(); break;
            case MicHealth.Silent:
                if (Finish())
                {
                    Log.Info("mic", $"watchdog: only silence from \"{_lease.DeviceLabel}\" for {_lease.Elapsed.TotalSeconds:0}s buffers={_lease.Buffers} peak={_lease.Peak:0}");
                    _hint(SilenceAdvice(_lease.DeviceLabel));
                }
                break;
        }
    }

    /// <summary>Stops watching; true only for the first caller, so each problem is reported once.</summary>
    private bool Finish()
    {
        if (Interlocked.Exchange(ref _done, 1) != 0) return false;
        _timer.Dispose();
        return true;
    }

    /// <summary>Names another microphone that does hear sound, if there is one.</summary>
    internal static string SilenceAdvice(string activeLabel)
    {
        try
        {
            var probe = WaveInRecorder.ProbeAll(TimeSpan.FromMilliseconds(1200));
            Log.Info("mic", "probe all microphones: " + string.Join(", ", probe.Select(p => $"\"{p.Name}\" peak={p.Peak:0}")));
            var alive = probe
                .Where(p => p.Peak >= MicHealthCheck.SilenceThreshold && !activeLabel.Contains(p.Name, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(p => p.Peak).Select(p => p.Name).ToList();
            if (alive.Count > 0)
                return $"I haven't heard any sound from \"{activeLabel}\" yet, but \"{alive[0]}\" is picking up sound. If that's the one you're speaking into, choose it under Settings › Voice › Microphone";
        }
        catch (Exception) { }
        return $"I haven't heard any sound from \"{activeLabel}\" yet. If you are speaking, check the microphone isn't muted";
    }

    public void Dispose() => Finish();
}
