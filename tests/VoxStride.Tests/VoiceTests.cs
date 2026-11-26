using VoxStride.Contracts;
using VoxStride.Core.Voice;
using Xunit;

namespace VoxStride.Tests;

public sealed class ControlPhraseTests
{
    private readonly ControlPhraseDetector _d = new(new ControlPhraseOptions());

    [Theory]
    [InlineData("VoxStride, execute.")]
    [InlineData("voxstride execute")]
    [InlineData("Okay, VoxStride proceed")]
    [InlineData("Assistant, proceed!")]
    [InlineData("execute now")]
    public void StandaloneFinishIsRecognized(string s) => Assert.Equal(ControlKind.Finish, _d.Classify(s).Kind);

    [Fact]
    public void TrailingDistinctiveFinishSplitsContent()
    {
        var r = _d.Classify("Organize my desktop and leave shortcuts. VoxStride, execute");
        Assert.Equal(ControlKind.Finish, r.Kind);
        Assert.Equal("Organize my desktop and leave shortcuts", r.Content);
    }

    [Theory]
    [InlineData("write the sentence 'please proceed' in Notepad++")]
    [InlineData("type \"VoxStride execute\" into notepad")]
    [InlineData("proceed with caution is a nice phrase")]
    [InlineData("I want to proceed with the plan tomorrow and then")]
    public void ContentMentioningControlWordsIsNotAFinish(string s) =>
        Assert.True(_d.Classify(s).Kind is ControlKind.Content or ControlKind.FinishUncertain);

    [Theory]
    [InlineData("write the sentence voxstride execute")]
    [InlineData("type the words assistant proceed")]
    [InlineData("don't voxstride execute")]
    public void RequestedAsContentOrNegatedIsUncertainNeverFinish(string s) =>
        Assert.Equal(ControlKind.FinishUncertain, _d.Classify(s).Kind);

    [Theory]
    [InlineData("proceed")]
    [InlineData("go ahead")]
    public void PlainProceedIsNotFinishByDefault(string s) => Assert.NotEqual(ControlKind.Finish, _d.Classify(s).Kind);

    [Fact]
    public void PlainProceedIsFinishWhenEnabledAndStandalone()
    {
        var d = new ControlPhraseDetector(new ControlPhraseOptions { AllowPlainProceed = true });
        Assert.Equal(ControlKind.Finish, d.Classify("Proceed.").Kind);
        Assert.NotEqual(ControlKind.Finish, d.Classify("then proceed to open the file").Kind);
    }

    [Fact]
    public void LowConfidenceFinishIsUncertain() => Assert.Equal(ControlKind.FinishUncertain, _d.Classify("voxstride execute", 0.3).Kind);

    [Theory]
    [InlineData("cancel", ControlKind.Cancel)]
    [InlineData("Never mind.", ControlKind.Cancel)]
    [InlineData("stop listening", ControlKind.Stop)]
    [InlineData("scratch that", ControlKind.UndoLast)]
    [InlineData("undo last sentence", ControlKind.UndoLast)]
    public void EditingControls(string s, ControlKind k) => Assert.Equal(k, _d.Classify(s).Kind);

    [Fact]
    public void ReplaceKeepsOriginalCasing()
    {
        var r = _d.Classify("Replace that with Move only the PDF files");
        Assert.Equal(ControlKind.Replace, r.Kind);
        Assert.Equal("Move only the PDF files", r.Content);
    }

    [Fact]
    public void CancelInsideSentenceIsContent() => Assert.Equal(ControlKind.Content, _d.Classify("cancel my 3pm meeting reminder note").Kind);
}

public sealed class CommandBufferTests
{
    private static readonly Guid S = Guid.NewGuid();
    private static TranscriptEvent E(string id, int rev, bool fin, string text, long start, long seq = 0) => new(S, id, seq, rev, fin, text, start, start + 500);

    [Fact]
    public void V04_RevisionsReplaceAndOutOfOrderFinalsAreSortedByTime()
    {
        var b = new CommandBuffer(S);
        b.Apply(E("a", 1, false, "organize my", 0));
        b.Apply(E("a", 2, false, "organize my desk", 0));
        b.Apply(E("b", 1, true, "leave shortcuts", 4000));
        b.Apply(E("a", 3, true, "organize my desktop", 0));
        b.Apply(E("a", 2, false, "organize my desk", 0)); // stale interim after final
        b.Apply(E("a", 3, true, "organize my desktop", 0)); // duplicate
        Assert.Equal("organize my desktop. leave shortcuts", b.FinalText());
        Assert.Equal(0, b.PendingCount);
    }

    [Fact]
    public void ForeignSessionEventsAreIgnored()
    {
        var b = new CommandBuffer(S);
        Assert.False(b.Apply(new TranscriptEvent(Guid.NewGuid(), "x", 0, 1, true, "rm -rf", 0, 1)));
        Assert.Equal("", b.FinalText());
    }

    [Fact]
    public void UndoAndReplaceEditTheLastSentence()
    {
        var b = new CommandBuffer(S);
        b.Apply(E("a", 1, true, "move pdfs", 0));
        b.Apply(E("b", 1, true, "and zips", 1000));
        Assert.True(b.UndoLast());
        Assert.Equal("move pdfs", b.FinalText());
        Assert.True(b.ReplaceLast("move pictures"));
        Assert.Equal("move pictures", b.FinalText());
    }

    [Fact]
    public void PendingInterimIsTracked()
    {
        var b = new CommandBuffer(S);
        b.Apply(E("a", 1, true, "check C", 0));
        b.Apply(E("b", 1, false, "also total", 2000));
        Assert.Equal(1, b.PendingCount);
        Assert.Equal("check C", b.FinalText());
        Assert.Equal("check C. also total", b.DisplayText);
    }

    [Fact]
    public void FinalTextBeforeFinishPointExcludesLaterAudio()
    {
        var b = new CommandBuffer(S);
        b.Apply(E("a", 1, true, "check C", 0));
        b.Apply(E("z", 1, true, "tv says buy now", 9000));
        Assert.Equal("check C", b.FinalText(5000));
    }
}
