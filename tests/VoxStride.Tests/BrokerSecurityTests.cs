using System.Text.Json.Nodes;
using VoxStride.Contracts;
using VoxStride.Core.Execution;
using Xunit;

namespace VoxStride.Tests;

/// <summary>S01/B03/P02 — the broker blocks anything not authorized by a committed user command.</summary>
public sealed class BrokerSecurityTests : IDisposable
{
    private readonly Harness _h = new();
    public void Dispose() => _h.Dispose();

    [Fact]
    public async Task ValidReadSucceedsAndIsVerified()
    {
        var auth = _h.Mint();
        var r = await _h.Call(auth, "system_get_free_space", new JsonObject { ["driveLetter"] = "C" });
        Assert.Equal(ToolOutcome.Success, r.Outcome);
        Assert.True(r.Verified);
        Assert.Equal(123_456_789_000L, r.ObservedState!["freeBytes"]!.GetValue<long>());
    }

    [Fact]
    public async Task RevokedRunIsBlocked()
    {
        var auth = _h.Mint();
        _h.Authority.Revoke(auth.RunId);
        var r = await _h.Call(auth, "system_get_free_space", new JsonObject { ["driveLetter"] = "C" });
        Assert.Equal("auth_revoked", r.ErrorCode);
    }

    [Fact]
    public async Task OlderRunBecomesStaleWhenNewCommandCommits()
    {
        var first = _h.Mint();
        _ = _h.Mint();
        var r = await _h.Call(first, "system_get_free_space", new JsonObject { ["driveLetter"] = "C" });
        Assert.Equal(ToolOutcome.Failure, r.Outcome);
        Assert.StartsWith("auth_", r.ErrorCode);
    }

    [Fact]
    public async Task ExpiredAuthorizationIsBlocked()
    {
        var auth = _h.Mint();
        _h.Time.Advance(TimeSpan.FromHours(1));
        var r = await _h.Call(auth, "system_get_free_space", new JsonObject { ["driveLetter"] = "C" });
        Assert.Equal("auth_expired", r.ErrorCode);
    }

    [Fact]
    public void TamperedTranscriptCannotBeMinted()
    {
        var cmd = new CommandEnvelope(Guid.NewGuid(), Guid.NewGuid(), "organize desktop", RunAuthority.HashTranscript("check drive"), "en", [], DateTimeOffset.UtcNow, InputMode.Voice, null, null);
        Assert.Throws<InvalidOperationException>(() => _h.Authority.Mint(cmd, ["system_get_free_space"], TimeSpan.FromMinutes(1)));
    }

    [Theory]
    [InlineData("shell_execute")]
    [InlineData("powershell_run")]
    [InlineData("registry_write")]
    public async Task UnknownOrArbitraryToolsAreBlocked(string tool)
    {
        var auth = _h.Mint();
        var r = await _h.Call(auth, tool, new JsonObject { ["command"] = "format c:" });
        Assert.Equal("unknown_tool", r.ErrorCode);
    }

    [Fact]
    public async Task UnknownFieldsAndBadPatternsAreRejected()
    {
        var auth = _h.Mint();
        var extra = await _h.Call(auth, "system_get_free_space", new JsonObject { ["driveLetter"] = "C", ["path"] = "C:\\Windows" });
        Assert.Equal("schema_invalid", extra.ErrorCode);
        var bad = await _h.Call(auth, "system_get_free_space", new JsonObject { ["driveLetter"] = "C:\\..\\" });
        Assert.Equal("schema_invalid", bad.ErrorCode);
        var missing = await _h.Call(auth, "system_get_free_space", []);
        Assert.Equal("schema_invalid", missing.ErrorCode);
    }

    [Fact]
    public async Task ToolOutsideRunScopeIsBlocked()
    {
        var auth = _h.Mint("system_get_free_space");
        _h.Desktop.File("a.pdf");
        var r = await _h.Call(auth, "desktop_preview_organization", new JsonObject { ["folder"] = "desktop" });
        Assert.Equal("tool_not_allowed", r.ErrorCode);
    }

    [Fact]
    public async Task ModerateActionNeedsManifestBoundApproval_DeclineChangesNothing()
    {
        var src = _h.Desktop.File("report.pdf");
        var auth = _h.Mint();
        var p = await _h.Call(auth, "desktop_preview_organization", new JsonObject { ["folder"] = "desktop" });
        _h.Approvals.Approve = false;
        var r = await _h.Call(auth, "desktop_apply_organization", new JsonObject { ["manifestId"] = p.ObservedState!["manifestId"]!.GetValue<string>() });
        Assert.Equal("approval_declined", r.ErrorCode);
        Assert.True(File.Exists(src));
        Assert.Single(_h.Approvals.Requests);
    }

    [Fact]
    public async Task ApprovalForDifferentManifestIsRejected()
    {
        var src = _h.Desktop.File("report.pdf");
        var auth = _h.Mint();
        var p = await _h.Call(auth, "desktop_preview_organization", new JsonObject { ["folder"] = "desktop" });
        _h.Approvals.Override = req => new Approval(Guid.NewGuid(), req.RunId, "FORGED-DIGEST", req.Operation, DateTimeOffset.UtcNow.AddMinutes(5), "x");
        var r = await _h.Call(auth, "desktop_apply_organization", new JsonObject { ["manifestId"] = p.ObservedState!["manifestId"]!.GetValue<string>() });
        Assert.Equal("approval_mismatch", r.ErrorCode);
        Assert.True(File.Exists(src));
    }

    [Fact]
    public async Task ApprovalForOtherRunIsRejected()
    {
        var src = _h.Desktop.File("report.pdf");
        var auth = _h.Mint();
        var p = await _h.Call(auth, "desktop_preview_organization", new JsonObject { ["folder"] = "desktop" });
        _h.Approvals.Override = req => new Approval(Guid.NewGuid(), Guid.NewGuid(), req.ManifestDigest, req.Operation, DateTimeOffset.UtcNow.AddMinutes(5), "x");
        var r = await _h.Call(auth, "desktop_apply_organization", new JsonObject { ["manifestId"] = p.ObservedState!["manifestId"]!.GetValue<string>() });
        Assert.Equal("approval_mismatch", r.ErrorCode);
        Assert.True(File.Exists(src));
    }

    [Fact]
    public async Task CancelWhileAwaitingApprovalPreventsEffect()
    {
        var src = _h.Desktop.File("report.pdf");
        var auth = _h.Mint();
        var p = await _h.Call(auth, "desktop_preview_organization", new JsonObject { ["folder"] = "desktop" });
        _h.Approvals.Override = req => { _h.Authority.Revoke(auth.RunId); return new Approval(Guid.NewGuid(), req.RunId, req.ManifestDigest, req.Operation, DateTimeOffset.UtcNow.AddMinutes(5), "x"); };
        var r = await _h.Call(auth, "desktop_apply_organization", new JsonObject { ["manifestId"] = p.ObservedState!["manifestId"]!.GetValue<string>() });
        Assert.Equal("auth_revoked", r.ErrorCode);
        Assert.True(File.Exists(src));
    }

    [Fact]
    public async Task ReplayedSideEffectIsNotRepeated()
    {
        _h.Desktop.File("a.pdf");
        var auth = _h.Mint();
        var p = await _h.Call(auth, "desktop_preview_organization", new JsonObject { ["folder"] = "desktop" });
        var args = new JsonObject { ["manifestId"] = p.ObservedState!["manifestId"]!.GetValue<string>() };
        var first = await _h.Call(auth, "desktop_apply_organization", args, key: "same-key");
        var second = await _h.Call(auth, "desktop_apply_organization", args, key: "same-key");
        Assert.Equal(ToolOutcome.Success, first.Outcome);
        Assert.Equal("duplicate", second.ErrorCode);
    }

    [Fact]
    public async Task StandingRuleCoversOnlyReversibleMovesNotCleanup()
    {
        using var h = new Harness(new AlwaysRule());
        h.Desktop.File("a.pdf");
        h.Temp.File("old.tmp", "zzz", DateTime.UtcNow.AddDays(-5));
        var auth = h.Mint();
        var p = await h.Call(auth, "desktop_preview_organization", new JsonObject { ["folder"] = "desktop" });
        var a = await h.Call(auth, "desktop_apply_organization", new JsonObject { ["manifestId"] = p.ObservedState!["manifestId"]!.GetValue<string>() });
        Assert.Equal(ToolOutcome.Success, a.Outcome);
        Assert.Empty(h.Approvals.Requests); // covered by rule

        var cp = await h.Call(auth, "cleanup_preview_temp");
        h.Approvals.Approve = false;
        var ca = await h.Call(auth, "cleanup_apply_temp", new JsonObject { ["manifestId"] = cp.ObservedState!["manifestId"]!.GetValue<string>(), ["mode"] = "permanent" });
        Assert.Equal("approval_declined", ca.ErrorCode); // rule never covers deletion
        Assert.Single(h.Approvals.Requests);
    }

    [Fact]
    public async Task BlockedOperationsAreJournaledRedacted()
    {
        var auth = _h.Mint();
        await _h.Call(auth, "shell_execute", new JsonObject { ["apiKey"] = "sk-secret-123" });
        var unresolved = await _h.Journal.GetRunAsync(auth.RunId, CancellationToken.None);
        var entry = Assert.Single(unresolved);
        Assert.Equal(JournalStatus.Blocked, entry.Status);
        Assert.DoesNotContain("sk-secret", entry.RedactedArguments);
    }

    [Fact]
    public void RedactionRemovesSecretsAndLongValues()
    {
        var s = ToolBroker.Redact(new JsonObject { ["apiKey"] = "sk-1", ["text"] = new string('a', 500), ["drive"] = "C" });
        Assert.DoesNotContain("sk-1", s);
        Assert.DoesNotContain("aaaaaaaaaa", s);
        Assert.Contains("\"drive\":\"C\"", s);
    }

    private sealed class AlwaysRule : IStandingRules
    {
        public bool Covers(ApprovalRequest request, out string ruleId) { ruleId = "test"; return true; }
    }
}
