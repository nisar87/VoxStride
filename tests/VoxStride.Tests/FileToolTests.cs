using System.Text.Json.Nodes;
using VoxStride.Contracts;
using VoxStride.Core.Execution;
using VoxStride.Core.Files;
using VoxStride.Core.Policy;
using Xunit;

namespace VoxStride.Tests;

/// <summary>F01–F05 — desktop organizer, collisions, exclusions, verification, undo, reconciliation, cleanup.</summary>
public sealed class FileToolTests : IDisposable
{
    private readonly Harness _h = new();
    public void Dispose() => _h.Dispose();

    private async Task<(ToolResult preview, ToolResult apply)> Organize(JsonObject? extra = null)
    {
        var auth = _h.Mint();
        var args = new JsonObject { ["folder"] = "desktop" };
        if (extra is not null) foreach (var (k, v) in extra) args[k] = v?.DeepClone();
        var p = await _h.Call(auth, "desktop_preview_organization", args);
        Assert.Equal(ToolOutcome.Success, p.Outcome);
        if (p.ObservedState!["moveCount"]!.GetValue<int>() == 0) return (p, p);
        var a = await _h.Call(auth, "desktop_apply_organization", new JsonObject { ["manifestId"] = p.ObservedState["manifestId"]!.GetValue<string>() });
        return (p, a);
    }

    [Fact]
    public async Task F01_MixedDesktopIsOrganizedWithSkipsAndNoOverwrite()
    {
        var d = _h.Desktop;
        for (var i = 0; i < 8; i++) d.File($"doc{i}.docx");
        for (var i = 0; i < 6; i++) d.File($"photo{i}.JPG");
        for (var i = 0; i < 4; i++) d.File($"song{i}.mp3");
        for (var i = 0; i < 3; i++) d.File($"pack{i}.zip");
        d.File("report.pdf");
        d.File("unknown.xyz");
        d.File("App.lnk");
        d.File("site.url");
        d.File(".hidden.txt");
        d.File("desktop.ini");
        d.Dir("Existing Folder");
        d.File("Images/photo0.JPG", "existing different");   // collision with an existing file in destination
        d.File("Images/keep.png");

        var (p, a) = await Organize();
        Assert.Equal(22, p.ObservedState!["moveCount"]!.GetValue<int>());
        Assert.Equal(ToolOutcome.Success, a.Outcome);
        Assert.True(a.Verified);
        Assert.Equal(22, a.ObservedState!["moved"]!.GetValue<int>());
        Assert.True(File.Exists(Path.Combine(d.Path, "App.lnk")));
        Assert.True(File.Exists(Path.Combine(d.Path, "site.url")));
        Assert.True(File.Exists(Path.Combine(d.Path, ".hidden.txt")));
        Assert.True(File.Exists(Path.Combine(d.Path, "unknown.xyz")));
        Assert.True(Directory.Exists(Path.Combine(d.Path, "Existing Folder")));
        Assert.Equal("existing different", File.ReadAllText(Path.Combine(d.Path, "Images", "photo0.JPG"))); // not overwritten
        Assert.True(File.Exists(Path.Combine(d.Path, "Images", "photo0 (2).JPG")));
        Assert.True(File.Exists(Path.Combine(d.Path, "PDFs", "report.pdf")));
        Assert.Equal(8, Directory.GetFiles(Path.Combine(d.Path, "Documents")).Length);
    }

    [Fact]
    public async Task ExclusionsByCategoryAndExtensionAreHonored()
    {
        _h.Desktop.File("a.pdf"); _h.Desktop.File("b.zip"); _h.Desktop.File("c.png");
        var (_, a) = await Organize(new JsonObject { ["excludeCategories"] = new JsonArray("PDFs"), ["excludeExtensions"] = new JsonArray(".zip") });
        Assert.Equal(1, a.ObservedState!["moved"]!.GetValue<int>());
        Assert.True(File.Exists(Path.Combine(_h.Desktop.Path, "a.pdf")));
        Assert.True(File.Exists(Path.Combine(_h.Desktop.Path, "b.zip")));
    }

    [Fact]
    public async Task TwoFilesWithSameNameDifferentCaseGetUniqueDestinations()
    {
        if (OperatingSystem.IsWindows()) return; // case-insensitive FS cannot hold both
        _h.Desktop.File("Notes.txt", "1"); _h.Desktop.File("notes.TXT", "2");
        var (_, a) = await Organize();
        Assert.Equal(2, a.ObservedState!["moved"]!.GetValue<int>());
        Assert.Equal(2, Directory.GetFiles(Path.Combine(_h.Desktop.Path, "Documents")).Length);
    }

    [Fact]
    public async Task F02_SymlinkFilesAreSkippedAndLinkedDestinationIsRefused()
    {
        var outside = new TempDir();
        try
        {
            var target = outside.File("secret.pdf");
            File.CreateSymbolicLink(Path.Combine(_h.Desktop.Path, "link.pdf"), target);
            Directory.CreateSymbolicLink(Path.Combine(_h.Desktop.Path, "Images"), outside.Path);
            _h.Desktop.File("pic.png");
            var (p, _) = await Organize();
            Assert.Equal(0, p.ObservedState!["moveCount"]!.GetValue<int>());
            Assert.True(File.Exists(target));
            Assert.DoesNotContain(Directory.GetFiles(outside.Path), f => f.EndsWith("pic.png", StringComparison.Ordinal));
        }
        catch (UnauthorizedAccessException) { /* symlink creation not permitted on this runner */ }
        catch (IOException) { }
        finally { outside.Dispose(); }
    }

    [Fact]
    public async Task F02_SourceChangedAfterPreviewIsSkipped()
    {
        var f = _h.Desktop.File("a.pdf", "v1");
        _h.Desktop.File("b.pdf", "v1");
        var auth = _h.Mint();
        var p = await _h.Call(auth, "desktop_preview_organization", new JsonObject { ["folder"] = "desktop" });
        File.WriteAllText(f, "version two is longer");
        var a = await _h.Call(auth, "desktop_apply_organization", new JsonObject { ["manifestId"] = p.ObservedState!["manifestId"]!.GetValue<string>() });
        Assert.Equal(ToolOutcome.Partial, a.Outcome);
        Assert.Equal(1, a.ObservedState!["moved"]!.GetValue<int>());
        Assert.True(File.Exists(f));
    }

    [Fact]
    public async Task F02_DestinationAppearingAfterPreviewIsNotOverwritten()
    {
        _h.Desktop.File("a.pdf", "mine");
        var auth = _h.Mint();
        var p = await _h.Call(auth, "desktop_preview_organization", new JsonObject { ["folder"] = "desktop" });
        _h.Desktop.File("PDFs/a.pdf", "someone else's");
        var a = await _h.Call(auth, "desktop_apply_organization", new JsonObject { ["manifestId"] = p.ObservedState!["manifestId"]!.GetValue<string>() });
        Assert.Equal(ToolOutcome.Failure, a.Outcome);
        Assert.Equal("someone else's", File.ReadAllText(Path.Combine(_h.Desktop.Path, "PDFs", "a.pdf")));
        Assert.Equal("mine", File.ReadAllText(Path.Combine(_h.Desktop.Path, "a.pdf")));
    }

    [Fact]
    public async Task ExpiredOrUnknownManifestIsRejected()
    {
        _h.Desktop.File("a.pdf");
        var auth = _h.Mint();
        var p = await _h.Call(auth, "desktop_preview_organization", new JsonObject { ["folder"] = "desktop" });
        _h.Time.Advance(TimeSpan.FromMinutes(16)); // manifests live 15 minutes
        var auth2 = _h.Mint();
        var a = await _h.Call(auth2, "desktop_apply_organization", new JsonObject { ["manifestId"] = p.ObservedState!["manifestId"]!.GetValue<string>() });
        Assert.Equal("manifest_unknown", a.ErrorCode);
        var b = await _h.Call(auth2, "desktop_apply_organization", new JsonObject { ["manifestId"] = "m-000000000000" });
        Assert.Equal("manifest_unknown", b.ErrorCode);
        Assert.True(File.Exists(Path.Combine(_h.Desktop.Path, "a.pdf")));
    }

    [Fact]
    public async Task UndoRestoresUnchangedFilesAndRemovesEmptyCreatedFolders()
    {
        _h.Desktop.File("a.pdf"); _h.Desktop.File("b.png");
        await Organize();
        var auth = _h.Mint();
        var u = await _h.Call(auth, "files_undo_organization");
        Assert.Equal(ToolOutcome.Success, u.Outcome);
        Assert.True(File.Exists(Path.Combine(_h.Desktop.Path, "a.pdf")));
        Assert.True(File.Exists(Path.Combine(_h.Desktop.Path, "b.png")));
        Assert.False(Directory.Exists(Path.Combine(_h.Desktop.Path, "PDFs")));
        Assert.False(Directory.Exists(Path.Combine(_h.Desktop.Path, "Images")));
    }

    [Fact]
    public async Task F04_UndoReportsConflictsAndNeverOverwrites()
    {
        _h.Desktop.File("edited.pdf", "orig"); _h.Desktop.File("occupied.png", "orig"); _h.Desktop.File("fine.mp3", "orig");
        await Organize();
        File.WriteAllText(Path.Combine(_h.Desktop.Path, "PDFs", "edited.pdf"), "user edited this after the move");
        _h.Desktop.File("occupied.png", "new file in the original place");
        var auth = _h.Mint();
        var u = await _h.Call(auth, "files_undo_organization");
        Assert.Equal(ToolOutcome.Partial, u.Outcome);
        Assert.Equal(1, u.ObservedState!["restored"]!.GetValue<int>());
        Assert.Equal(2, u.ObservedState!["conflicts"]!.GetValue<int>());
        Assert.Equal("new file in the original place", File.ReadAllText(Path.Combine(_h.Desktop.Path, "occupied.png")));
        Assert.Equal("user edited this after the move", File.ReadAllText(Path.Combine(_h.Desktop.Path, "PDFs", "edited.pdf")));
        Assert.True(File.Exists(Path.Combine(_h.Desktop.Path, "fine.mp3")));
        Assert.True(Directory.Exists(Path.Combine(_h.Desktop.Path, "PDFs"))); // not empty → kept
    }

    [Fact]
    public async Task F03_CrashBetweenIntentAndResultIsReportedNotReplayed()
    {
        await _h.Journal.RecordMoveAsync(new MoveRecord(Guid.NewGuid(), Guid.NewGuid(), "m-crash0000000", "/x/a.pdf", "/x/PDFs/a.pdf", 1, DateTime.UtcNow, "intent", DateTimeOffset.UtcNow), CancellationToken.None);
        var unresolved = await _h.Journal.GetUnresolvedMovesAsync(CancellationToken.None);
        Assert.Single(unresolved);
        // Undo never acts on intent-only records.
        var plan = await _h.Files.Executor.PlanUndoAsync("m-crash0000000", CancellationToken.None);
        Assert.Empty(plan);
    }

    [Fact]
    public async Task L04_CancelMidBatchStopsNewMoves()
    {
        for (var i = 0; i < 50; i++) _h.Desktop.File($"f{i:00}.pdf");
        var auth = _h.Mint();
        var p = await _h.Call(auth, "desktop_preview_organization", new JsonObject { ["folder"] = "desktop" });
        var moved = 0;
        var sinkHook = new CancelAfterSink(_h.Authority, auth.RunId, after: 10, () => moved++);
        var broker = new ToolBroker(_h.Registry, _h.Authority, _h.Journal, _h.Approvals, new NoStandingRules(), sinkHook, _h.Time);
        var r = await broker.DispatchAsync(new ToolRequest(Guid.Empty, "s9", "k9", 1, "desktop_apply_organization", new JsonObject { ["manifestId"] = p.ObservedState!["manifestId"]!.GetValue<string>() }), auth, CancellationToken.None);
        var remaining = Directory.GetFiles(_h.Desktop.Path, "*.pdf").Length;
        Assert.Equal(ToolOutcome.Unknown, r.Outcome); // cancelled mid-batch: reconciled from journal, not claimed
        Assert.True(remaining >= 39, $"remaining={remaining}");
        var movedRecords = (await _h.Journal.GetMovesAsync(p.ObservedState["manifestId"]!.GetValue<string>(), CancellationToken.None)).Count(m => m.Status == "moved");
        Assert.Equal(50 - remaining, movedRecords);
    }

    [Fact]
    public async Task F05_TempCleanupRemovesOnlyOldEligibleFilesAndMeasures()
    {
        var old = DateTime.UtcNow.AddDays(-3);
        _h.Temp.File("old1.tmp", "aaaa", old);
        _h.Temp.File("sub/old2.log", "bbbbbb", old);
        var recent = _h.Temp.File("recent.tmp", "cc");
        var auth = _h.Mint();
        var p = await _h.Call(auth, "cleanup_preview_temp");
        Assert.Equal(2, p.ObservedState!["eligibleFiles"]!.GetValue<int>());
        Assert.Equal(10L, p.ObservedState!["eligibleBytes"]!.GetValue<long>());
        var a = await _h.Call(auth, "cleanup_apply_temp", new JsonObject { ["manifestId"] = p.ObservedState["manifestId"]!.GetValue<string>(), ["mode"] = "permanent" });
        Assert.Equal(ToolOutcome.Success, a.Outcome);
        Assert.True(File.Exists(recent));
        Assert.Equal(2, a.ObservedState!["removedFiles"]!.GetValue<int>());
        Assert.Contains("Permanently delete", _h.Approvals.Requests.Single().Title);
    }

    [Fact]
    public async Task RecycleModeIsRefusedWhenUnsupported()
    {
        _h.Temp.File("old.tmp", "a", DateTime.UtcNow.AddDays(-3));
        var auth = _h.Mint();
        var p = await _h.Call(auth, "cleanup_preview_temp");
        var a = await _h.Call(auth, "cleanup_apply_temp", new JsonObject { ["manifestId"] = p.ObservedState!["manifestId"]!.GetValue<string>(), ["mode"] = "recycle" });
        Assert.Equal("recycle_unavailable", a.ErrorCode);
    }

    [Fact]
    public void PathPolicyRejectsEscapesAndProtectedLocations()
    {
        var policy = new PathPolicy([_h.Desktop.Path]);
        Assert.Equal(PathDecision.Traversal, policy.Check(Path.Combine(_h.Desktop.Path, "..", "x"), out _));
        Assert.Equal(PathDecision.OutsideRoots, policy.Check(Path.GetTempPath(), out _));
        Assert.Equal(PathDecision.Network, policy.Check(@"\\server\share\x", out _));
        Assert.Equal(PathDecision.Allowed, policy.Check(Path.Combine(_h.Desktop.Path, "a.txt"), out _));
        Assert.True(PathPolicy.IsProtectedLocation(@"C:\Windows\System32\drivers"));
        Assert.True(PathPolicy.IsProtectedLocation(@"C:\Users\me\.ssh\id_rsa"));
        Assert.False(PathPolicy.IsProtectedLocation(@"C:\Users\me\Desktop\a.txt"));
    }

    [Fact]
    public void PathPolicyRejectsSiblingPrefixRoot()
    {
        var policy = new PathPolicy([_h.Desktop.Path]);
        Assert.Equal(PathDecision.OutsideRoots, policy.Check(_h.Desktop.Path + "-evil" + Path.DirectorySeparatorChar + "a.txt", out _));
    }

    private sealed class CancelAfterSink(RunAuthority authority, Guid runId, int after, Action onProgress) : VoxStride.Core.Events.IEventSink
    {
        private int _n;
        public void Publish(Guid r, AgentState state, string phase, string message, string? stepId = null, int? completed = null, int? total = null, bool canCancel = true)
        {
            if (phase != "moving") return;
            onProgress();
            if (Interlocked.Increment(ref _n) == after) authority.Revoke(runId);
        }
    }
}
