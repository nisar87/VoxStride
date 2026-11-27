using System.Diagnostics;
using VoxStride.Core.Tools;
using VoxStride.Windows;
using Xunit;
using Xunit.Abstractions;

namespace VoxStride.Tests.Windows;

/// <summary>
/// Real UI Automation against the test fixture app (tests/VoxStride.UiFixture) on the Windows desktop session.
/// Each test writes an "E2E-UI" line so CI logs show what actually ran.
/// </summary>
public sealed class UiAutomationTests(ITestOutputHelper output) : IDisposable
{
    private readonly WindowsUiAutomation _ui = new();
    private readonly List<Process> _started = [];

    private static string FixturePath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "VoxStride.sln"))) dir = dir.Parent;
        if (dir is null) throw new InvalidOperationException("repo root not found");
        var exe = Directory.GetFiles(Path.Combine(dir.FullName, "tests", "VoxStride.UiFixture", "bin"), "VoxStride.UiFixture.exe", SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
        return exe ?? throw new FileNotFoundException("UI fixture not built");
    }

    private async Task<UiWindow> StartFixture(string title)
    {
        var p = Process.Start(new ProcessStartInfo(FixturePath(), $"\"{title}\"") { UseShellExecute = false })!;
        _started.Add(p);
        for (var i = 0; i < 100; i++)
        {
            await Task.Delay(100);
            var w = (await _ui.ListWindowsAsync(CancellationToken.None)).FirstOrDefault(x => x.ProcessId == p.Id && x.Title == title);
            if (w is not null) return w;
        }
        throw new TimeoutException("fixture window did not appear");
    }

    private static UiElement Find(UiSnapshot s, string name, string? role = null) =>
        s.Elements.FirstOrDefault(e => e.Name == name && (role is null || e.Role == role)) ?? throw new InvalidOperationException($"element '{name}' not found in: " + string.Join(", ", s.Elements.Select(e => $"{e.Role}:{e.Name}")));

    [Fact]
    public async Task ObserveSetValueInvokeAndVerifyTheResult()
    {
        var w = await StartFixture("Fixture A " + Guid.NewGuid().ToString("N")[..6]);
        var snap = await _ui.ObserveAsync(w.Id, null, 150, CancellationToken.None);
        Assert.NotNull(snap);
        output.WriteLine("E2E-UI: observed " + string.Join(", ", snap.Elements.Select(e => $"{e.Id}:{e.Role}:{e.Name}[{string.Join("/", e.Actions)}]")));
        var box = Find(snap, "Your name", "edit");
        Assert.Contains("setvalue", box.Actions);
        var set = await _ui.SetValueAsync(w.Id, box.Id, "Nisar", CancellationToken.None);
        Assert.True(set.Ok, set.Message);
        Assert.Equal("Nisar", set.NewValue);
        var greet = Find(snap, "Greet", "button");
        var press = await _ui.InvokeAsync(w.Id, greet.Id, "invoke", CancellationToken.None);
        Assert.True(press.Ok, press.Message);
        await Task.Delay(300);
        var after = await _ui.ObserveAsync(w.Id, "Hello", 150, CancellationToken.None);
        Assert.Contains(after!.Elements, e => e.Name == "Hello, Nisar!");
        output.WriteLine("E2E-UI: ran — set value, pressed Greet, label reads 'Hello, Nisar!'");
    }

    [Fact]
    public async Task ToggleReportsTheNewState()
    {
        var w = await StartFixture("Fixture B " + Guid.NewGuid().ToString("N")[..6]);
        var snap = await _ui.ObserveAsync(w.Id, null, 150, CancellationToken.None);
        var box = Find(snap!, "Subscribe");
        var r = await _ui.InvokeAsync(w.Id, box.Id, "toggle", CancellationToken.None);
        Assert.True(r.Ok && r.Verified, r.Message);
        Assert.Equal("On", r.NewValue);
        output.WriteLine("E2E-UI: ran — checkbox toggled On and verified");
    }

    [Fact]
    public async Task TypeTextGoesIntoTheFocusedControlAndIsVerified()
    {
        var w = await StartFixture("Fixture C " + Guid.NewGuid().ToString("N")[..6]);
        Assert.True((await _ui.FocusAsync(w.Id, CancellationToken.None)).Ok, "fixture could not be brought to the front");
        var snap = await _ui.ObserveAsync(w.Id, null, 150, CancellationToken.None);
        var notes = Find(snap!, "Notes", "edit");
        var r = await _ui.TypeTextAsync(w.Id, notes.Id, "Typed by VoxStride ✓ اردو", CancellationToken.None);
        Assert.True(r.Ok, r.Message);
        Assert.True(r.Verified, "typed text should read back");
        output.WriteLine("E2E-UI: ran — typed Unicode text and read it back");
    }

    [Fact]
    public async Task CaptureAndClickFromTheScreenshot()
    {
        var w = await StartFixture("Fixture D " + Guid.NewGuid().ToString("N")[..6]);
        Assert.True((await _ui.FocusAsync(w.Id, CancellationToken.None)).Ok);
        var snap = await _ui.ObserveAsync(w.Id, null, 150, CancellationToken.None);
        var greet = Find(snap!, "Greet", "button");
        var cap = await _ui.CaptureAsync(w.Id, CancellationToken.None);
        Assert.NotNull(cap);
        Assert.Equal((byte)0x89, cap.Png[0]);
        Assert.True(cap.Png.Length > 1000);
        // Map the button's screen centre into screenshot pixels, as the model would from the image.
        var scale = (double)cap.Width / w.Width;
        var x = (int)((greet.X + greet.Width / 2.0 - w.X) * scale);
        var y = (int)((greet.Y + greet.Height / 2.0 - w.Y) * scale);
        var click = await _ui.ClickAsync(w.Id, cap, x, y, CancellationToken.None);
        Assert.True(click.Ok, click.Message);
        await Task.Delay(300);
        var after = await _ui.ObserveAsync(w.Id, "Hello", 150, CancellationToken.None);
        Assert.Contains(after!.Elements, e => e.Name == "Hello, !");
        output.WriteLine($"E2E-UI: ran — captured {cap.Width}x{cap.Height} PNG ({cap.Png.Length / 1024} KB) and clicked Greet from it");
    }

    [Fact]
    public async Task ClosedWindowIsReportedGone()
    {
        var w = await StartFixture("Fixture E " + Guid.NewGuid().ToString("N")[..6]);
        _started[^1].Kill();
        _started[^1].WaitForExit(5000);
        Assert.Null(await _ui.GetWindowAsync(w.Id, CancellationToken.None));
        var r = await _ui.InvokeAsync(w.Id, "e1", "invoke", CancellationToken.None);
        Assert.Equal("window_gone", r.Error);
        output.WriteLine("E2E-UI: ran — closed window detected");
    }

    public void Dispose()
    {
        foreach (var p in _started) { try { if (!p.HasExited) p.Kill(); } catch (Exception) { } p.Dispose(); }
        _ui.Dispose();
    }
}
