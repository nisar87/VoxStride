using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using VoxStride.Contracts;

namespace VoxStride.Desktop;

/// <summary>
/// Diagnostics log at %LocalAppData%\VoxStride\logs\voxstride.log, kept only on this PC.
/// <para>
/// Every component writes through <see cref="Log"/>; this class installs the file sink. Lines are queued and written
/// by one background thread, so logging never blocks the microphone or the UI. Files rotate at 5 MB and the last five
/// are kept (voxstride.log, voxstride.1.log … voxstride.4.log). API keys and tokens are redacted before anything is
/// written. What the user says and VoxStride's replies are included only while "Include what I say in the
/// diagnostics log" is on (Privacy). Audio is never written.
/// </para>
/// Line format: <c>2026-11-27 07:01:02.123 +05:00 INF voice  [t12] message</c>, with exception stack traces indented below.
/// </summary>
public static class DiagnosticsLog
{
    public const long MaxFileBytes = 5_000_000;
    public const int FilesKept = 5;
    public static string Folder => Path.Combine(AppSettings.Folder, "logs");
    public static string FilePath => Path.Combine(Folder, "voxstride.log");
    public static string SessionId { get; } = Guid.NewGuid().ToString("N")[..6];

    private static FileSink? _sink;

    /// <summary>Starts the file sink (idempotent).</summary>
    public static void Start(bool includeContent)
    {
        Log.IncludeContent = includeContent;
        if (_sink is not null) return;
        _sink = new FileSink(Folder);
        Log.Sink = _sink;
    }

    /// <summary>Compatibility helper for app-level messages.</summary>
    public static void Write(string message) => Log.Info("app", message);

    /// <summary>Waits (bounded) until queued lines are on disk — call before exit and from crash handlers.</summary>
    public static void Flush(TimeSpan timeout) => _sink?.Flush(timeout);

    /// <summary>All log files, newest first.</summary>
    public static IReadOnlyList<string> Files()
    {
        var list = new List<string>();
        if (File.Exists(FilePath)) list.Add(FilePath);
        for (var i = 1; i < FilesKept; i++) { var p = RotatedPath(Folder, i); if (File.Exists(p)) list.Add(p); }
        return list;
    }

    internal static string RotatedPath(string folder, int i) => Path.Combine(folder, $"voxstride.{i}.log");

    private sealed class FileSink : ILogSink
    {
        private readonly string _folder;
        private readonly Channel<LogEntry> _queue = Channel.CreateBounded<LogEntry>(new BoundedChannelOptions(20_000) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
        private readonly Thread _thread;
        private readonly ManualResetEventSlim _idle = new(true);
        private long _pending;
        private StreamWriter? _writer;
        private long _size;

        public FileSink(string folder)
        {
            _folder = folder;
            _thread = new Thread(Run) { IsBackground = true, Name = "VoxStride log writer", Priority = ThreadPriority.BelowNormal };
            _thread.Start();
        }

        public void Write(in LogEntry entry)
        {
            Interlocked.Increment(ref _pending);
            _idle.Reset();
            if (!_queue.Writer.TryWrite(entry)) Done();
        }

        private void Done(int n = 1) { if (Interlocked.Add(ref _pending, -n) <= 0) _idle.Set(); }

        public void Flush(TimeSpan timeout) => _idle.Wait(timeout);

        private void Run()
        {
            var reader = _queue.Reader;
            var sb = new StringBuilder(512);
            while (true)
            {
                var batch = 0;
                try
                {
                    if (!reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult()) return;
                    while (batch < 500 && reader.TryRead(out var e))
                    {
                        batch++;
                        sb.Clear();
                        Format(sb, e);
                        WriteLine(sb.ToString());
                    }
                    _writer?.Flush(); // a batch counts as done only once it is on disk
                }
                catch (Exception) { Thread.Sleep(200); /* disk problem: keep going, never crash the app */ }
                finally { if (batch > 0) Done(batch); }
            }
        }

        private static void Format(StringBuilder sb, in LogEntry e)
        {
            sb.Append(e.Time.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture)).Append(' ')
              .Append(e.Level switch { LogLevel.Debug => "DBG", LogLevel.Info => "INF", LogLevel.Warn => "WRN", _ => "ERR" }).Append(' ')
              .Append(e.Area.PadRight(6)).Append(" [t").Append(e.ThreadId.ToString(CultureInfo.InvariantCulture)).Append("] ")
              .Append(e.Message.ReplaceLineEndings(" "));
            if (e.Exception is { } ex)
                foreach (var line in ex.Split('\n')) sb.Append(Environment.NewLine).Append("    ").Append(line.TrimEnd('\r'));
        }

        private void WriteLine(string line)
        {
            if (_writer is null) Open();
            if (_size > MaxFileBytes) { Rotate(); Open(); }
            _writer!.WriteLine(line);
            _size += Encoding.UTF8.GetByteCount(line) + 2;
        }

        private void Open()
        {
            Directory.CreateDirectory(_folder);
            var path = Path.Combine(_folder, "voxstride.log");
            MigrateLegacy();
            var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            _writer = new StreamWriter(fs, new UTF8Encoding(false)) { AutoFlush = false };
            _size = fs.Length;
        }

        private void Rotate()
        {
            _writer?.Dispose();
            _writer = null;
            var current = Path.Combine(_folder, "voxstride.log");
            try
            {
                File.Delete(RotatedPath(_folder, FilesKept - 1));
                for (var i = FilesKept - 2; i >= 1; i--)
                    if (File.Exists(RotatedPath(_folder, i))) File.Move(RotatedPath(_folder, i), RotatedPath(_folder, i + 1), overwrite: true);
                File.Move(current, RotatedPath(_folder, 1), overwrite: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        /// <summary>The 0.2/0.3 log rotated to "voxstride.log.1"; keep it under the new name so old history stays readable.</summary>
        private void MigrateLegacy()
        {
            var legacy = Path.Combine(_folder, "voxstride.log.1");
            try { if (File.Exists(legacy) && !File.Exists(RotatedPath(_folder, 1))) File.Move(legacy, RotatedPath(_folder, 1)); }
            catch (IOException) { }
        }
    }
}
