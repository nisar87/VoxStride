using System.Text.Json.Nodes;
using VoxStride.Contracts;
using VoxStride.Core.Execution;
using VoxStride.Core.Files;
using VoxStride.Core.Json;

namespace VoxStride.Core.Tools;

public abstract class ToolBase : ITool
{
    public abstract ToolDefinition Definition { get; }
    public virtual ValueTask<ToolValidation> ValidateAsync(JsonObject args, ToolContext context, CancellationToken ct) => ValueTask.FromResult(ToolValidation.Valid());
    public abstract ValueTask<ToolResult> ExecuteAsync(JsonObject args, ToolContext context, CancellationToken ct);
    public virtual ValueTask<ToolVerification> VerifyAsync(JsonObject args, ToolResult result, ToolContext context, CancellationToken ct) =>
        ValueTask.FromResult(new ToolVerification(result.Verified, result.Outcome));
    protected static string Str(JsonObject a, string k, string def = "") => a[k]?.GetValue<string>() ?? def;
    protected static int Int(JsonObject a, string k, int def) => a[k] is JsonValue v ? (int)double.Parse(v.ToJsonString(), System.Globalization.CultureInfo.InvariantCulture) : def;
    protected static bool Bool(JsonObject a, string k, bool def) => a[k] is JsonValue v ? v.GetValue<bool>() : def;
}

public interface IDriveInfoSource
{
    DriveSnapshot? Get(string driveLetter);
    IReadOnlyList<DriveSnapshot> All();
}

public sealed record DriveSnapshot(string Name, string Label, string Format, string Type, long TotalBytes, long FreeBytes, long AvailableToUserBytes, bool Ready);

public sealed class SystemDriveInfoSource : IDriveInfoSource
{
    public DriveSnapshot? Get(string driveLetter)
    {
        var name = OperatingSystem.IsWindows() ? driveLetter.ToUpperInvariant() + @":\" : "/";
        var d = DriveInfo.GetDrives().FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
        return d is null ? null : Snap(d);
    }

    public IReadOnlyList<DriveSnapshot> All() => DriveInfo.GetDrives().Select(Snap).ToList();

    private static DriveSnapshot Snap(DriveInfo d)
    {
        if (!d.IsReady) return new DriveSnapshot(d.Name, "", "", d.DriveType.ToString(), 0, 0, 0, false);
        return new DriveSnapshot(d.Name, SafeLabel(d), d.DriveFormat, d.DriveType.ToString(), d.TotalSize, d.TotalFreeSpace, d.AvailableFreeSpace, true);
    }

    private static string SafeLabel(DriveInfo d) { try { return d.VolumeLabel; } catch (Exception) { return ""; } }
}

/// <summary>system_get_free_space — exact live bytes from the OS, no reasoning call required.</summary>
public sealed class GetFreeSpaceTool(IDriveInfoSource drives) : ToolBase
{
    public override ToolDefinition Definition { get; } = new(
        "system_get_free_space",
        "Read free and total bytes on one explicitly requested local drive.",
        Schema.Object(("driveLetter", Schema.Str("Drive letter, e.g. C", "^[A-Za-z]$", 1), true)),
        RiskTier.Read, Mutates: false);

    public override ValueTask<ToolResult> ExecuteAsync(JsonObject args, ToolContext context, CancellationToken ct)
    {
        var letter = Str(args, "driveLetter").ToUpperInvariant();
        var d = drives.Get(letter);
        if (d is null) return ValueTask.FromResult(ToolResult.Fail("drive_not_found", $"I couldn't find a drive {letter} on this PC."));
        if (!d.Ready) return ValueTask.FromResult(ToolResult.Fail("drive_not_ready", $"Drive {letter} is not ready."));
        var state = new JsonObject
        {
            ["drive"] = letter + ":",
            ["freeBytes"] = d.FreeBytes,
            ["availableToUserBytes"] = d.AvailableToUserBytes,
            ["totalBytes"] = d.TotalBytes,
            ["usedBytes"] = d.TotalBytes - d.FreeBytes,
            ["freeReadable"] = Bytes.Format(d.AvailableToUserBytes),
            ["totalReadable"] = Bytes.Format(d.TotalBytes),
            ["freeBinary"] = Bytes.FormatBinary(d.AvailableToUserBytes),
            ["percentFree"] = d.TotalBytes > 0 ? Math.Round(100.0 * d.AvailableToUserBytes / d.TotalBytes, 1) : 0,
            ["format"] = d.Format,
        };
        var pct = d.TotalBytes > 0 ? 100.0 * d.AvailableToUserBytes / d.TotalBytes : 0;
        var mood = pct < 10 ? " That's getting low — want me to clean up temp files?" : pct < 20 ? " It's getting a bit full." : "";
        var msg = $"You've got {Bytes.Spoken(d.AvailableToUserBytes)} free on drive {letter}, out of {Bytes.Spoken(d.TotalBytes)} — about {pct:0}% free.{mood}";
        return ValueTask.FromResult(ToolResult.Ok(state, msg, verified: true));
    }
}

public sealed class ListDrivesTool(IDriveInfoSource drives) : ToolBase
{
    public override ToolDefinition Definition { get; } = new(
        "system_list_drives", "List local drives with free and total space.", Schema.Object(), RiskTier.Read, false);

    public override ValueTask<ToolResult> ExecuteAsync(JsonObject args, ToolContext context, CancellationToken ct)
    {
        var arr = new JsonArray();
        foreach (var d in drives.All().Where(d => d.Ready && d.Type is "Fixed" or "Removable"))
            arr.Add(new JsonObject { ["drive"] = d.Name, ["label"] = d.Label, ["freeBytes"] = d.AvailableToUserBytes, ["totalBytes"] = d.TotalBytes, ["freeReadable"] = Bytes.Format(d.AvailableToUserBytes), ["totalReadable"] = Bytes.Format(d.TotalBytes) });
        return ValueTask.FromResult(ToolResult.Ok(new JsonObject { ["drives"] = arr }, $"{arr.Count} drive(s) found.", true));
    }
}
