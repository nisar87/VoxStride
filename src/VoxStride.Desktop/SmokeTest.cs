using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using VoxStride.Contracts;

namespace VoxStride.Desktop;

/// <summary>
/// Headless end-to-end check used by CI after installation: builds the real composition root (SQLite, policy,
/// broker, tools, router), runs typed commands through the same commit → route → broker → verify path the UI uses,
/// and writes a JSON evidence file. Never touches the user's Desktop (no organize commands) and makes no network calls.
/// </summary>
public static class SmokeTest
{
    private sealed class DenyApprovals : IUserApproval
    {
        public Task<Approval?> RequestAsync(ApprovalRequest request, CancellationToken ct) => Task.FromResult<Approval?>(null);
    }

    public static async Task<int> RunAsync(string[] args)
    {
        var outPath = args.SkipWhile(a => a != "--out").Skip(1).FirstOrDefault() ?? Path.Combine(Path.GetTempPath(), "voxstride-smoke.json");
        var results = new System.Collections.Generic.List<object>();
        var ok = true;
        var settings = new AppSettings { ActiveProvider = null, SpeakResults = false, WakeWordEnabled = false };
        var sw = Stopwatch.StartNew();
        DiagnosticsLog.Start(includeContent: true); // exercises the real file logger on the installed build
        Log.Info("app", "smoke test starting");
        try
        {
            await using var host = new AppHost(settings, new DenyApprovals(), enableSpeech: false);
            await host.Apps.BuildAsync();
            var startupMs = sw.Elapsed.TotalMilliseconds;
            foreach (var (text, expectSuccess) in new[] { ("how much space is left on C", true), ("list my drives", true), ("write a poem about rain", false) })
            {
                var t = Stopwatch.StartNew();
                var cmd = new CommandEnvelope(Guid.NewGuid(), Guid.NewGuid(), text, Core.Execution.RunAuthority.HashTranscript(text), "en-US", [], DateTimeOffset.UtcNow, InputMode.Typed, null, null);
                var r = await host.Runs.RunAsync(cmd, CancellationToken.None);
                var pass = r.Success == expectSuccess;
                ok &= pass;
                results.Add(new { text, r.Success, r.SpokenSummary, ms = t.Elapsed.TotalMilliseconds, pass });
            }
            // Section 11 latency: commit → verified result for a direct native command, 30 runs, p50/p95.
            var samples = new System.Collections.Generic.List<double>();
            for (var i = 0; i < 30; i++)
            {
                const string text = "how much space is left on C";
                var cmd = new CommandEnvelope(Guid.NewGuid(), Guid.NewGuid(), text, Core.Execution.RunAuthority.HashTranscript(text), "en-US", [], DateTimeOffset.UtcNow, InputMode.Typed, null, null);
                var t = Stopwatch.StartNew();
                var r = await host.Runs.RunAsync(cmd, CancellationToken.None);
                t.Stop();
                if (r.Success) samples.Add(t.Elapsed.TotalMilliseconds);
                if (host.Runs.LastLatency?.Between("t_commit", "t_verified") is { } v) samples[^1] = v;
            }
            samples.Sort();
            double P(double q) => samples.Count == 0 ? double.NaN : samples[Math.Min(samples.Count - 1, (int)Math.Ceiling(q * samples.Count) - 1)];
            var latency = new { runs = samples.Count, p50Ms = Math.Round(P(0.50), 1), p95Ms = Math.Round(P(0.95), 1), maxMs = Math.Round(samples.LastOrDefault(), 1), targetP95Ms = 2000 };
            ok &= samples.Count == 30 && P(0.95) < 2000;

            // Cancellation: a hard stop must take effect quickly (target 500 ms).
            var slow = new CommandEnvelope(Guid.NewGuid(), Guid.NewGuid(), "write a long poem about rain", Core.Execution.RunAuthority.HashTranscript("write a long poem about rain"), "en-US", [], DateTimeOffset.UtcNow, InputMode.Typed, null, null);
            var running = host.Runs.RunAsync(slow, CancellationToken.None);
            var cancelWatch = Stopwatch.StartNew();
            await host.Runs.CancelActiveAsync("smoke-test");
            await running;
            var cancelMs = Math.Round(cancelWatch.Elapsed.TotalMilliseconds, 1);
            ok &= cancelMs < 500;

            // The real diagnostics file must contain the full trail of those runs.
            DiagnosticsLog.Flush(TimeSpan.FromSeconds(5));
            string logText;
            using (var fs = new FileStream(DiagnosticsLog.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(fs)) logText = reader.ReadToEnd();
            var logChecks = new
            {
                session = logText.Contains("smoke test starting", StringComparison.Ordinal),
                route = logText.Contains("route=Direct", StringComparison.Ordinal),
                toolStep = logText.Contains("system_get_free_space -> outcome=Success", StringComparison.Ordinal),
                runEnd = logText.Contains("end success=True", StringComparison.Ordinal),
                noProvider = logText.Contains("provider=NONE", StringComparison.Ordinal),
                format = System.Text.RegularExpressions.Regex.IsMatch(logText, @"^\d{4}-\d\d-\d\d \d\d:\d\d:\d\d\.\d{3} [+-]\d\d:\d\d (DBG|INF|WRN|ERR) ", System.Text.RegularExpressions.RegexOptions.Multiline),
            };
            var logOk = logChecks.session && logChecks.route && logChecks.toolStep && logChecks.runEnd && logChecks.noProvider && logChecks.format;
            ok &= logOk;
            using var proc = Process.GetCurrentProcess();
            proc.Refresh();
            var evidence = new
            {
                caseId = "SMOKE-01",
                os = Environment.OSVersion.ToString(),
                dotnet = Environment.Version.ToString(),
                startupMs,
                workingSetMb = proc.WorkingSet64 / 1048576.0,
                apps = host.Apps.Snapshot().Count,
                hasNotepad = host.Apps.Resolve("notepad").App is not null,
                results,
                latency,
                cancelMs,
                log = new { path = DiagnosticsLog.FilePath, bytes = logText.Length, checks = logChecks, passed = logOk },
                status = ok ? "passed" : "failed",
            };
            await File.WriteAllTextAsync(outPath, JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }));
            return ok ? 0 : 1;
        }
        catch (Exception ex)
        {
            await File.WriteAllTextAsync(outPath, JsonSerializer.Serialize(new { status = "error", error = ex.ToString() }));
            return 2;
        }
    }
}
