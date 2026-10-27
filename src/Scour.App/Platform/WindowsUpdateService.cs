using Scour.Core.Updates;

namespace Scour.App.Platform;

/// <summary>Talks to the Windows Update Agent (its COM API, through PowerShell).</summary>
public static class WindowsUpdateService
{
    public static async Task<List<UpdateHistoryEntry>> HistoryAsync(CancellationToken ct = default)
    {
        const string script = """
            $ErrorActionPreference = 'Stop'
            $searcher = (New-Object -ComObject Microsoft.Update.Session).CreateUpdateSearcher()
            $count = $searcher.GetTotalHistoryCount()
            if ($count -gt 0) {
              $searcher.QueryHistory(0, [Math]::Min($count, 200)) | Where-Object { $_.Title } | ForEach-Object {
                [pscustomobject]@{ Title = $_.Title; Date = $_.Date.ToUniversalTime().ToString('o'); Result = [int]$_.ResultCode; HResult = [int]$_.HResult; Operation = [int]$_.Operation }
              } | ConvertTo-Json -Compress
            }
            """;
        var r = await ProcessRunner.PowerShellAsync(script, TimeSpan.FromMinutes(2), ct);
        if (r.ExitCode != 0) throw new InvalidOperationException(Describe(r.Output));
        return WindowsUpdateJson.History(r.StdOut);
    }

    /// <summary>Updates offered but not installed, including hidden ones. Asks Microsoft's servers, so it can take a few minutes.</summary>
    public static async Task<List<PendingUpdate>> PendingAsync(CancellationToken ct = default)
    {
        const string script = """
            $ErrorActionPreference = 'Stop'
            $searcher = (New-Object -ComObject Microsoft.Update.Session).CreateUpdateSearcher()
            $result = $searcher.Search("IsInstalled=0")
            $result.Updates | ForEach-Object {
              [pscustomobject]@{ Id = $_.Identity.UpdateID; Title = $_.Title; Driver = ($_.Type -eq 2); Size = [long]$_.MaxDownloadSize; Hidden = [bool]$_.IsHidden }
            } | ConvertTo-Json -Compress
            """;
        var r = await ProcessRunner.PowerShellAsync(script, TimeSpan.FromMinutes(6), ct);
        if (r.ExitCode != 0) throw new InvalidOperationException(Describe(r.Output));
        return WindowsUpdateJson.Pending(r.StdOut);
    }

    /// <summary>Hides an update so Windows stops offering it (the same as Microsoft's "Show or hide updates" tool), or shows it again.</summary>
    public static async Task SetHiddenAsync(string updateId, bool hidden)
    {
        if (!Guid.TryParse(updateId, out var id)) throw new ArgumentException("Not an update ID.", nameof(updateId));
        var value = hidden ? "$true" : "$false";
        var script = $$"""
            $ErrorActionPreference = 'Stop'
            $searcher = (New-Object -ComObject Microsoft.Update.Session).CreateUpdateSearcher()
            $result = $searcher.Search("UpdateID='{{id}}'")
            if ($result.Updates.Count -eq 0) { throw 'Windows Update no longer offers this update.' }
            $result.Updates.Item(0).IsHidden = {{value}}
            """;
        var r = await ProcessRunner.PowerShellAsync(script, TimeSpan.FromMinutes(4));
        if (r.ExitCode != 0) throw new InvalidOperationException(Describe(r.Output));
    }

    private static string Describe(string output)
    {
        if (output.Contains("0x80070422", StringComparison.OrdinalIgnoreCase))
            return "the Windows Update service is turned off on this PC.";
        if (output.Contains("0x8024402C", StringComparison.OrdinalIgnoreCase) || output.Contains("0x80072EE7", StringComparison.OrdinalIgnoreCase))
            return "Windows Update can't reach Microsoft's servers (check the internet connection).";
        var line = output.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0 && !l.StartsWith("At ", StringComparison.Ordinal) && !l.StartsWith('+'));
        return line ?? "Windows Update didn't answer.";
    }
}
