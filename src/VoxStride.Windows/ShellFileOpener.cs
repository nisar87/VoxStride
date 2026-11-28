using System.Diagnostics;
using System.IO;
using VoxStride.Contracts;
using VoxStride.Core.Tools;

namespace VoxStride.Windows;

/// <summary>
/// Opens a document with its default app (Excel for .xlsx, Notepad for .txt…). Only document types, only inside the
/// user's allowed folders: never executables, scripts or shortcuts.
/// </summary>
public sealed class ShellFileOpener(FileScope scope) : IFileOpener
{
    public static readonly HashSet<string> DocumentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ".xlsx", ".xls", ".csv", ".docx", ".doc", ".pptx", ".pdf", ".txt", ".md", ".html", ".htm", ".png", ".jpg", ".jpeg", ".gif", ".mp3", ".mp4",
    };

    public Task<bool> OpenAsync(string path, CancellationToken ct)
    {
        if (!scope.TryResolve(path, out var full, out var err)) { Log.Warn("files", "refused to open outside allowed folders: " + err); return Task.FromResult(false); }
        if (!DocumentTypes.Contains(Path.GetExtension(full)) || !File.Exists(full)) { Log.Warn("files", $"refused to open {Path.GetExtension(full)} file"); return Task.FromResult(false); }
        try
        {
            using var p = Process.Start(new ProcessStartInfo(full) { UseShellExecute = true });
            Log.Info("files", $"opened {Path.GetFileName(full)} with its default app");
            return Task.FromResult(true);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Warn("files", $"no app could open {Path.GetFileName(full)}", ex);
            return Task.FromResult(false);
        }
    }
}
