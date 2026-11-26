using System;
using System.IO;

namespace VoxStride.Desktop;

/// <summary>
/// Small rolling diagnostics log at %LocalAppData%\VoxStride\logs\voxstride.log (max ~1 MB, one backup).
/// Records app/engine/microphone state, notices and errors only — never instructions, transcripts, keys or audio.
/// </summary>
public static class DiagnosticsLog
{
    private static readonly object Gate = new();
    public static string Folder => Path.Combine(AppSettings.Folder, "logs");
    public static string FilePath => Path.Combine(Folder, "voxstride.log");

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Folder);
                var fi = new FileInfo(FilePath);
                if (fi.Exists && fi.Length > 1_000_000) File.Move(FilePath, FilePath + ".1", overwrite: true);
                File.AppendAllText(FilePath, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz}  {message.ReplaceLineEndings(" ")}{Environment.NewLine}");
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
