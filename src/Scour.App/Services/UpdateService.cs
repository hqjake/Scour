using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using Scour.Core.Updates;

namespace Scour.App.Services;

/// <summary>Checks GitHub Releases for a newer Scour and swaps the running exe for it.</summary>
public static class UpdateService
{
    private const string LatestUrl = "https://api.github.com/repos/hqjake/Scour/releases/latest";

    public static Version CurrentVersion
    {
        get
        {
            var v = typeof(UpdateService).Assembly.GetName().Version ?? new Version(1, 0, 0);
            return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
        }
    }

    private static HttpClient CreateClient(TimeSpan timeout)
    {
        var http = new HttpClient { Timeout = timeout };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"Scour/{CurrentVersion}");
        return http;
    }

    /// <summary>The latest release if it's newer than this build, otherwise null.</summary>
    public static async Task<ReleaseInfo?> CheckAsync(CancellationToken ct = default)
    {
        using var http = CreateClient(TimeSpan.FromSeconds(20));
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestUrl);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await http.SendAsync(request, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null; // no releases yet
        response.EnsureSuccessStatusCode();
        var release = ReleaseCheck.Parse(await response.Content.ReadAsStringAsync(ct));
        return release is not null && ReleaseCheck.IsNewer(release, CurrentVersion) ? release : null;
    }

    /// <summary>The running Scour.exe, or null when running from a development build (dotnet host, loose dlls).</summary>
    public static string? InstalledExe()
    {
        var path = Environment.ProcessPath;
        if (path is null || !Path.GetFileName(path).Equals("Scour.exe", StringComparison.OrdinalIgnoreCase)) return null;
        // A framework-dependent build sits next to Scour.dll; replacing just the exe would break it.
        return File.Exists(Path.Combine(Path.GetDirectoryName(path)!, "Scour.dll")) ? null : path;
    }

    public static bool CanSelfUpdate(ReleaseInfo release) => release.DownloadUrl is not null && InstalledExe() is not null;

    /// <summary>
    /// Downloads the new exe, moves it into place and starts it. The running exe is renamed (Windows allows that
    /// while it runs) and deleted by the new version on its first start. Returns once the new copy is running.
    /// </summary>
    public static async Task InstallAsync(ReleaseInfo release, IProgress<double> progress, CancellationToken ct)
    {
        var exe = InstalledExe() ?? throw new InvalidOperationException("This copy of Scour can't update itself.");
        var url = release.DownloadUrl ?? throw new InvalidOperationException("The release has no Scour.exe attached.");
        var dir = Path.GetDirectoryName(exe)!;
        var download = Path.Combine(dir, "Scour.new.exe");
        var old = Path.Combine(dir, "Scour.old.exe");

        using (var http = CreateClient(TimeSpan.FromMinutes(10)))
        using (var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            response.EnsureSuccessStatusCode();
            long? total = response.Content.Headers.ContentLength;
            await using var source = await response.Content.ReadAsStreamAsync(ct);
            await using var target = File.Create(download);
            var buffer = new byte[81920];
            long done = 0;
            int n;
            while ((n = await source.ReadAsync(buffer, ct)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, n), ct);
                done += n;
                if (total > 0) progress.Report(100.0 * done / total.Value);
            }
        }

        try
        {
            Verify(download, release.Sha256);
        }
        catch
        {
            TryDelete(download);
            throw;
        }

        TryDelete(old);
        File.Move(exe, old);
        try
        {
            File.Move(download, exe);
        }
        catch
        {
            File.Move(old, exe); // put the working copy back
            throw;
        }
        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = dir });
    }

    private static void Verify(string file, string? sha256)
    {
        using var f = File.OpenRead(file);
        if (f.Length < 1_000_000 || f.ReadByte() != 'M' || f.ReadByte() != 'Z')
            throw new InvalidDataException("The download isn't a valid Scour.exe.");
        if (sha256 is null) return;
        f.Position = 0;
        var actual = Convert.ToHexStringLower(SHA256.HashData(f));
        if (!actual.Equals(sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The download is damaged (checksum mismatch). Try again later.");
    }

    /// <summary>Removes the previous version left behind by an update.</summary>
    public static async Task CleanUpAsync()
    {
        var exe = InstalledExe();
        if (exe is null) return;
        var dir = Path.GetDirectoryName(exe)!;
        TryDelete(Path.Combine(dir, "Scour.new.exe"));
        var old = Path.Combine(dir, "Scour.old.exe");
        // The old process may still be shutting down for a moment.
        for (int i = 0; i < 10 && File.Exists(old); i++)
        {
            TryDelete(old);
            if (File.Exists(old)) await Task.Delay(1000);
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* in use */ }
    }
}
