using System.Text.Json;
using System.Text.Json.Nodes;
using VoxStride.Contracts;
using VoxStride.Core.Execution;
using VoxStride.Core.Json;

namespace VoxStride.Core.Tools;

/// <summary>
/// Things the user asked VoxStride to remember ("my sister's name is Ayesha", "I like short answers"), kept only on
/// this PC in a JSON file, shown in Settings, and given to the companion at the start of each conversation.
/// </summary>
public sealed class CompanionMemory(string path)
{
    public const int MaxFacts = 200;
    private readonly Lock _gate = new();

    public sealed record Fact(string Text, DateTimeOffset Saved);

    public IReadOnlyList<Fact> All()
    {
        lock (_gate)
        {
            try { return File.Exists(path) ? JsonSerializer.Deserialize<List<Fact>>(File.ReadAllText(path)) ?? [] : []; }
            catch (Exception ex) when (ex is JsonException or IOException) { Log.Warn("memory", "memory file unreadable; starting empty", ex); return []; }
        }
    }

    public bool Add(string text)
    {
        text = text.Trim();
        if (text.Length is 0 or > 500) return false;
        lock (_gate)
        {
            var list = All().ToList();
            if (list.Any(f => string.Equals(f.Text, text, StringComparison.OrdinalIgnoreCase))) return true;
            list.Add(new Fact(text, DateTimeOffset.Now));
            if (list.Count > MaxFacts) list.RemoveRange(0, list.Count - MaxFacts);
            Save(list);
            return true;
        }
    }

    /// <summary>Removes facts containing all the given words; returns how many.</summary>
    public int Forget(string about)
    {
        var words = about.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(w => w.Length > 2).ToArray();
        if (words.Length == 0) return 0;
        lock (_gate)
        {
            var list = All().ToList();
            var n = list.RemoveAll(f => words.All(w => f.Text.Contains(w, StringComparison.OrdinalIgnoreCase)));
            if (n > 0) Save(list);
            return n;
        }
    }

    public void Clear() { lock (_gate) Save([]); }

    public string AsPrompt() => string.Join("\n", All().TakeLast(80).Select(f => "- " + f.Text));

    private void Save(List<Fact> list)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, path, overwrite: true);
    }
}

public sealed class RememberTool(CompanionMemory memory) : ToolBase
{
    public override ToolDefinition Definition { get; } = new(
        "memory_remember",
        "Save something the user wants you to remember about them for future conversations (their name, preferences, people, plans). Only when they ask you to remember it, or clearly want it kept. Write it as a short third-person fact, e.g. 'The user's sister is called Ayesha.'",
        Schema.Object(("fact", Schema.Str("The fact to remember", null, 500), true)),
        RiskTier.LowReversible, true);

    public override ValueTask<ToolResult> ExecuteAsync(JsonObject args, ToolContext ctx, CancellationToken ct) =>
        ValueTask.FromResult(memory.Add(Str(args, "fact"))
            ? ToolResult.Ok(new JsonObject { ["saved"] = true }, "Got it, I'll remember that.", true)
            : ToolResult.Fail("invalid", "That was empty or too long to remember."));
}

public sealed class ForgetTool(CompanionMemory memory) : ToolBase
{
    public override ToolDefinition Definition { get; } = new(
        "memory_forget",
        "Forget remembered facts about something when the user asks you to forget it.",
        Schema.Object(("about", Schema.Str("Words identifying what to forget, e.g. 'sister'", null, 200), true)),
        RiskTier.LowReversible, true);

    public override ValueTask<ToolResult> ExecuteAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        var n = memory.Forget(Str(args, "about"));
        return ValueTask.FromResult(ToolResult.Ok(new JsonObject { ["forgotten"] = n }, n == 0 ? "I didn't have anything saved about that." : $"Okay, I forgot {n} thing(s) about that.", true));
    }
}
