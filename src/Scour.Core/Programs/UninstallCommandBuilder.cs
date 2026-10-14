using System.Text.RegularExpressions;
using Scour.Core.Platform;

namespace Scour.Core.Programs;

public sealed record UninstallCommand(string Executable, string Arguments, bool IsQuiet)
{
    public override string ToString() => $"{CommandLine.Quote(Executable)} {Arguments}".Trim();
}

/// <summary>Works out how to launch a program's own uninstaller, interactively or silently.</summary>
public static class UninstallCommandBuilder
{
    private static readonly Regex InnoUninstaller = new(@"^unins\d{3}\.exe$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool SupportsQuiet(InstalledProgram p) => Build(p, quiet: true)?.IsQuiet == true;

    public static UninstallCommand? Build(InstalledProgram p, bool quiet, KnownFolders? folders = null)
    {
        string Expand(string s) => folders?.Expand(s) ?? s;
        string msiexec = folders is null ? "msiexec.exe" : WinPath.Combine(folders.SystemDir, "msiexec.exe");

        if (p.ProductCode is not null && (p.IsMsi || IsMsiexec(p.UninstallString)))
        {
            return quiet
                ? new UninstallCommand(msiexec, $"/x {p.ProductCode} /qb /norestart", true)
                : new UninstallCommand(msiexec, $"/x {p.ProductCode}", false);
        }

        if (quiet && !string.IsNullOrWhiteSpace(p.QuietUninstallString))
        {
            var (qexe, qargs) = CommandLine.Split(Expand(p.QuietUninstallString));
            return new UninstallCommand(qexe, qargs, true);
        }

        if (string.IsNullOrWhiteSpace(p.UninstallString)) return null;

        var (exe, args) = CommandLine.Split(Expand(p.UninstallString));
        if (IsMsiexec(exe))
        {
            // "MsiExec.exe /I{GUID}" opens maintenance mode; prefer an explicit remove.
            var m = Regex.Match(args, @"\{[0-9A-Fa-f\-]{36}\}");
            if (m.Success)
            {
                var code = m.Value.ToUpperInvariant();
                return quiet
                    ? new UninstallCommand(msiexec, $"/x {code} /qb /norestart", true)
                    : new UninstallCommand(msiexec, $"/x {code}", false);
            }
            return new UninstallCommand(exe, args, false);
        }

        if (quiet && InnoUninstaller.IsMatch(WinPath.GetFileName(exe)))
            return new UninstallCommand(exe, AppendArgs(args, "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART"), true);

        return new UninstallCommand(exe, args, false);
    }

    private static bool IsMsiexec(string? exeOrCommand) =>
        exeOrCommand is not null &&
        WinPath.GetFileName(CommandLine.Split(exeOrCommand).Executable).StartsWith("msiexec", StringComparison.OrdinalIgnoreCase);

    private static string AppendArgs(string existing, string extra) =>
        string.IsNullOrWhiteSpace(existing) ? extra : existing + " " + extra;
}
