using System.Globalization;
using Scour.Core.Platform;

namespace Scour.Core.Repair;

public enum RepairStepKind
{
    /// <summary>Run a program and wait for it.</summary>
    Command,
    /// <summary>Delete files matching <see cref="RepairStep.Pattern"/> in <see cref="RepairStep.Path"/>.</summary>
    DeleteFiles,
    /// <summary>Rename a folder to "name.old-timestamp" so Windows rebuilds it.</summary>
    RenameFolder,
    /// <summary>Start a program without waiting (e.g. restarting Explorer).</summary>
    StartDetached,
}

public sealed record RepairStep(RepairStepKind Kind, string Path, string Args = "", string Pattern = "*", bool IgnoreFailure = false, string? Label = null)
{
    public static RepairStep Run(string exe, string args, bool ignoreFailure = false, string? label = null) =>
        new(RepairStepKind.Command, exe, args, IgnoreFailure: ignoreFailure, Label: label);
}

public sealed class RepairAction
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required string Glyph { get; init; }
    public required string Duration { get; init; }
    public required IReadOnlyList<RepairStep> Steps { get; init; }
    public bool NeedsRestart { get; init; }
    public string? Warning { get; init; }
}

/// <summary>The fixes offered by the repair center — all use Windows' own tools.</summary>
public static class RepairCatalog
{
    public static IReadOnlyList<RepairAction> Create(KnownFolders f, string systemDrive)
    {
        string sys = f.SystemDir;
        string Exe(string name) => WinPath.Combine(sys, name);
        string ps(string script) => $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"{script}\"";
        var stamp = "{timestamp}"; // replaced at run time
        var drive = systemDrive.TrimEnd('\\');

        return
        [
            new RepairAction
            {
                Id = "sfc", Name = "Repair system files", Glyph = "\uE90F", Duration = "10–20 min",
                Description = "System File Checker scans every protected Windows file and replaces damaged ones.",
                Steps = [RepairStep.Run(Exe("sfc.exe"), "/scannow", label: "sfc /scannow")],
            },
            new RepairAction
            {
                Id = "dism", Name = "Repair Windows image", Glyph = "\uE895", Duration = "10–30 min",
                Description = "DISM checks the Windows component store and downloads fresh copies of anything corrupt. Run this before \"Repair system files\" if that one can't fix everything.",
                Steps = [RepairStep.Run(Exe("dism.exe"), "/Online /Cleanup-Image /RestoreHealth", label: "DISM /RestoreHealth")],
            },
            new RepairAction
            {
                Id = "chkdsk", Name = "Check system drive for errors", Glyph = "\uEDA2", Duration = "1–5 min",
                Description = "Online scan of the file system on the system drive. Problems that need the drive offline are queued for the next restart.",
                Steps = [RepairStep.Run(Exe("chkdsk.exe"), $"{drive} /scan", ignoreFailure: true, label: $"chkdsk {drive} /scan")],
            },
            new RepairAction
            {
                Id = "network", Name = "Reset network", Glyph = "\uE968", Duration = "under 1 min", NeedsRestart = true,
                Description = "Resets Winsock and the TCP/IP stack and clears the DNS cache. Fixes most \"connected but no internet\" problems.",
                Warning = "Custom network settings such as static IP addresses may need to be entered again.",
                Steps =
                [
                    RepairStep.Run(Exe("netsh.exe"), "winsock reset"),
                    RepairStep.Run(Exe("netsh.exe"), "int ip reset", ignoreFailure: true),
                    RepairStep.Run(Exe("ipconfig.exe"), "/flushdns"),
                ],
            },
            new RepairAction
            {
                Id = "dns", Name = "Flush DNS cache", Glyph = "\uE774", Duration = "seconds",
                Description = "Forgets cached website addresses. Helps when a site moved or won't load on this PC only.",
                Steps = [RepairStep.Run(Exe("ipconfig.exe"), "/flushdns")],
            },
            new RepairAction
            {
                Id = "wu", Name = "Reset Windows Update", Glyph = "\uE777", Duration = "1–2 min",
                Description = "Stops the update services, sets aside the download cache and catalog so Windows rebuilds them, then restarts the services. Fixes stuck or failing updates.",
                Steps =
                [
                    RepairStep.Run(Exe("net.exe"), "stop wuauserv", ignoreFailure: true),
                    RepairStep.Run(Exe("net.exe"), "stop bits", ignoreFailure: true),
                    RepairStep.Run(Exe("net.exe"), "stop cryptsvc", ignoreFailure: true),
                    RepairStep.Run(Exe("net.exe"), "stop msiserver", ignoreFailure: true),
                    new RepairStep(RepairStepKind.RenameFolder, WinPath.Combine(f.WindowsDir, "SoftwareDistribution"), stamp, IgnoreFailure: true),
                    new RepairStep(RepairStepKind.RenameFolder, WinPath.Combine(sys, "catroot2"), stamp, IgnoreFailure: true),
                    RepairStep.Run(Exe("net.exe"), "start cryptsvc", ignoreFailure: true),
                    RepairStep.Run(Exe("net.exe"), "start bits", ignoreFailure: true),
                    RepairStep.Run(Exe("net.exe"), "start wuauserv", ignoreFailure: true),
                    RepairStep.Run(Exe("net.exe"), "start msiserver", ignoreFailure: true),
                ],
            },
            new RepairAction
            {
                Id = "icons", Name = "Rebuild icon & thumbnail cache", Glyph = "\uE8B9", Duration = "seconds",
                Description = "Fixes blank, wrong or outdated icons and thumbnails. Explorer restarts briefly (the taskbar disappears for a moment).",
                Steps =
                [
                    RepairStep.Run(Exe("taskkill.exe"), "/f /im explorer.exe", ignoreFailure: true),
                    new RepairStep(RepairStepKind.DeleteFiles, f.LocalAppData, Pattern: "IconCache.db", IgnoreFailure: true),
                    new RepairStep(RepairStepKind.DeleteFiles, WinPath.Combine(f.LocalAppData, @"Microsoft\Windows\Explorer"), Pattern: "iconcache_*.db", IgnoreFailure: true),
                    new RepairStep(RepairStepKind.DeleteFiles, WinPath.Combine(f.LocalAppData, @"Microsoft\Windows\Explorer"), Pattern: "thumbcache_*.db", IgnoreFailure: true),
                    new RepairStep(RepairStepKind.StartDetached, WinPath.Combine(f.WindowsDir, "explorer.exe")),
                ],
            },
            new RepairAction
            {
                Id = "store", Name = "Repair Microsoft Store", Glyph = "\uE719", Duration = "1 min",
                Description = "Re-registers the Store app and clears its cache. Fixes a Store that won't open or download.",
                Steps =
                [
                    RepairStep.Run(Exe(@"WindowsPowerShell\v1.0\powershell.exe"),
                        ps("Get-AppxPackage -AllUsers Microsoft.WindowsStore | ForEach-Object { Add-AppxPackage -DisableDevelopmentMode -Register \\\"$($_.InstallLocation)\\AppXManifest.xml\\\" }"),
                        ignoreFailure: true, label: "Re-register Microsoft Store"),
                    new RepairStep(RepairStepKind.StartDetached, Exe("wsreset.exe")),
                ],
            },
            new RepairAction
            {
                Id = "start", Name = "Repair Start menu & taskbar", Glyph = "\uE8FC", Duration = "1 min",
                Description = "Re-registers the Start menu, search and shell components. Fixes a Start menu or search box that won't open.",
                Steps =
                [
                    RepairStep.Run(Exe(@"WindowsPowerShell\v1.0\powershell.exe"),
                        ps("Get-AppxPackage -AllUsers *StartMenuExperienceHost*, *ShellExperienceHost*, *Windows.Search* | ForEach-Object { Add-AppxPackage -DisableDevelopmentMode -Register \\\"$($_.InstallLocation)\\AppXManifest.xml\\\" }"),
                        ignoreFailure: true, label: "Re-register shell components"),
                    RepairStep.Run(Exe("taskkill.exe"), "/f /im StartMenuExperienceHost.exe", ignoreFailure: true),
                ],
            },
            new RepairAction
            {
                Id = "spooler", Name = "Fix stuck printing", Glyph = "\uE749", Duration = "seconds",
                Description = "Clears jammed print jobs and restarts the print spooler.",
                Steps =
                [
                    RepairStep.Run(Exe("net.exe"), "stop spooler", ignoreFailure: true),
                    new RepairStep(RepairStepKind.DeleteFiles, WinPath.Combine(sys, @"spool\PRINTERS"), IgnoreFailure: true),
                    RepairStep.Run(Exe("net.exe"), "start spooler"),
                ],
            },
            new RepairAction
            {
                Id = "time", Name = "Sync the clock", Glyph = "\uE823", Duration = "seconds",
                Description = "Resynchronises the clock with the internet time server. A wrong clock breaks HTTPS sites and sign-ins.",
                Steps =
                [
                    RepairStep.Run(Exe("net.exe"), "start w32time", ignoreFailure: true),
                    RepairStep.Run(Exe("w32tm.exe"), "/config /update", ignoreFailure: true),
                    // A service that has only just started often has no time source yet; the second try then works.
                    RepairStep.Run(Exe("w32tm.exe"), "/resync /force", ignoreFailure: true),
                    RepairStep.Run(Exe("w32tm.exe"), "/resync /force", label: "w32tm.exe /resync /force (again)"),
                ],
            },
        ];
    }

    /// <summary>"SoftwareDistribution" → "SoftwareDistribution.old-20261014-2130".</summary>
    public static string BackupName(string folder, DateTime now) => $"{folder.TrimEnd('\\')}.old-" + now.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture);
}
