using System.IO.Compression;
using System.Text.Json.Nodes;
using VoxStride.Core.Tools;
using Xunit;

namespace VoxStride.Tests;

public sealed class XlsxTests
{
    [Fact]
    public void WorkbookHasTheRequiredPartsAndCells()
    {
        var bytes = Xlsx.Build([["City", "Max °C", "Note"], ["Paris", "21.5", "Sunny & warm <ok>"], ["Lyon", "-3", "0123"]], "France weather");
        using var zip = new ZipArchive(new MemoryStream(bytes));
        foreach (var part in new[] { "[Content_Types].xml", "_rels/.rels", "xl/workbook.xml", "xl/_rels/workbook.xml.rels", "xl/styles.xml", "xl/worksheets/sheet1.xml" })
            Assert.NotNull(zip.GetEntry(part));
        var sheet = new StreamReader(zip.GetEntry("xl/worksheets/sheet1.xml")!.Open()).ReadToEnd();
        var doc = System.Xml.Linq.XDocument.Parse(sheet); // well-formed XML
        Assert.Contains("<c r=\"B2\"><v>21.5</v></c>", sheet);
        Assert.Contains("<c r=\"B3\"><v>-3</v></c>", sheet);
        Assert.Contains("Sunny &amp; warm &lt;ok&gt;", sheet);
        Assert.Contains("<c r=\"C3\" t=\"inlineStr\"><is><t xml:space=\"preserve\">0123</t>", sheet); // leading zero stays text
        Assert.Contains("s=\"1\"", sheet); // bold header
        var wb = new StreamReader(zip.GetEntry("xl/workbook.xml")!.Open()).ReadToEnd();
        Assert.Contains("name=\"France weather\"", wb);
        Assert.NotNull(doc.Root);
    }

    [Theory]
    [InlineData(0, "A")]
    [InlineData(25, "Z")]
    [InlineData(26, "AA")]
    [InlineData(701, "ZZ")]
    [InlineData(702, "AAA")]
    public void ColumnNames(int i, string expected) => Assert.Equal(expected, Xlsx.Column(i));

    [Fact]
    public void CsvQuotesCommasAndQuotes() =>
        Assert.Equal("a,\"b,c\",\"say \"\"hi\"\"\"\r\n", CreateDocumentTool.Csv([["a", "b,c", "say \"hi\""]]));

    [Fact]
    public void NeverOverwritesAnExistingName()
    {
        var dir = Directory.CreateTempSubdirectory("vx-doc-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "France weather.xlsx"), "x");
            Assert.Equal(Path.Combine(dir, "France weather (2).xlsx"), CreateDocumentTool.FreeName(dir, "France weather", "xlsx"));
        }
        finally { Directory.Delete(dir, true); }
    }
}

public sealed class ScreenLookPolicyTests
{
    private sealed class FakeCapture : IScreenCapture
    {
        public Task<ScreenShot?> CaptureScreenAsync(CancellationToken ct) => Task.FromResult<ScreenShot?>(new ScreenShot([1, 2, 3], "image/png", 1280, 720, "Excel"));
    }

    private static VoxStride.Core.Execution.ToolContext Ctx() =>
        new(new VoxStride.Core.Execution.RunAuthority(TimeProvider.System).Mint(
                new VoxStride.Contracts.CommandEnvelope(Guid.NewGuid(), Guid.NewGuid(), "look", VoxStride.Core.Execution.RunAuthority.HashTranscript("look"), "en-US", [], DateTimeOffset.UtcNow, VoxStride.Contracts.InputMode.Typed, null, null),
                ["screen_look"], TimeSpan.FromMinutes(1)),
            new VoxStride.Core.Execution.InMemoryJournal(), new VoxStride.Tests.RecordingSink(), "s1", () => true);

    [Theory]
    [InlineData(VoxStride.Core.Policy.ScreenCapturePolicy.WhenNeeded, true, false)]
    [InlineData(VoxStride.Core.Policy.ScreenCapturePolicy.AskEachTime, true, true)]
    [InlineData(VoxStride.Core.Policy.ScreenCapturePolicy.Never, false, false)]
    public async Task PolicyDecidesWhetherToAsk(VoxStride.Core.Policy.ScreenCapturePolicy policy, bool ok, bool asks)
    {
        var tool = new ScreenLookTool(new FakeCapture(), () => new VoxStride.Core.Policy.UserPermissions(screenCapture: () => policy));
        var v = await tool.ValidateAsync(new JsonObject { ["reason"] = "user asked" }, Ctx(), CancellationToken.None);
        Assert.Equal(ok, v.Ok);
        Assert.Equal(asks, v.ApprovalNeeded is not null);
    }

    [Fact]
    public async Task PictureIsHandedOverByReferenceNotInline()
    {
        var tool = new ScreenLookTool(new FakeCapture(), () => new VoxStride.Core.Policy.UserPermissions(screenCapture: () => VoxStride.Core.Policy.ScreenCapturePolicy.WhenNeeded));
        var r = await tool.ExecuteAsync(new JsonObject { ["reason"] = "x" }, Ctx(), CancellationToken.None);
        var id = r.ObservedState!["imageRef"]!.GetValue<string>();
        Assert.True(r.ObservedState["untrusted"]!.GetValue<bool>());
        var img = CapturedImages.TakeWithType(id);
        Assert.NotNull(img);
        Assert.Equal("image/png", img!.Value.Mime);
    }
}

public sealed class CompanionMemoryTests
{
    [Fact]
    public void RemembersForgetsAndSurvivesRestart()
    {
        var dir = Directory.CreateTempSubdirectory("vx-mem-").FullName;
        try
        {
            var path = Path.Combine(dir, "memory.json");
            var m = new CompanionMemory(path);
            Assert.True(m.Add("The user's sister is called Ayesha."));
            Assert.True(m.Add("The user likes short answers."));
            Assert.True(m.Add("the user likes short answers.")); // duplicate ignored
            Assert.Equal(2, new CompanionMemory(path).All().Count);
            Assert.Contains("- The user's sister is called Ayesha.", m.AsPrompt());
            Assert.Equal(1, m.Forget("sister"));
            Assert.DoesNotContain("Ayesha", new CompanionMemory(path).AsPrompt());
            Assert.False(m.Add(""));
        }
        finally { Directory.Delete(dir, true); }
    }
}
