using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using VoxStride.Core.Updates;

namespace VoxStride.Desktop;

/// <summary>
/// Keeps VoxStride up to date from GitHub Releases. Downloads the per-user installer over HTTPS, verifies its
/// SHA-256 against the release's SHA256SUMS.txt (refuses to install without a match), then runs it silently and
/// relaunches. No admin rights, no background service.
/// </summary>
public sealed class UpdateService
{
    private static readonly HttpClient Http = CreateClient();

    public static Version CurrentVersion =>
        UpdateLogic.Normalize(Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0));

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        c.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("VoxStride", CurrentVersion.ToString(3)));
        c.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return c;
    }

    public async Task<UpdateInfo?> CheckAsync(CancellationToken ct)
    {
        using var resp = await Http.GetAsync(UpdateLogic.ReleasesApi, ct).ConfigureAwait(false);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null; // no releases yet
        resp.EnsureSuccessStatusCode();
        var json = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        return json is null ? null : UpdateLogic.Evaluate(json, CurrentVersion);
    }

    /// <summary>Downloads and verifies the installer. Returns its local path.</summary>
    public async Task<string> DownloadAsync(UpdateInfo info, IProgress<double>? progress, CancellationToken ct)
    {
        if (info.ChecksumsUrl is null) throw new InvalidOperationException("This release has no checksum file, so it can't be verified. Download it manually from GitHub.");
        var sums = await Http.GetStringAsync(info.ChecksumsUrl, ct).ConfigureAwait(false);
        var expected = UpdateLogic.ExpectedHash(sums, info.InstallerName) ?? throw new InvalidOperationException("The release checksum list doesn't include the installer.");

        var dir = Path.Combine(Path.GetTempPath(), "VoxStride-Update");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, info.InstallerName);
        using (var resp = await Http.GetAsync(info.InstallerUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
        {
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength ?? info.Size;
            await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var dst = File.Create(path);
            var buf = new byte[81920];
            long done = 0;
            int n;
            while ((n = await src.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                done += n;
                if (total > 0) progress?.Report(100.0 * done / total);
            }
        }
        string actual;
        await using (var f = File.OpenRead(path)) actual = Convert.ToHexString(await SHA256.HashDataAsync(f, ct).ConfigureAwait(false));
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(path);
            throw new InvalidOperationException("The downloaded update didn't match its checksum, so I deleted it and kept your current version.");
        }
        return path;
    }

    /// <summary>Starts the silent per-user installer; it closes this app, updates in place and relaunches VoxStride.</summary>
    public static void LaunchInstaller(string installerPath) =>
        Process.Start(new ProcessStartInfo(installerPath, "/SILENT /SUPPRESSMSGBOXES /NORESTART /CURRENTUSER /CLOSEAPPLICATIONS /relaunch=1") { UseShellExecute = true });
}
