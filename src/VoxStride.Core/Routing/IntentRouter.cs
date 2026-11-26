using System.Text.RegularExpressions;
using VoxStride.Core.Files;
using VoxStride.Core.Tools;

namespace VoxStride.Core.Routing;

public abstract record DirectRecipe;
public sealed record FreeSpaceRecipe(string DriveLetter) : DirectRecipe;
public sealed record ListDrivesRecipe : DirectRecipe;
public sealed record LaunchAppRecipe(AppEntry App) : DirectRecipe;
public sealed record OrganizeRecipe(string Folder, IReadOnlyList<string> ExcludeCategories, IReadOnlyList<string> IncludeCategories) : DirectRecipe;
public sealed record UndoRecipe : DirectRecipe;
public sealed record TempCleanupRecipe : DirectRecipe;

public enum RouteKind { Direct, Cloud, Clarify }

public sealed record RouteDecision(RouteKind Kind, IReadOnlyList<DirectRecipe> Recipes, string Reason, string? Question = null)
{
    public static RouteDecision Cloud(string reason) => new(RouteKind.Cloud, [], reason);
    public static RouteDecision Clarify(string q) => new(RouteKind.Clarify, [], "clarify", q);
}

/// <summary>
/// Conservative, anchored direct grammar (spec §13.2). A direct route requires that EVERY clause of the committed
/// instruction is understood, has no unhandled constraints/negations/corrections, and resolves uniquely.
/// Anything else goes to the cloud planner (or clarification). Clauses are never silently discarded.
/// </summary>
public sealed partial class IntentRouter(IAppCatalog? apps)
{
    private static readonly RegexOptions O = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    public RouteDecision Route(string transcript)
    {
        var text = NormalizeText(transcript);
        if (text.Length == 0) return RouteDecision.Clarify("I didn't get an instruction. What would you like me to do?");
        if (CorrectionMarker().IsMatch(text)) return RouteDecision.Cloud("contains a correction");
        if (text.Contains('"', StringComparison.Ordinal) || text.Contains('\'', StringComparison.Ordinal) && QuotedContent().IsMatch(transcript)) return RouteDecision.Cloud("contains quoted content");

        var clauses = SplitClauses(text);
        var recipes = new List<DirectRecipe>();
        foreach (var clause in clauses)
        {
            var r = MatchClause(clause, recipes);
            if (r is null) return RouteDecision.Cloud($"clause not in direct grammar: '{clause}'");
            if (r is ClarifyMarker c) return RouteDecision.Clarify(c.Question);
            if (r is not NoopRecipe) recipes.Add(r);
        }
        recipes = recipes.Distinct().ToList();
        if (recipes.Count == 0) return RouteDecision.Cloud("no actionable clause");
        if (recipes.Count > 3) return RouteDecision.Cloud("too many direct clauses");
        return new RouteDecision(RouteKind.Direct, recipes, "direct grammar");
    }

    private sealed record NoopRecipe : DirectRecipe;
    private sealed record ClarifyMarker(string Question) : DirectRecipe;

    private DirectRecipe? MatchClause(string c, List<DirectRecipe> previous)
    {
        c = LeadingFiller().Replace(c, "").Trim();
        if (c.Length == 0) return new NoopRecipe();

        // Follow-on capacity request after a free-space clause: "also tell me total capacity"
        if (TotalCapacity().IsMatch(c) && previous.OfType<FreeSpaceRecipe>().Any()) return new NoopRecipe();

        var m = FreeSpace().Match(c);
        if (m.Success && DriveLetter(m.Groups["d"].Value) is { } letter) return new FreeSpaceRecipe(letter);
        m = CheckDrive().Match(c);
        if (m.Success && DriveLetter(m.Groups["d"].Value) is { } l2) return new FreeSpaceRecipe(l2);
        if (ListDrives().IsMatch(c)) return new ListDrivesRecipe();
        if (Undo().IsMatch(c)) return new UndoRecipe();
        if (TempCleanup().IsMatch(c)) return new TempCleanupRecipe();

        m = Organize().Match(c);
        if (m.Success)
        {
            var folder = m.Groups["f"].Value.ToLowerInvariant() switch { "downloads" or "download folder" => "downloads", "documents" => "documents", _ => "desktop" };
            var rest = m.Groups["rest"].Value.Trim();
            var exclude = new List<string>();
            var include = new List<string>();
            if (rest.Length > 0 && !ByType().IsMatch(rest))
            {
                var ex = Exclusion().Match(rest);
                if (!ex.Success) return null; // an unrecognized constraint → cloud
                foreach (var part in ListSplit().Split(ex.Groups["items"].Value))
                {
                    var p = part.Trim();
                    if (p.Length == 0) continue;
                    if (p is "shortcut" or "shortcuts" or "folder" or "folders" or "the folders" or "the shortcuts") continue; // always left alone
                    var cat = FileCategories.Normalize(p);
                    if (cat is null) return null;
                    exclude.Add(cat);
                }
            }
            return new OrganizeRecipe(folder, exclude, include);
        }

        if (Negation().IsMatch(c)) return null;
        m = Launch().Match(c);
        if (m.Success && apps is not null)
        {
            var name = m.Groups["app"].Value.Trim();
            if (DriveLetter(name) is not null || name.Contains("drive", StringComparison.Ordinal) || name.Contains("folder", StringComparison.Ordinal) || name.Contains("file", StringComparison.Ordinal)) return null;
            var res = apps.Resolve(name);
            if (res.App is not null) return new LaunchAppRecipe(res.App);
            if (res.Candidates.Count > 1) return new ClarifyMarker("Which app did you mean: " + string.Join(", ", res.Candidates.Take(4).Select(a => a.DisplayName)) + "?");
            return null;
        }
        return null;
    }

    internal static string NormalizeText(string t)
    {
        t = t.Trim().ToLowerInvariant().Replace('’', '\'');
        t = Regex.Replace(t, @"[!?;]+", ".", O);
        t = Regex.Replace(t, @"\s+", " ", O);
        return t.Trim(' ', '.', ',');
    }

    internal static IReadOnlyList<string> SplitClauses(string t) =>
        ClauseSplit().Split(t).Select(s => s.Trim(' ', '.', ',')).Where(s => s.Length > 0).ToList();

    internal static string? DriveLetter(string spoken)
    {
        var s = spoken.Trim().TrimEnd(':').Trim();
        return s switch
        {
            "c" or "see" or "sea" or "cee" => "C",
            "d" or "dee" => "D",
            "e" => "E",
            "f" or "ef" => "F",
            "g" or "gee" => "G",
            _ when s.Length == 1 && char.IsAsciiLetter(s[0]) => s.ToUpperInvariant(),
            _ => null,
        };
    }

    [GeneratedRegex(@"\b(actually|instead|rather|scratch that|wait|no wait|change that|on second thought)\b", RegexOptions.IgnoreCase)]
    private static partial Regex CorrectionMarker();
    [GeneratedRegex(@"'[^']{2,}'")]
    private static partial Regex QuotedContent();
    [GeneratedRegex(@"\s*(?:\.|,?\s+and then\s+|,?\s+then\s+|,?\s+and also\s+|,?\s+also\s+|,?\s+plus\s+)\s*", RegexOptions.IgnoreCase)]
    private static partial Regex ClauseSplit();
    [GeneratedRegex(@"^(?:(?:ok|okay|so|please|hey|um|uh|and|also|plus|can you|could you|would you|i want you to|i'd like you to|tell me|let me know)\b\s*,?\s*)+", RegexOptions.IgnoreCase)]
    private static partial Regex LeadingFiller();
    [GeneratedRegex(@"^(?:check |show me |find out )?(?:how much|what(?:'s| is)(?: the)?|check(?: the)?|show(?: me)?(?: the)?)?\s*(?:free |available |remaining )?(?:disk )?space(?: is)?(?: left| free| available| remaining)?(?: on| in)?(?: the| my)?(?: drive)? (?<d>[a-z]{1,3})(?::)?(?: drive)?(?: please)?$", RegexOptions.IgnoreCase)]
    private static partial Regex FreeSpace();
    [GeneratedRegex(@"^(?:check|how full is|how big is|what(?:'s| is) the (?:capacity|size) of)(?: the| my)? (?:drive )?(?<d>[a-z]{1,3})(?::)?(?: drive)?(?: space| capacity)?(?: please)?$", RegexOptions.IgnoreCase)]
    private static partial Regex CheckDrive();
    [GeneratedRegex(@"^(?:what(?:'s| is) the |the |its |also )?(?:total )?(?:capacity|size|total size|total capacity)(?: of it| too| as well)?$", RegexOptions.IgnoreCase)]
    private static partial Regex TotalCapacity();
    [GeneratedRegex(@"^(?:list|show)(?: me)?(?: all)?(?: my| the)? (?:drives|disks)(?: and their (?:free )?space)?$", RegexOptions.IgnoreCase)]
    private static partial Regex ListDrives();
    [GeneratedRegex(@"^(?:undo|reverse|revert)(?: that| it| the last (?:organization|organisation|move|change)| what you (?:just )?did| last action)?$", RegexOptions.IgnoreCase)]
    private static partial Regex Undo();
    [GeneratedRegex(@"^(?:clean(?: up)?|clear|delete|remove)(?: my| the| all)? (?:temp|temporary) files$", RegexOptions.IgnoreCase)]
    private static partial Regex TempCleanup();
    [GeneratedRegex(@"^(?:organi[sz]e|tidy(?: up)?|sort|clean up|arrange)(?: my| the)? (?<f>desktop|downloads|download folder|documents)(?: folder)?(?<rest>.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex Organize();
    [GeneratedRegex(@"^,?\s*(?:by|into folders by|according to)(?: file)? (?:type|extension|kind)s?$", RegexOptions.IgnoreCase)]
    private static partial Regex ByType();
    [GeneratedRegex(@"^,?\s*(?:(?:by|into folders by)(?: file)? (?:type|extension|kind)s?,?\s*)?(?:and\s+|but\s+)?(?:leave|keep|except(?: for)?|excluding|but (?:leave|keep|not)|don't (?:move|touch)|do not (?:move|touch)|without(?: moving)?)\s+(?:the |my )?(?<items>[a-z0-9 ,&]+?)(?:\s+(?:alone|where they are|in place|untouched))?$", RegexOptions.IgnoreCase)]
    private static partial Regex Exclusion();
    [GeneratedRegex(@"\s*(?:,|\band\b|\bor\b|&)\s*(?:the |my )?", RegexOptions.IgnoreCase)]
    private static partial Regex ListSplit();
    [GeneratedRegex(@"\b(don't|do not|never|not|no|without|except)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Negation();
    [GeneratedRegex(@"^(?:open|launch|start|run|bring up|fire up)(?: up)?(?: the| my)? (?<app>[a-z0-9+#. \-]{1,60}?)(?: app| application| program)?(?: please)?$", RegexOptions.IgnoreCase)]
    private static partial Regex Launch();
}
