using System.Text.Json.Nodes;
using VoxStride.Contracts;
using VoxStride.Core.Execution;
using VoxStride.Core.Files;
using VoxStride.Core.Tools;
using Xunit;

namespace VoxStride.Tests;

public sealed class FileOpsTests : IDisposable
{
    private sealed class Folders(string desktop, string documents) : IKnownFolders
    {
        public string? Resolve(KnownFolder f) => f switch { KnownFolder.Desktop => desktop, KnownFolder.Documents => documents, _ => null };
    }

    /// <summary>Stands in for the Windows Recycle Bin: moves items into a bin folder outside the scope.</summary>
    private sealed class FakeBin(string bin) : IFileRemover
    {
        public bool SupportsRecycle => true;
        public List<string> Recycled { get; } = [];
        public void Remove(string path, bool recycle)
        {
            if (!recycle) throw new InvalidOperationException("permanent delete must never be used by files_recycle");
            var target = Path.Combine(bin, Guid.NewGuid().ToString("N") + Path.GetFileName(path));
            if (Directory.Exists(path)) Directory.Move(path, target); else File.Move(path, target);
            Recycled.Add(path);
        }
    }

    private readonly TempDir _desktop = new(), _documents = new(), _outside = new(), _bin = new();
    private readonly Harness _h = new();
    private readonly FakeBin _recycle;
    private readonly FileScope _scope;

    public FileOpsTests()
    {
        _recycle = new FakeBin(_bin.Path);
        _scope = new FileScope(new Folders(_desktop.Path, _documents.Path));
        var services = new FileToolServices(new Folders(_desktop.Path, _documents.Path), new ManifestStore(_h.Time), new MoveExecutor(_h.Journal, _h.Time), new OrganizationPlanner(_h.Time), new TempCleaner(_h.Time), _recycle, _h.Drives);
        _h.Registry.Add(new ListFilesTool(_scope)).Add(new SearchFilesTool(_scope)).Add(new CreateFolderTool(_scope, _h.Journal))
            .Add(new MoveFilesTool(_scope, services)).Add(new RenameFileTool(_scope, services)).Add(new CopyFilesTool(_scope)).Add(new RecycleFilesTool(_scope, _recycle));
        // Undo for these tools uses the same manifest store/executor as the move tools.
        _h.Registry.Add(new UndoWrapper(new UndoOrganizationTool(services)));
    }

    /// <summary>Registers the undo tool under a distinct name (the harness already registers one bound to its own services).</summary>
    private sealed class UndoWrapper(UndoOrganizationTool inner) : ITool
    {
        public ToolDefinition Definition { get; } = inner.Definition with { Name = "files_undo_test" };
        public ValueTask<ToolValidation> ValidateAsync(JsonObject a, ToolContext c, CancellationToken ct) => inner.ValidateAsync(a, c, ct);
        public ValueTask<ToolResult> ExecuteAsync(JsonObject a, ToolContext c, CancellationToken ct) => inner.ExecuteAsync(a, c, ct);
        public ValueTask<ToolVerification> VerifyAsync(JsonObject a, ToolResult r, ToolContext c, CancellationToken ct) => inner.VerifyAsync(a, r, c, ct);
    }

    public void Dispose() { _h.Dispose(); _desktop.Dispose(); _documents.Dispose(); _outside.Dispose(); _bin.Dispose(); }

    private string Make(string root, string rel, string content = "data")
    {
        var p = Path.Combine(root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, content);
        return p;
    }

    private static JsonArray Arr(params string[] s) => new(s.Select(x => (JsonNode)x!).ToArray());

    [Fact]
    public async Task ListShowsFilesAndFoldersButNotHiddenOnes()
    {
        Make(_desktop.Path, "a.txt"); Make(_desktop.Path, "sub/b.txt"); Make(_desktop.Path, ".secret");
        var r = await _h.Call(_h.Mint(), "files_list", new JsonObject { ["path"] = "Desktop" });
        Assert.Equal(ToolOutcome.Success, r.Outcome);
        Assert.Equal(1, r.ObservedState!["files"]!.GetValue<int>());
        Assert.Equal(1, r.ObservedState!["folders"]!.GetValue<int>());
        Assert.DoesNotContain(".secret", r.ObservedState!.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SearchFindsByNameAndTypeAcrossAllowedFolders()
    {
        Make(_desktop.Path, "Invoice-March.pdf"); Make(_documents.Path, "2026/invoice-april.PDF"); Make(_documents.Path, "notes.txt"); Make(_outside.Path, "invoice-outside.pdf");
        var r = await _h.Call(_h.Mint(), "files_search", new JsonObject { ["nameContains"] = "invoice", ["extensions"] = Arr(".pdf") });
        Assert.Equal(2, r.ObservedState!["matches"]!.GetValue<int>());
        Assert.DoesNotContain("outside", r.ObservedState!.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SearchRefusesAnUnboundedQuery()
    {
        var r = await _h.Call(_h.Mint(), "files_search", []);
        Assert.Equal("too_broad", r.ErrorCode);
    }

    [Theory]
    [InlineData("outside")]
    [InlineData("traversal")]
    [InlineData("relative")]
    public async Task PathsOutsideTheScopeAreRefused(string kind)
    {
        var path = kind switch
        {
            "outside" => _outside.Path,
            "traversal" => Path.Combine(_desktop.Path, "..", Path.GetFileName(_outside.Path)),
            _ => "some/relative/folder",
        };
        var r = await _h.Call(_h.Mint(), "files_list", new JsonObject { ["path"] = path });
        Assert.Equal(ToolOutcome.Failure, r.Outcome);
        Assert.Equal("path_not_allowed", r.ErrorCode);
    }

    [Fact]
    public async Task CreateFolderCreatesOnceAndRejectsReservedNames()
    {
        var auth = _h.Mint();
        var r = await _h.Call(auth, "files_create_folder", new JsonObject { ["parent"] = "Documents", ["name"] = "Projects" });
        Assert.True(r.Verified);
        Assert.True(Directory.Exists(Path.Combine(_documents.Path, "Projects")));
        var again = await _h.Call(auth, "files_create_folder", new JsonObject { ["parent"] = "Documents", ["name"] = "Projects" });
        Assert.False(again.ObservedState!["created"]!.GetValue<bool>());
        var bad = await _h.Call(auth, "files_create_folder", new JsonObject { ["parent"] = "Documents", ["name"] = "CON" });
        Assert.Equal("invalid_name", bad.ErrorCode);
    }

    [Fact]
    public async Task MoveNeedsApprovalNeverOverwritesAndUndoesAcrossFolders()
    {
        var a = Make(_desktop.Path, "report.docx", "A");
        var b = Make(_desktop.Path, "photo.jpg", "B");
        Make(_documents.Path, "Work/photo.jpg", "existing");
        var auth = _h.Mint();
        var r = await _h.Call(auth, "files_move", new JsonObject { ["sources"] = Arr(a, b), ["destination"] = "Documents/Work" });
        var req = Assert.Single(_h.Approvals.Requests);
        Assert.Equal("files.move_batch", req.Operation);
        Assert.Contains(req.Lines, l => l.Contains("report.docx", StringComparison.Ordinal));
        Assert.Contains(req.Lines, l => l.Contains("Left alone: photo.jpg", StringComparison.Ordinal));
        Assert.True(r.Verified);
        Assert.False(File.Exists(a));
        Assert.Equal("A", File.ReadAllText(Path.Combine(_documents.Path, "Work", "report.docx")));
        Assert.Equal("existing", File.ReadAllText(Path.Combine(_documents.Path, "Work", "photo.jpg"))); // never overwritten
        Assert.True(File.Exists(b));

        var undo = await _h.Call(auth, "files_undo_test", []);
        Assert.Equal(ToolOutcome.Success, undo.Outcome);
        Assert.Equal("A", File.ReadAllText(a));
    }

    [Fact]
    public async Task DeclinedMoveChangesNothing()
    {
        var a = Make(_desktop.Path, "keep.txt");
        _h.Approvals.Approve = false;
        var r = await _h.Call(_h.Mint(), "files_move", new JsonObject { ["sources"] = Arr(a), ["destination"] = "Documents" });
        Assert.Equal("approval_declined", r.ErrorCode);
        Assert.True(File.Exists(a));
    }

    [Fact]
    public async Task RenameKeepsTheTypeRefusesExistingNamesAndUndoes()
    {
        var f = Make(_desktop.Path, "draft.txt", "hello");
        Make(_desktop.Path, "taken.txt");
        var auth = _h.Mint();
        var clash = await _h.Call(auth, "files_rename", new JsonObject { ["path"] = f, ["newName"] = "taken.txt" });
        Assert.Equal("nothing_to_do", clash.ErrorCode);
        var r = await _h.Call(auth, "files_rename", new JsonObject { ["path"] = f, ["newName"] = "final" });
        Assert.True(r.Verified);
        Assert.Equal("hello", File.ReadAllText(Path.Combine(_desktop.Path, "final.txt")));
        await _h.Call(auth, "files_undo_test", []);
        Assert.True(File.Exists(f));
    }

    [Fact]
    public async Task CopyLeavesOriginalsAndSkipsExistingNames()
    {
        var a = Make(_desktop.Path, "a.txt", "1");
        var b = Make(_desktop.Path, "b.txt", "2");
        Make(_documents.Path, "Backup/b.txt", "old");
        var r = await _h.Call(_h.Mint(), "files_copy", new JsonObject { ["sources"] = Arr(a, b), ["destination"] = "Documents/Backup" });
        Assert.True(r.Verified);
        Assert.Equal(1, r.ObservedState!["copied"]!.GetValue<int>());
        Assert.True(File.Exists(a) && File.Exists(b));
        Assert.Equal("old", File.ReadAllText(Path.Combine(_documents.Path, "Backup", "b.txt")));
        Assert.Empty(_h.Approvals.Requests); // copying destroys nothing, so no approval
    }

    [Fact]
    public async Task RecycleAlwaysAsksAndNeverDeletesPermanently()
    {
        var a = Make(_desktop.Path, "old.log");
        var r = await _h.Call(_h.Mint(), "files_recycle", new JsonObject { ["paths"] = Arr(a) });
        Assert.Single(_h.Approvals.Requests);
        Assert.True(r.Verified);
        Assert.False(File.Exists(a));
        Assert.Single(_recycle.Recycled);
    }

    [Fact]
    public async Task RecycleRefusesAWholeKnownFolder()
    {
        var r = await _h.Call(_h.Mint(), "files_recycle", new JsonObject { ["paths"] = Arr("Desktop") });
        Assert.Equal("nothing_to_do", r.ErrorCode);
        Assert.True(Directory.Exists(_desktop.Path));
    }

    [Fact]
    public async Task RecycleSkipsAFileChangedAfterApproval()
    {
        var a = Make(_desktop.Path, "live.txt", "v1");
        _h.Approvals.Override = req =>
        {
            File.WriteAllText(a, "version two is longer"); // the user edits it while the approval dialog is open
            return new Approval(Guid.NewGuid(), req.RunId, req.ManifestDigest, req.Operation, DateTimeOffset.UtcNow.AddMinutes(5), "test-click");
        };
        var r = await _h.Call(_h.Mint(), "files_recycle", new JsonObject { ["paths"] = Arr(a) });
        Assert.Equal(ToolOutcome.Failure, r.Outcome);
        Assert.True(File.Exists(a));
    }
}

public sealed class UserPermissionsTests
{
    [Theory]
    [InlineData("facebook.com", "https://m.facebook.com/x", false)]
    [InlineData("https://www.Facebook.com/home", "https://facebook.com", false)]
    [InlineData("facebook.com", "https://notfacebook.com", true)]
    [InlineData("", "https://example.com", true)]
    public void SitesBlockByDomainAndSubdomain(string blocked, string url, bool allowed) =>
        Assert.Equal(allowed, new VoxStride.Core.Policy.UserPermissions(blockedSites: () => [blocked]).SiteAllowed(new Uri(url), out _));

    [Theory]
    [InlineData("Steam", "Steam", @"C:\Program Files\Steam\steam.exe", false)]
    [InlineData("steam.exe", "Steam Client", @"C:\x\steam.exe", false)]
    [InlineData("Word", "WordPad", @"C:\x\wordpad.exe", false)]
    [InlineData("Paint", "Notepad", @"C:\x\notepad.exe", true)]
    public void AppsBlockByNameOrExe(string blocked, string name, string target, bool allowed) =>
        Assert.Equal(allowed, new VoxStride.Core.Policy.UserPermissions(blockedApps: () => [blocked]).AppAllowed(name, target, out _));
}
