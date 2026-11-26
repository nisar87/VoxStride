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
