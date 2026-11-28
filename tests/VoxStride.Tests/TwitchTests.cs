using VoxStride.Core.Live;
using Xunit;

namespace VoxStride.Tests;

public sealed class TwitchIrcTests
{
    [Fact]
    public void ParsesChatWithDisplayName()
    {
        var e = TwitchIrc.Parse("@badge-info=;display-name=Ayesha_99;mod=0 :ayesha_99!ayesha_99@ayesha_99.tmi.twitch.tv PRIVMSG #nisar :hello VoxStride, nice play!");
        Assert.NotNull(e);
        Assert.Equal(TwitchEventKind.Chat, e!.Kind);
        Assert.Equal("Ayesha_99", e.User);
        Assert.Equal("hello VoxStride, nice play!", e.Text);
    }

    [Fact]
    public void ParsesSubsResubsGiftsRaidsAndCheers()
    {
        Assert.Equal(TwitchEventKind.Sub, TwitchIrc.Parse("@display-name=Ali;msg-id=sub :tmi.twitch.tv USERNOTICE #nisar")!.Kind);
        var re = TwitchIrc.Parse("@display-name=Ali;msg-id=resub;msg-param-cumulative-months=7 :tmi.twitch.tv USERNOTICE #nisar :7 months!")!;
        Assert.Equal((TwitchEventKind.Resub, 7, "7 months!"), (re.Kind, re.Amount, re.Text));
        Assert.Equal(5, TwitchIrc.Parse("@display-name=Sara;msg-id=submysterygift;msg-param-mass-gift-count=5 :tmi.twitch.tv USERNOTICE #nisar")!.Amount);
        var raid = TwitchIrc.Parse("@display-name=raider;msg-id=raid;msg-param-displayName=BigStreamer;msg-param-viewerCount=120 :tmi.twitch.tv USERNOTICE #nisar")!;
        Assert.Equal(("BigStreamer", 120), (raid.User, raid.Amount));
        var cheer = TwitchIrc.Parse("@display-name=Zed;bits=100 :zed!zed@zed.tmi.twitch.tv PRIVMSG #nisar :cheer100 gg")!;
        Assert.Equal((TwitchEventKind.Cheer, 100), (cheer.Kind, cheer.Amount));
        Assert.Null(TwitchIrc.Parse(":tmi.twitch.tv 001 justinfan123 :Welcome, GLHF!"));
        Assert.Null(TwitchIrc.Parse("@msg-id=unknownthing :tmi.twitch.tv USERNOTICE #nisar"));
    }

    [Fact]
    public void ViewerTextIsCleaned()
    {
        Assert.Equal("go to [link] now", TwitchIrc.Clean("go to https://evil.example/x?y=1 now"));
        Assert.EndsWith("…", TwitchIrc.Clean(new string('a', 300)));
        Assert.Equal("ab", TwitchIrc.Clean("a\u0007b"));
    }

    [Theory]
    [InlineData("nisar87", true)]
    [InlineData("#Nisar87", true)]
    [InlineData("ab", false)]
    [InlineData("bad name", false)]
    [InlineData(null, false)]
    public void ChannelNames(string? name, bool ok) => Assert.Equal(ok, TwitchIrc.IsValidChannel(name));
}

public sealed class TwitchCoHostTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.Parse("2026-11-28T12:00:00Z", global::System.Globalization.CultureInfo.InvariantCulture);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public async Task EventsGoFirstChatIsBatchedAndMarkedAsNotTheUser()
    {
        var sent = new List<string>();
        var clock = new Clock();
        var quiet = true;
        var host = new TwitchCoHost(() => quiet, t => { sent.Add(t); return Task.CompletedTask; }, clock, TimeSpan.FromSeconds(20));
        host.Add(new TwitchEvent(TwitchEventKind.Chat, "Ali", "hi!"));
        host.Add(new TwitchEvent(TwitchEventKind.Chat, "Nightbot", "Follow the channel!"));
        host.Add(new TwitchEvent(TwitchEventKind.Chat, "Sara", "!so someone"));
        host.Add(new TwitchEvent(TwitchEventKind.Sub, "Zed", ""));
        await host.TickAsync();
        Assert.Single(sent);
        Assert.Contains("Zed just subscribed!", sent[0]);
        Assert.Contains("not the user", sent[0]);
        await host.TickAsync();                         // chat waits for its interval
        Assert.Single(sent);
        clock.Now = clock.Now.AddSeconds(21);
        await host.TickAsync();
        Assert.Equal(2, sent.Count);
        Assert.Contains("Ali: \"hi!\"", sent[1]);
        Assert.DoesNotContain("Nightbot", sent[1]);
        Assert.DoesNotContain("!so", sent[1]);
        Assert.Contains("never use tools", sent[1]);
    }

    [Fact]
    public async Task NothingIsSentWhileTheUserIsTalking()
    {
        var sent = 0;
        var host = new TwitchCoHost(() => false, _ => { sent++; return Task.CompletedTask; }, new Clock(), TimeSpan.Zero);
        host.Add(new TwitchEvent(TwitchEventKind.Raid, "Big", "", 50));
        await host.TickAsync();
        Assert.Equal(0, sent);
    }
}
