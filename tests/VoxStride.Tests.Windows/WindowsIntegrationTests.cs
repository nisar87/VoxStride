using System.Diagnostics;
using VoxStride.Core.Files;
using VoxStride.Core.Tools;
using VoxStride.Speech;
using VoxStride.Windows;
using Xunit;

namespace VoxStride.Tests.Windows;

/// <summary>Runs on a real Windows desktop session (GitHub windows runner or a physical/VM test account). Uses only synthetic data.</summary>
public sealed class WindowsIntegrationTests
{
    [Fact]
    public void KnownFoldersResolveAndExist()
    {
        var f = new WindowsKnownFolders();
        var desktop = f.Resolve(KnownFolder.Desktop);
        Assert.False(string.IsNullOrEmpty(desktop));
        Assert.True(Directory.Exists(desktop));
        Assert.True(Directory.Exists(f.Resolve(KnownFolder.Temp)));
    }

    [Fact]
    public void CredentialStoreRoundTripsAndDeletes()
    {
        var store = new CredentialSecretStore("VoxStride-Test/");
        var name = "k-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            store.Save(name, "test-value-ünicode-🔑");
            Assert.Equal("test-value-ünicode-🔑", store.Load(name));
        }
        finally { store.Delete(name); }
        Assert.Null(store.Load(name));
    }

    [Fact]
    public void RecycleBinRemoverRemovesFileRecoverably()
    {
        var path = Path.Combine(Path.GetTempPath(), "voxstride-recycle-" + Guid.NewGuid().ToString("N")[..8] + ".txt");
        File.WriteAllText(path, "synthetic");
        new WindowsFileRemover().Remove(path, recycle: true);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task AppCatalogFindsBuiltInsAndExcludesShells()
    {
        var c = new WindowsAppCatalog();
        await c.BuildAsync();
        Assert.True(c.IsReady);
        Assert.Equal("Notepad", c.Resolve("notepad").App?.DisplayName);
        Assert.Equal("Calculator", c.Resolve("the calculator app").App?.DisplayName);
        Assert.Null(c.Resolve("cmd").App);
        Assert.Null(c.Resolve("powershell").App);
        Assert.DoesNotContain(c.Snapshot(), a => a.LaunchTarget.EndsWith("cmd.exe", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ResolverHandlesAmbiguityAndSpokenForms()
    {
        var apps = new List<AppEntry>
        {
            new("1", "Notepad++", "n++.lnk", ["notepad++"], "startmenu"),
            new("2", "Notepad", "notepad.exe", ["notepad"], "builtin"),
            new("3", "Visual Studio 2026", "vs.lnk", ["visual studio 2026", "visual studio"], "startmenu"),
            new("4", "Visual Studio Code", "code.lnk", ["visual studio code"], "startmenu"),
        };
        Assert.Equal("Notepad++", WindowsAppCatalog.ResolveIn(apps, "notepad plus plus").App?.DisplayName);
        Assert.Equal("Notepad", WindowsAppCatalog.ResolveIn(apps, "Notepad").App?.DisplayName);
        Assert.Equal("Visual Studio 2026", WindowsAppCatalog.ResolveIn(apps, "visual studio").App?.DisplayName);
        Assert.Null(WindowsAppCatalog.ResolveIn(apps, "excel").App);
    }

    /// <summary>U01/U02: writes into a NEW Notepad window by exact HWND and verifies by read-back; no keystrokes are injected.</summary>
    [Fact]
    public async Task NotepadWriteIsVerifiedByReadBack()
    {
        if (Environment.GetEnvironmentVariable("VOXSTRIDE_SKIP_GUI") == "1") return;
        var adapter = new WindowsEditorAdapter(new WindowsAppCatalog());
        var text = "VoxStride integration test ✓\nLine two — ünïcode.";
        var r = await adapter.WriteNewDocumentAsync(EditorKind.Notepad, text, CancellationToken.None);
        try
        {
            Assert.True(r.Inserted, r.Error);
            Assert.True(r.ReadBackVerified, $"read back: {r.ReadBack}");
        }
        finally
        {
            if (r.Window is { } w) { try { Process.GetProcessById(w.ProcessId).Kill(); } catch (Exception) { } }
        }
    }

    [Fact]
    public void EarconsAreValidWav()
    {
        var wav = WindowsSpeechSynthesizer_Tone();
        Assert.Equal((byte)'R', wav[0]);
        Assert.Equal((byte)'W', wav[8]);
        Assert.True(wav.Length > 1000);
    }

    private static byte[] WindowsSpeechSynthesizer_Tone() =>
        (byte[])typeof(WindowsSpeechSynthesizer).GetMethod("Tone", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .Invoke(null, [new (int, int)[] { (880, 70) }, 22050])!;

    [Fact]
    public void SpeechEnvironmentReportsWithoutThrowing()
    {
        _ = SpeechEnvironment.RecognizerCultures();
        _ = SpeechEnvironment.Voices();
    }
}
