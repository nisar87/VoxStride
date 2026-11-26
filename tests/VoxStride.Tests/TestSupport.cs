using System.Text.Json.Nodes;
using VoxStride.Contracts;
using VoxStride.Core.Events;
using VoxStride.Core.Execution;
using VoxStride.Core.Files;
using VoxStride.Core.Tools;

namespace VoxStride.Tests;

public sealed class ManualTime(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;
    public ManualTime() : this(DateTimeOffset.UtcNow) { }
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan by) => _now += by;
}

public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "voxstride-tests", Guid.NewGuid().ToString("N")[..10]);
    public TempDir() { Directory.CreateDirectory(Path); }
    public string File(string rel, string content = "x", DateTime? lastWriteUtc = null)
    {
        var p = System.IO.Path.Combine(Path, rel);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(p)!);
        System.IO.File.WriteAllText(p, content);
        if (lastWriteUtc is { } t) { System.IO.File.SetLastWriteTimeUtc(p, t); System.IO.File.SetLastAccessTimeUtc(p, t); }
        return p;
    }
    public string Dir(string rel) { var p = System.IO.Path.Combine(Path, rel); Directory.CreateDirectory(p); return p; }
    public void Dispose() { try { Directory.Delete(Path, true); } catch (Exception) { } }
}

public sealed class FixedFolders(string desktop, string? temp = null) : IKnownFolders
{
    public string? Resolve(KnownFolder folder) => folder switch
    {
        KnownFolder.Desktop => desktop,
        KnownFolder.Temp => temp,
        _ => null,
    };
}

public sealed class AutoApprove : IUserApproval
{
    public List<ApprovalRequest> Requests { get; } = [];
    public bool Approve { get; set; } = true;
    public Func<ApprovalRequest, Approval?>? Override { get; set; }
    public Task<Approval?> RequestAsync(ApprovalRequest request, CancellationToken ct)
    {
        Requests.Add(request);
        if (Override is not null) return Task.FromResult(Override(request));
        return Task.FromResult(Approve ? new Approval(Guid.NewGuid(), request.RunId, request.ManifestDigest, request.Operation, DateTimeOffset.UtcNow.AddYears(10), "test-click") : null);
    }
}

public sealed class RecordingSink : IEventSink
{
    public List<string> Messages { get; } = [];
    public void Publish(Guid runId, AgentState state, string phase, string message, string? stepId = null, int? completed = null, int? total = null, bool canCancel = true)
    {
        lock (Messages) Messages.Add($"{state}|{phase}|{message}");
    }
}

public sealed class FakeDrives(long total = 500_000_000_000, long free = 123_456_789_000) : IDriveInfoSource
{
    public long Free { get; set; } = free;
    public DriveSnapshot? Get(string driveLetter) => driveLetter.Equals("C", StringComparison.OrdinalIgnoreCase)
        ? new DriveSnapshot("C:\\", "OS", "NTFS", "Fixed", total, Free, Free, true) : null;
    public IReadOnlyList<DriveSnapshot> All() => [Get("C")!];
}

/// <summary>Builds a fully wired broker with real file tools over a temp "desktop".</summary>
public sealed class Harness : IDisposable
{
    public TempDir Desktop { get; } = new();
    public TempDir Temp { get; } = new();
    public ManualTime Time { get; } = new();
    public RunAuthority Authority { get; }
    public InMemoryJournal Journal { get; } = new();
    public AutoApprove Approvals { get; } = new();
    public RecordingSink Events { get; } = new();
    public ToolRegistry Registry { get; } = new();
    public ToolBroker Broker { get; }
    public FileToolServices Files { get; }
    public FakeDrives Drives { get; } = new();
    public IStandingRules Rules { get; set; } = new NoStandingRules();

    public Harness(IStandingRules? rules = null)
    {
        if (rules is not null) Rules = rules;
        Authority = new RunAuthority(Time);
        Files = new FileToolServices(new FixedFolders(Desktop.Path, Temp.Path), new ManifestStore(Time), new MoveExecutor(Journal, Time), new OrganizationPlanner(Time), new TempCleaner(Time), new PermanentFileRemover(), Drives);
        Registry.Add(new GetFreeSpaceTool(Drives)).Add(new ListDrivesTool(Drives))
            .Add(new PreviewOrganizationTool(Files)).Add(new ApplyOrganizationTool(Files)).Add(new UndoOrganizationTool(Files))
            .Add(new PreviewTempCleanupTool(Files)).Add(new ApplyTempCleanupTool(Files));
        Broker = new ToolBroker(Registry, Authority, Journal, Approvals, Rules, Events, Time);
    }

    public RunAuthorization Mint(params string[] tools)
    {
        const string text = "test instruction";
        var cmd = new CommandEnvelope(Guid.NewGuid(), Guid.NewGuid(), text, RunAuthority.HashTranscript(text), "en-US", [], Time.GetUtcNow(), InputMode.Typed, null, null);
        return Authority.Mint(cmd, tools.Length == 0 ? Registry.Definitions.Select(d => d.Name) : tools, TimeSpan.FromMinutes(10));
    }

    private int _n;
    public Task<ToolResult> Call(RunAuthorization auth, string tool, JsonObject? args = null, string? key = null) =>
        Broker.DispatchAsync(new ToolRequest(Guid.Empty, "s" + (++_n), key ?? Guid.NewGuid().ToString(), 1, tool, args ?? []), auth, CancellationToken.None);

    public void Dispose() { Desktop.Dispose(); Temp.Dispose(); }
}
