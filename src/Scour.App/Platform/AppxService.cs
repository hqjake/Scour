using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Scour.App.Platform;

public sealed class AppxPackage
{
    public required string Name { get; init; }
    public required string DisplayName { get; init; }
    public required string FullName { get; init; }
    public required string FamilyName { get; init; }
    public string Publisher { get; init; } = "";
    public string Version { get; init; } = "";
    public string? InstallLocation { get; init; }
    public bool IsRemovable { get; init; }
    public bool IsSystem { get; init; }
}

/// <summary>Microsoft Store / MSIX packages, via the Appx PowerShell module.</summary>
public static class AppxService
{
    public static async Task<List<AppxPackage>> ListAsync(CancellationToken ct = default)
    {
        const string script =
            "Get-AppxPackage -PackageTypeFilter Main | Where-Object { -not $_.IsFramework } | " +
            "Select-Object Name, PackageFullName, PackageFamilyName, Publisher, @{n='Version';e={$_.Version.ToString()}}, " +
            "InstallLocation, NonRemovable, @{n='SignatureKind';e={$_.SignatureKind.ToString()}} | ConvertTo-Json -Depth 2 -Compress";
        var (code, output) = await ProcessRunner.PowerShellAsync(script, TimeSpan.FromMinutes(2), ct);
        if (code != 0) throw new InvalidOperationException("Could not list Windows apps: " + output.Trim());

        var json = output.Trim();
        if (json.Length == 0) return [];
        using var doc = JsonDocument.Parse(json);
        var items = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.EnumerateArray().ToList() : [doc.RootElement];

        var list = new List<AppxPackage>();
        foreach (var e in items)
        {
            string S(string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            bool B(string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.True;
            var name = S("Name");
            var signature = S("SignatureKind");
            list.Add(new AppxPackage
            {
                Name = name,
                DisplayName = FriendlyName(name),
                FullName = S("PackageFullName"),
                FamilyName = S("PackageFamilyName"),
                Publisher = PublisherName(S("Publisher")),
                Version = S("Version"),
                InstallLocation = S("InstallLocation"),
                IsSystem = signature.Equals("System", StringComparison.OrdinalIgnoreCase),
                IsRemovable = !B("NonRemovable") && !signature.Equals("System", StringComparison.OrdinalIgnoreCase),
            });
        }
        return list.OrderBy(p => p.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public static async Task RemoveAsync(AppxPackage package, CancellationToken ct = default)
    {
        var (code, output) = await ProcessRunner.PowerShellAsync(
            $"Remove-AppxPackage -Package '{package.FullName.Replace("'", "''")}' -ErrorAction Stop", TimeSpan.FromMinutes(5), ct);
        if (code != 0) throw new InvalidOperationException(output.Trim());
    }

    /// <summary>The per-user data folder Windows leaves behind for a removed package.</summary>
    public static string DataFolder(AppxPackage package) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages", package.FamilyName);

    /// <summary>"Microsoft.WindowsCalculator" → "Windows Calculator".</summary>
    public static string FriendlyName(string packageName)
    {
        var last = packageName.Split('.').Last(s => s.Length > 0);
        if (last.Length <= 3 && packageName.Contains('.')) last = packageName[(packageName.IndexOf('.') + 1)..].Replace('.', ' ');
        var spaced = Regex.Replace(last, @"(?<=[a-z])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])", " ");
        return spaced.Trim();
    }

    public static string PublisherName(string distinguishedName)
    {
        var m = Regex.Match(distinguishedName, @"CN=(""[^""]+""|[^,]+)");
        return m.Success ? m.Groups[1].Value.Trim('"') : distinguishedName;
    }
}
