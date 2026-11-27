using System.IO;
using System.Diagnostics;
using VoxStride.Core.Tools;

namespace VoxStride.Windows;

/// <summary>
/// Writes text into a NEW, empty editor document by sending WM_SETTEXT to the exact, verified editor control
/// (Notepad: Edit/RichEdit; Notepad++: Scintilla), then reads it back with WM_GETTEXT. No simulated keystrokes,
/// so focus changes cannot leak text into another app. Elevated targets are refused by UIPI and reported.
/// </summary>
public sealed class WindowsEditorAdapter(IAppCatalog catalog) : IEditorAdapter
{
    private static readonly string[] NotepadEditClasses = ["RichEditD2DPT", "Edit", "RICHEDIT50W"];

    public async Task<EditorInsertResult> WriteNewDocumentAsync(EditorKind editor, string text, CancellationToken ct)
    {
        return editor == EditorKind.NotepadPlusPlus
            ? await WriteNotepadPlusPlusAsync(text, ct).ConfigureAwait(false)
            : await WriteNotepadAsync(text, ct).ConfigureAwait(false);
    }

    private async Task<EditorInsertResult> WriteNotepadPlusPlusAsync(string text, CancellationToken ct)
    {
        var exe = ResolveNotepadPlusPlus();
        if (exe is null) return Fail("Notepad++ is not installed.");
        // A separate instance with no session: guarantees a fresh, empty "new 1" document and never touches the user's open tabs.
        using var proc = Process.Start(new ProcessStartInfo(exe, "-multiInst -nosession") { UseShellExecute = false });
        if (proc is null) return Fail("Could not start Notepad++.");
        var main = await WaitForWindow(proc.Id, "Notepad++", ct).ConfigureAwait(false);
        if (main == 0) return Fail("Notepad++ window did not appear.");
        var edit = Native.ChildWindows(main).FirstOrDefault(h => Native.ClassName(h) == "Scintilla" && Native.IsWindowVisible(h));
        if (edit == 0) return Fail("Could not find the Notepad++ editor control.");
        return Insert(main, edit, text);
    }

    private async Task<EditorInsertResult> WriteNotepadAsync(string text, CancellationToken ct)
    {
        var before = Native.TopLevelWindows(h => Native.ClassName(h) == "Notepad").ToHashSet();
        var exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe");
        using (Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false })) { }
        var sw = Stopwatch.StartNew();
        nint main = 0;
        while (sw.Elapsed < TimeSpan.FromSeconds(10) && main == 0)
        {
            ct.ThrowIfCancellationRequested();
            main = Native.TopLevelWindows(h => Native.ClassName(h) == "Notepad" && Native.IsWindowVisible(h)).FirstOrDefault(h => !before.Contains(h));
            if (main == 0) await Task.Delay(100, ct).ConfigureAwait(false);
        }
        if (main == 0) return Fail("A new Notepad window did not appear.");
        nint edit = 0;
        for (var i = 0; i < 30 && edit == 0; i++)
        {
            edit = Native.ChildWindows(main).FirstOrDefault(h => NotepadEditClasses.Contains(Native.ClassName(h)) && Native.IsWindowVisible(h));
            if (edit == 0) await Task.Delay(100, ct).ConfigureAwait(false);
        }
        if (edit == 0) return Fail("Could not find Notepad's text area.");
        return Insert(main, edit, text);
    }

    private static EditorInsertResult Insert(nint main, nint edit, string text)
    {
        var identity = WindowsAppController.Identity(main);
        var existing = Native.ReadText(edit);
        if (existing is null) return new EditorInsertResult(false, false, null, identity, "The editor is not responding or is running as administrator.");
        if (existing.Length > 0) return new EditorInsertResult(false, false, null, identity, "The editor opened a document that already has text; I won't overwrite it. Open a blank document and try again.");
        // Revalidate the exact window just before the effect.
        if (!Native.IsWindow(edit) || identity is null || Native.ProcessIdOf(main) != (uint)identity.ProcessId)
            return new EditorInsertResult(false, false, null, identity, "The editor window changed before writing.");
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal);
        if (!Native.SetText(edit, normalized)) return new EditorInsertResult(false, false, null, identity, "The editor did not accept the text.");
        var readBack = Native.ReadText(edit);
        var ok = readBack is not null && EditorWriteTool.Normalize(readBack) == EditorWriteTool.Normalize(text);
        Native.ShowWindow(main, 9);
        Native.SetForegroundWindow(main);
        return new EditorInsertResult(true, ok, readBack, identity, ok ? null : "Read-back did not match.");
    }

    private static async Task<nint> WaitForWindow(int pid, string className, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(10))
        {
            var h = Native.TopLevelWindows(w => Native.ProcessIdOf(w) == (uint)pid && Native.ClassName(w) == className && Native.IsWindowVisible(w)).FirstOrDefault();
            if (h != 0) return h;
            await Task.Delay(100, ct).ConfigureAwait(false);
        }
        return 0;
    }

    private string? ResolveNotepadPlusPlus()
    {
        var app = catalog.Resolve("notepad++").App;
        if (app is not null)
        {
            var target = app.LaunchTarget.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ? ShellLink.ResolveTarget(app.LaunchTarget) : app.LaunchTarget;
            if (target is not null && File.Exists(target) && Path.GetFileName(target).Equals("notepad++.exe", StringComparison.OrdinalIgnoreCase)) return target;
        }
        foreach (var pf in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
        {
            var p = Path.Combine(pf, "Notepad++", "notepad++.exe");
            if (File.Exists(p)) return p;
        }
        return null;
    }

    private static EditorInsertResult Fail(string m) => new(false, false, null, null, m);
}
