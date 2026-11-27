using VoxStride.Core.Voice;
using Xunit;

namespace VoxStride.Tests;

/// <summary>
/// Regression tests for the "VoxStride never hears me" fault: SAPI ends recognition on any short read,
/// so the live stream must fill every request completely until audio genuinely ends.
/// </summary>
public sealed class PcmPipeStreamTests
{
    private static byte[] Bytes(int n, byte value = 7) => Enumerable.Repeat(value, n).ToArray();

    private static async Task<bool> CompletesWithin(Task t, int ms) => await Task.WhenAny(t, Task.Delay(ms)) == t;

    [Fact]
    public async Task ReadWaitsUntilTheWholeRequestIsAvailable()
    {
        var pipe = new PcmPipeStream(64 * 1024);
        var buffer = new byte[4800]; // larger than one 50 ms capture buffer (1600 bytes)
        var read = Task.Run(() => pipe.Read(buffer, 0, buffer.Length));
        pipe.Append(Bytes(1600));
        Assert.False(await CompletesWithin(read, 150)); // a short read here is what made SAPI think the audio had ended
        pipe.Append(Bytes(1600));
        Assert.False(await CompletesWithin(read, 150));
        pipe.Append(Bytes(1600));
        Assert.True(await CompletesWithin(read, 2000));
        Assert.Equal(4800, await read);
        Assert.Equal(4800, pipe.Position);
    }

    [Fact]
    public async Task ReadNeverReturnsShortWhileAudioIsLive()
    {
        var pipe = new PcmPipeStream(64 * 1024);
        var producer = Task.Run(async () =>
        {
            for (var i = 0; i < 40; i++) { pipe.Append(Bytes(1600, (byte)i)); await Task.Delay(2); }
        });
        var buffer = new byte[3000];
        for (var i = 0; i < 20; i++) Assert.Equal(3000, pipe.Read(buffer, 0, buffer.Length));
        await producer;
    }

    [Fact]
    public void PreservesOrderAcrossChunkBoundaries()
    {
        var pipe = new PcmPipeStream(4096);
        var source = Enumerable.Range(0, 3000).Select(i => (byte)(i % 251)).ToArray();
        for (var i = 0; i < source.Length; i += 700) pipe.Append(source.AsSpan(i, Math.Min(700, source.Length - i)));
        pipe.Complete();
        var output = new MemoryStream();
        var buffer = new byte[1000];
        int n;
        while ((n = pipe.Read(buffer, 0, buffer.Length)) > 0) output.Write(buffer, 0, n);
        Assert.True(source.AsSpan().SequenceEqual(output.ToArray()), "audio must come out in the same order");
    }

    [Fact]
    public void CompleteDeliversRemainingAudioThenEndOfStream()
    {
        var pipe = new PcmPipeStream(8192);
        pipe.Append(Bytes(1000));
        pipe.Complete();
        var buffer = new byte[4000];
        Assert.Equal(1000, pipe.Read(buffer, 0, buffer.Length));
        Assert.Equal(0, pipe.Read(buffer, 0, buffer.Length));
    }

    [Fact]
    public async Task CompleteUnblocksAWaitingReader()
    {
        var pipe = new PcmPipeStream(8192);
        var read = Task.Run(() => pipe.Read(new byte[2000], 0, 2000));
        Assert.False(await CompletesWithin(read, 100));
        pipe.Complete();
        Assert.True(await CompletesWithin(read, 2000));
        Assert.Equal(0, await read);
    }

    [Fact]
    public void StalledReaderDropsOldestAudioInsteadOfGrowing()
    {
        var pipe = new PcmPipeStream(4096);
        pipe.Append(Bytes(3000, 1));
        pipe.Append(Bytes(3000, 2));
        Assert.Equal(4096, pipe.Buffered);
        Assert.Equal(6000 - 4096, pipe.DroppedBytes);
        pipe.Complete();
        var buffer = new byte[4096];
        Assert.Equal(4096, pipe.Read(buffer, 0, buffer.Length));
        Assert.Equal(3000, buffer.Count(b => b == 2)); // newest audio kept
    }

    [Fact]
    public void ReportsUnknownLengthAsSapiExpects()
    {
        var pipe = new PcmPipeStream(4096);
        Assert.Equal(-1, pipe.Length); // System.Speech: -1 = no known end; throwing breaks SetInputToAudioStream
        Assert.False(pipe.CanSeek);
        pipe.Append(Bytes(100));
        pipe.Complete();
        _ = pipe.Read(new byte[100], 0, 100);
        Assert.Equal(100, pipe.Seek(0, SeekOrigin.Current));
    }

    [Fact]
    public async Task AbortDiscardsBufferedAudioAndUnblocksReader()
    {
        var pipe = new PcmPipeStream(8192);
        pipe.Append(Bytes(1000));
        var read = Task.Run(() => pipe.Read(new byte[4000], 0, 4000));
        Assert.False(await CompletesWithin(read, 100));
        pipe.Abort();
        Assert.True(await CompletesWithin(read, 2000));
        Assert.Equal(0, await read); // cancelled audio is never handed to the engine
        Assert.Equal(1000, pipe.DroppedBytes);
    }

    [Fact]
    public void AppendAfterCompleteIsIgnored()
    {
        var pipe = new PcmPipeStream(4096);
        pipe.Complete();
        pipe.Append(Bytes(100));
        Assert.Equal(0, pipe.Read(new byte[100], 0, 100));
    }
}

public sealed class AudioHubTests
{
    private sealed class FakeDevice(string label) : IAudioCaptureDevice
    {
        public string Label => label;
        public int SampleRate => 16000;
        public bool Running { get; private set; }
        public bool Disposed { get; private set; }
        public bool FailToStart { get; init; }
        public event Action<byte[]>? DataAvailable;
        public event Action<string>? Error;
        public void Start() { if (FailToStart) throw new InvalidOperationException("Could not open the microphone."); Running = true; }
        public void Stop() => Running = false;
        public void Dispose() { Running = false; Disposed = true; }
        public void Emit(short amplitude, int bytes = 1600)
        {
            var b = new byte[bytes];
            for (var i = 0; i < bytes / 2; i++) { var s = (short)(i % 2 == 0 ? amplitude : -amplitude); b[2 * i] = (byte)(s & 0xFF); b[2 * i + 1] = (byte)((s >> 8) & 0xFF); }
            DataAvailable?.Invoke(b);
        }
        public void Fail(string m) => Error?.Invoke(m);
    }

    [Fact]
    public void OpensOnceAndSharesTheDeviceBetweenLeases()
    {
        var opened = new List<FakeDevice>();
        var hub = new AudioHub(() => { var d = new FakeDevice("HyperX"); opened.Add(d); return d; });
        using var wake = hub.Acquire("wake");
        using var dictation = hub.Acquire("dictation");
        Assert.Single(opened);
        var got = 0;
        dictation.Data += _ => got++;
        opened[0].Emit(5000);
        Assert.Equal(1, got);
        Assert.Equal(1, wake.Buffers);
        Assert.Equal(1, dictation.Buffers);
    }

    [Fact]
    public void ClosesTheDeviceWhenTheLastLeaseIsReleased()
    {
        FakeDevice? device = null;
        var hub = new AudioHub(() => device = new FakeDevice("mic"));
        var a = hub.Acquire("wake");
        var b = hub.Acquire("handoff");
        a.Dispose();
        Assert.True(device!.Running); // handoff keeps the mic open across wake → dictation
        b.Dispose();
        Assert.True(device.Disposed);
        Assert.False(hub.IsOpen);
    }

    [Fact]
    public void DoubleDisposeOfALeaseIsHarmless()
    {
        var hub = new AudioHub(() => new FakeDevice("mic"));
        var a = hub.Acquire("x");
        var b = hub.Acquire("y");
        a.Dispose(); a.Dispose();
        Assert.Equal(1, hub.LeaseCount);
        Assert.True(hub.IsOpen);
        b.Dispose();
    }

    [Fact]
    public void ReleasedLeaseStopsReceivingAudio()
    {
        FakeDevice? device = null;
        var hub = new AudioHub(() => device = new FakeDevice("mic"));
        using var keep = hub.Acquire("keep");
        var gone = hub.Acquire("gone");
        var got = 0;
        gone.Data += _ => got++;
        gone.Dispose();
        device!.Emit(5000);
        Assert.Equal(0, got);
        Assert.Equal(1, keep.Buffers);
    }

    [Fact]
    public void FailedOpenThrowsAndLeavesNothingOpen()
    {
        FakeDevice? device = null;
        var hub = new AudioHub(() => device = new FakeDevice("mic") { FailToStart = true });
        Assert.Throws<InvalidOperationException>(() => hub.Acquire("dictation"));
        Assert.True(device!.Disposed);
        Assert.False(hub.IsOpen);
        Assert.Equal(0, hub.LeaseCount);
    }

    [Fact]
    public void ReopenSwitchesDeviceAndKeepsLeasesFlowing()
    {
        var devices = new List<FakeDevice>();
        var name = "AUKEY";
        var hub = new AudioHub(() => { var d = new FakeDevice(name); devices.Add(d); return d; });
        using var lease = hub.Acquire("dictation");
        name = "HyperX";
        hub.Reopen();
        Assert.True(devices[0].Disposed);
        Assert.Equal("HyperX", lease.DeviceLabel);
        devices[1].Emit(4000);
        Assert.Equal(1, lease.Buffers);
    }

    [Fact]
    public void LogsOpenLeaseSummaryAndClose()
    {
        var log = new List<string>();
        FakeDevice? device = null;
        var hub = new AudioHub(() => device = new FakeDevice("HyperX"), log.Add);
        var lease = hub.Acquire("dictation");
        device!.Emit(3000);
        lease.Dispose();
        Assert.Contains(log, l => l.StartsWith("mic-open device=\"HyperX\"", StringComparison.Ordinal));
        Assert.Contains(log, l => l.Contains("mic-lease purpose=dictation", StringComparison.Ordinal) && l.Contains("buffers=1", StringComparison.Ordinal));
        Assert.Contains(log, l => l.StartsWith("mic-close device=\"HyperX\" reason=no listeners", StringComparison.Ordinal));
    }

    [Fact]
    public void DeviceErrorsAreForwardedAndLogged()
    {
        var log = new List<string>();
        FakeDevice? device = null;
        var hub = new AudioHub(() => device = new FakeDevice("mic"), log.Add);
        string? error = null;
        hub.DeviceError += m => error = m;
        using var lease = hub.Acquire("dictation");
        device!.Fail("unplugged");
        Assert.Equal("unplugged", error);
        Assert.Contains("mic-error: unplugged", log);
    }

    [Theory]
    [InlineData(0, 0, 7000, MicHealth.NoAudio)]
    [InlineData(120, 5, 7000, MicHealth.Silent)]
    [InlineData(120, 900, 7000, MicHealth.Ok)]
    [InlineData(10, 2, 1000, MicHealth.Unknown)]
    [InlineData(10, 800, 500, MicHealth.Ok)]
    public void HealthDistinguishesNoAudioFromSilence(long buffers, double peak, int elapsedMs, MicHealth expected) =>
        Assert.Equal(expected, MicHealthCheck.Evaluate(buffers, peak, TimeSpan.FromMilliseconds(elapsedMs), TimeSpan.FromSeconds(6)));
}
