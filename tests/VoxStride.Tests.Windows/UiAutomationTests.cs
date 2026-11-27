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
[Collection("Desktop")] // UI tests share one desktop and foreground window: never run them in parallel
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

/// <summary>P4 gate: ≥100 attempts per workflow class with ≥95% verified success, and a hung app never hangs VoxStride.</summary>
[Collection("Desktop")]
public sealed class UiAutomationGateTests(ITestOutputHelper output) : IDisposable
{
    private readonly WindowsUiAutomation _ui = new();
    private Process? _fixture;

    private async Task<UiWindow> Start(string title)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "VoxStride.sln"))) dir = dir.Parent;
        var exe = Directory.GetFiles(Path.Combine(dir!.FullName, "tests", "VoxStride.UiFixture", "bin"), "VoxStride.UiFixture.exe", SearchOption.AllDirectories).OrderByDescending(File.GetLastWriteTimeUtc).First();
        _fixture = Process.Start(new ProcessStartInfo(exe, $"\"{title}\"") { UseShellExecute = false })!;
        return await UiFixtureReady.WaitAsync(_ui, _fixture, title);
    }

    [Fact]
    public async Task Gate_100AttemptsPerWorkflowClass_AtLeast95PercentVerified()
    {
        var w = await Start("Gate " + Guid.NewGuid().ToString("N")[..6]);
        int setOk = 0, invokeOk = 0, toggleOk = 0;
        const int n = 100;
        for (var i = 0; i < n; i++)
        {
            var snap = await _ui.ObserveAsync(w.Id, null, 150, CancellationToken.None);
            if (snap is null) continue;
            var box = snap.Elements.First(e => e.Name == "Your name" && e.Role == "edit");
            var name = "N" + i;
            var set = await _ui.SetValueAsync(w.Id, box.Id, name, CancellationToken.None);
            if (set.Ok && set.NewValue == name) setOk++;
            var greet = snap.Elements.First(e => e.Name == "Greet");
            if ((await _ui.InvokeAsync(w.Id, greet.Id, "invoke", CancellationToken.None)).Ok)
            {
                for (var t = 0; t < 20; t++)
                {
                    var check = await _ui.ObserveAsync(w.Id, "Hello", 50, CancellationToken.None);
                    if (check?.Elements.Any(e => e.Name == $"Hello, {name}!") == true) { invokeOk++; break; }
                    await Task.Delay(25);
                }
            }
            snap = await _ui.ObserveAsync(w.Id, null, 150, CancellationToken.None); // ids from the full observation (the filtered one replaced it)
            var sub = snap!.Elements.First(e => e.Name == "Subscribe");
            var tog = await _ui.InvokeAsync(w.Id, sub.Id, "toggle", CancellationToken.None);
            if (tog.Ok && tog.Verified) toggleOk++;
        }
        output.WriteLine($"E2E-UI-GATE: set-value {setOk}/{n}, invoke+verify {invokeOk}/{n}, toggle {toggleOk}/{n}");
        Assert.True(setOk >= 95, $"set value {setOk}/{n}");
        Assert.True(invokeOk >= 95, $"invoke {invokeOk}/{n}");
        Assert.True(toggleOk >= 95, $"toggle {toggleOk}/{n}");
    }

    [Fact]
    public async Task HungAppTimesOutWithoutBlockingVoxStride()
    {
        var w = await Start("Hang " + Guid.NewGuid().ToString("N")[..6]);
        var snap = await _ui.ObserveAsync(w.Id, null, 150, CancellationToken.None);
        var freeze = snap!.Elements.First(e => e.Name == "Freeze");
        var sw = Stopwatch.StartNew();
        var r = await _ui.InvokeAsync(w.Id, freeze.Id, "invoke", CancellationToken.None); // the app blocks its UI thread for 20 s
        var observeHung = await _ui.ObserveAsync(w.Id, null, 150, CancellationToken.None);
        var elapsed = sw.Elapsed;
        // Other windows still work immediately: the hung worker was replaced.
        var list = await _ui.ListWindowsAsync(CancellationToken.None);
        output.WriteLine($"E2E-UI: hung app — invoke ok={r.Ok} error={r.Error}, observe={(observeHung is null ? "timed out" : "answered")}, total {elapsed.TotalSeconds:0.0}s, windows still listed: {list.Count}");
        Assert.True(elapsed < TimeSpan.FromSeconds(19), "calls into a hung app must time out, not wait for it");
        Assert.NotEmpty(list);
    }

    public void Dispose()
    {
        try { if (_fixture is { HasExited: false }) _fixture.Kill(); } catch (Exception) { }
        _fixture?.Dispose();
        _ui.Dispose();
    }
}

[Collection("Desktop")]
public sealed class UiElementIdTests : IDisposable
{
    private readonly WindowsUiAutomation _ui = new();
    private Process? _p;

    [Fact]
    public async Task IdsFromAnOlderObservationNeverAliasNewControls()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "VoxStride.sln"))) dir = dir.Parent;
        var exe = Directory.GetFiles(Path.Combine(dir!.FullName, "tests", "VoxStride.UiFixture", "bin"), "VoxStride.UiFixture.exe", SearchOption.AllDirectories).OrderByDescending(File.GetLastWriteTimeUtc).First();
        var title = "Ids " + Guid.NewGuid().ToString("N")[..6];
        _p = Process.Start(new ProcessStartInfo(exe, "\"" + title + "\"") { UseShellExecute = false })!;
        var w = await UiFixtureReady.WaitAsync(_ui, _p, title);
        var full = await _ui.ObserveAsync(w.Id, null, 150, CancellationToken.None);
        var subscribe = full!.Elements.First(e => e.Name == "Subscribe");
        var filtered = await _ui.ObserveAsync(w.Id, "Greet", 150, CancellationToken.None);
        Assert.DoesNotContain(filtered!.Elements, e => e.Id == subscribe.Id);
        var stale = await _ui.InvokeAsync(w.Id, subscribe.Id, "toggle", CancellationToken.None);
        Assert.Equal("element_stale", stale.Error); // refused, not applied to whatever control now has that id
    }

    public void Dispose() { try { if (_p is { HasExited: false }) _p.Kill(); } catch (Exception) { } _p?.Dispose(); _ui.Dispose(); }
}

/// <summary>
/// Waits until the fixture's main window exists (by process AND title) and its controls are exposed to UI Automation.
/// On a busy CI runner the window can be listed before WinForms has created its controls, which made observations
/// taken too early come back without "Greet"/"Subscribe".
/// </summary>
internal static class UiFixtureReady
{
    public static async Task<UiWindow> WaitAsync(WindowsUiAutomation ui, Process p, string title, int timeoutMs = 30000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            await Task.Delay(150);
            var w = (await ui.ListWindowsAsync(CancellationToken.None)).FirstOrDefault(x => x.ProcessId == p.Id && x.Title == title);
            if (w is null) continue;
            var snap = await ui.ObserveAsync(w.Id, null, 150, CancellationToken.None);
            if (snap is not null && snap.Elements.Any(e => e.Name == "Greet") && snap.Elements.Any(e => e.Name == "Your name")) return w;
        }
        throw new TimeoutException($"fixture \"{title}\" did not become ready in {timeoutMs} ms");
    }
}
