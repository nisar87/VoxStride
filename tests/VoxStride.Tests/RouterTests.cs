using VoxStride.Core.Routing;
using VoxStride.Core.Tools;
using Xunit;

namespace VoxStride.Tests;

public sealed class FakeCatalog : IAppCatalog
{
    private readonly List<AppEntry> _apps =
    [
        new("notepad", "Notepad", "notepad.exe", ["notepad"], "test"),
        new("npp", "Notepad++", "notepad++.exe", ["notepad++", "notepad plus plus"], "test"),
        new("chrome", "Google Chrome", "chrome.exe", ["chrome", "google chrome"], "test"),
        new("calc", "Calculator", "calc.exe", ["calculator", "calc"], "test"),
        new("vs1", "Visual Studio 2026", "devenv.exe", ["visual studio"], "test"),
        new("vs2", "Visual Studio Code", "code.exe", ["visual studio code", "vs code", "code"], "test"),
    ];
    public bool IsReady => true;
    public IReadOnlyList<AppEntry> Snapshot() => _apps;
    public AppResolution Resolve(string name)
    {
        var n = name.Trim().ToLowerInvariant();
        var exact = _apps.Where(a => a.DisplayName.Equals(n, StringComparison.OrdinalIgnoreCase) || a.Aliases.Contains(n)).ToList();
        if (exact.Count == 1) return new AppResolution(exact[0], exact);
        var partial = _apps.Where(a => a.DisplayName.Contains(n, StringComparison.OrdinalIgnoreCase)).ToList();
        return partial.Count == 1 ? new AppResolution(partial[0], partial) : new AppResolution(null, partial);
    }
}

public sealed class RouterTests
{
    private readonly IntentRouter _r = new(new FakeCatalog());

    [Theory]
    [InlineData("How much space is left on C?", "C")]
    [InlineData("how much free space on the C drive", "C")]
    [InlineData("check C drive", "C")]
    [InlineData("Check how much space is left on see drive.", "C")]
    [InlineData("what's the free space on D", "D")]
    [InlineData("Check how much space is left on C. Also tell me total capacity", "C")]
    [InlineData("free space on C drive please", "C")]
    public void FreeSpaceIsDirect(string text, string letter)
    {
        var d = _r.Route(text);
        Assert.Equal(RouteKind.Direct, d.Kind);
        Assert.Equal(letter, Assert.Single(d.Recipes.OfType<FreeSpaceRecipe>()).DriveLetter);
    }

    [Theory]
    [InlineData("open notepad++", "Notepad++")]
    [InlineData("Launch Notepad", "Notepad")]
    [InlineData("please open the calculator app", "Calculator")]
    [InlineData("start google chrome", "Google Chrome")]
    public void LaunchIsDirectWhenUnique(string text, string app)
    {
        var d = _r.Route(text);
        Assert.Equal(RouteKind.Direct, d.Kind);
        Assert.Equal(app, Assert.Single(d.Recipes.OfType<LaunchAppRecipe>()).App.DisplayName);
    }

    [Theory]
    [InlineData("don't open chrome")]
    [InlineData("do not open notepad")]
    [InlineData("open C drive and clean it")]
    [InlineData("open notepad++ and write a paragraph about renewable energy")]
    [InlineData("write a poem")]
    [InlineData("play the third video in the top row on youtube for lofi music")]
    [InlineData("open my resume file")]
    [InlineData("move PDFs. actually leave PDFs alone and move pictures")]
    [InlineData("organize my desktop but group them by project")]
    [InlineData("organize my desktop, leave the receipts alone")]
    [InlineData("open the C drive")]
    [InlineData("delete everything in downloads")]
    public void AnythingNotFullyUnderstoodGoesToCloud(string text) =>
        Assert.Equal(RouteKind.Cloud, _r.Route(text).Kind);

    [Fact]
    public void AmbiguousAppAsksForClarification()
    {
        var d = _r.Route("open visual studio thing");
        Assert.NotEqual(RouteKind.Direct, d.Kind);
        var c = _r.Route("open visual");
        Assert.Equal(RouteKind.Clarify, c.Kind);
        Assert.Contains("Visual Studio", c.Question);
    }

    [Theory]
    [InlineData("organize my desktop", 0)]
    [InlineData("Organize my desktop by file type", 0)]
    [InlineData("organize desktop, leave shortcuts", 0)]
    [InlineData("tidy up my desktop but leave the PDFs alone", 1)]
    [InlineData("organize my desktop by file type and leave zip files and pictures where they are", 2)]
    [InlineData("sort my desktop except videos", 1)]
    public void OrganizeWithExclusionsIsDirect(string text, int excluded)
    {
        var d = _r.Route(text);
        Assert.Equal(RouteKind.Direct, d.Kind);
        var o = Assert.Single(d.Recipes.OfType<OrganizeRecipe>());
        Assert.Equal("desktop", o.Folder);
        Assert.Equal(excluded, o.ExcludeCategories.Count);
    }

    [Fact]
    public void ExclusionCategoriesAreMapped()
    {
        var o = Assert.Single(_r.Route("organize my desktop and leave zip files and pictures alone").Recipes.OfType<OrganizeRecipe>());
        Assert.Contains("Archives", o.ExcludeCategories);
        Assert.Contains("Images", o.ExcludeCategories);
    }

    [Fact]
    public void MultiClauseDirectCombinesRecipes()
    {
        var d = _r.Route("check C drive and then open notepad");
        Assert.Equal(RouteKind.Direct, d.Kind);
        Assert.Equal(2, d.Recipes.Count);
    }

    [Fact]
    public void MultiClauseWithUnknownClauseNeverDropsIt()
    {
        var d = _r.Route("check C drive and then email the result to my boss");
        Assert.Equal(RouteKind.Cloud, d.Kind);
    }

    [Theory]
    [InlineData("undo that")]
    [InlineData("undo the last organization")]
    public void UndoIsDirect(string text) => Assert.IsType<UndoRecipe>(Assert.Single(_r.Route(text).Recipes));

    [Fact]
    public void TempCleanupIsDirect() => Assert.IsType<TempCleanupRecipe>(Assert.Single(_r.Route("clean up my temp files").Recipes));

    [Fact]
    public void EmptyInstructionAsksForClarification() => Assert.Equal(RouteKind.Clarify, _r.Route("  ").Kind);

    [Fact]
    public void NoCatalogMeansLaunchGoesToCloud() => Assert.Equal(RouteKind.Cloud, new IntentRouter(null).Route("open notepad").Kind);
}

