using System.Diagnostics;
using System.Threading.Channels;
using VoxStride.Contracts;

namespace VoxStride.Core.Events;

public interface IEventSink
{
    void Publish(Guid runId, AgentState state, string phase, string message, string? stepId = null, int? completed = null, int? total = null, bool canCancel = true);
}

/// <summary>
/// Fan-out of task events to UI/speech subscribers via bounded channels. State and journal-relevant events are
/// never dropped (writer waits); subscribers that only render progress can use coalescing.
/// </summary>
public sealed class EventHub : IEventSink
{
    private readonly List<Channel<TaskEvent>> _subscribers = [];
    private readonly Lock _gate = new();
    private long _sequence;

    public ChannelReader<TaskEvent> Subscribe(int capacity = 512)
    {
        var ch = Channel.CreateBounded<TaskEvent>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = false,
        });
        lock (_gate) _subscribers.Add(ch);
        return ch.Reader;
    }

    public event Action<TaskEvent>? Published;

    public void Publish(Guid runId, AgentState state, string phase, string message, string? stepId = null, int? completed = null, int? total = null, bool canCancel = true)
    {
        var ev = new TaskEvent(runId, Interlocked.Increment(ref _sequence), Stopwatch.GetTimestamp(), state, phase, message, stepId, completed, total, canCancel);
        Channel<TaskEvent>[] subs;
        lock (_gate) subs = [.. _subscribers];
        foreach (var s in subs) s.Writer.TryWrite(ev);
        Published?.Invoke(ev);
    }
}

/// <summary>Monotonic latency marks (spec §13.3). Raw timings stay local.</summary>
public sealed class LatencyRecorder
{
    private readonly Dictionary<string, long> _marks = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public void Mark(string name)
    {
        var t = Stopwatch.GetTimestamp();
        lock (_gate) _marks.TryAdd(name, t);
    }

    public double? Between(string from, string to)
    {
        lock (_gate)
            return _marks.TryGetValue(from, out var a) && _marks.TryGetValue(to, out var b)
                ? Stopwatch.GetElapsedTime(a, b).TotalMilliseconds : null;
    }

    public IReadOnlyDictionary<string, long> Snapshot() { lock (_gate) return new Dictionary<string, long>(_marks); }
}

/// <summary>Synchronous IProgress (System.Progress posts asynchronously, which would let cancellation race ahead of reports).</summary>
public sealed class SyncProgress<T>(Action<T> handler) : IProgress<T>
{
    public void Report(T value) => handler(value);
}
