using Scour.Core.Platform;
using Scour.Core.Programs;
using Scour.Core.Shortcuts;

namespace Scour.Core.Autorun;

public enum AutorunSource
{
    RegistryRun,
    RegistryRunOnce,
    StartupFolder,
}

public sealed class AutorunEntry
{
    public required string Name { get; init; }
    public required string Command { get; init; }
    public required AutorunSource Source { get; init; }
    public required string Location { get; init; }
    public bool Enabled { get; set; }

    /// <summary>The Run key holding the value (registry entries).</summary>
    public RegKeyPath? RunKey { get; init; }

    /// <summary>The shortcut/file (startup folder entries).</summary>
    public string? FilePath { get; init; }

    /// <summary>The StartupApproved key that stores the enabled/disabled flag, if this source has one.</summary>
    public RegKeyPath? ApprovalKey { get; init; }

    public string? TargetPath { get; init; }
    public bool TargetExists { get; init; }
}

/// <summary>
/// Lists and toggles programs that start with Windows, using the same StartupApproved flags as
/// Task Manager so changes show up consistently there.
/// </summary>
public sealed class AutorunManager(IRegistry registry, IFileSystem fs, KnownFolders folders)
{
    private const string Approved = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";

    private IEnumerable<(RegKeyPath Key, AutorunSource Source, RegKeyPath? Approval, string Label)> RegistrySources()
    {
        yield return (RegKeyPath.CurrentUser(@"Software\Microsoft\Windows\CurrentVersion\Run"), AutorunSource.RegistryRun,
            RegKeyPath.CurrentUser(Approved + @"\Run"), "Current user");
        yield return (RegKeyPath.LocalMachine(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run"), AutorunSource.RegistryRun,
            RegKeyPath.LocalMachine(Approved.Replace("Software", "SOFTWARE") + @"\Run"), "All users");
        yield return (RegKeyPath.LocalMachine(@"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run"), AutorunSource.RegistryRun,
            RegKeyPath.LocalMachine(Approved.Replace("Software", "SOFTWARE") + @"\Run32"), "All users (32-bit)");
        yield return (RegKeyPath.CurrentUser(@"Software\Microsoft\Windows\CurrentVersion\RunOnce"), AutorunSource.RegistryRunOnce, null, "Current user, once");
        yield return (RegKeyPath.LocalMachine(@"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce"), AutorunSource.RegistryRunOnce, null, "All users, once");
    }

    public List<AutorunEntry> GetEntries()
    {
        var list = new List<AutorunEntry>();
        foreach (var (key, source, approval, label) in RegistrySources())
        {
            foreach (var name in registry.GetValueNames(key))
            {
                if (name.Length == 0) continue;
                var cmd = registry.GetString(key, name) ?? "";
                var target = ResolveTarget(cmd);
                list.Add(new AutorunEntry
                {
                    Name = name,
                    Command = cmd,
                    Source = source,
                    Location = $"Registry: {label}",
                    RunKey = key,
                    ApprovalKey = approval,
                    Enabled = approval is null || IsApproved(approval.Value, name),
                    TargetPath = target,
                    TargetExists = target is not null && fs.FileExists(target),
                });
            }
        }

        foreach (var (dir, approval, label) in new[]
                 {
                     (folders.UserStartup, RegKeyPath.CurrentUser(Approved + @"\StartupFolder"), "Startup folder (current user)"),
                     (folders.CommonStartup, RegKeyPath.LocalMachine(Approved.Replace("Software", "SOFTWARE") + @"\StartupFolder"), "Startup folder (all users)"),
                 })
        {
            if (!fs.DirectoryExists(dir)) continue;
            foreach (var file in fs.EnumerateFiles(dir))
            {
                var fileName = WinPath.GetFileName(file);
                if (fileName.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                string? target = file;
                string command = file;
                if (WinPath.GetExtension(file).Equals(".lnk", StringComparison.OrdinalIgnoreCase))
                {
                    LnkInfo? info = null;
                    try { info = LnkParser.TryParse(fs.ReadAllBytes(file)); } catch { /* unreadable */ }
                    target = info?.EnvironmentTarget is { } env ? folders.Expand(env) : info?.TargetPath;
                    command = target is null ? file : (target + " " + info?.Arguments).Trim();
                }
                list.Add(new AutorunEntry
                {
                    Name = WinPath.GetFileNameWithoutExtension(file),
                    Command = command,
                    Source = AutorunSource.StartupFolder,
                    Location = label,
                    FilePath = file,
                    ApprovalKey = approval,
                    Enabled = IsApproved(approval, fileName),
                    TargetPath = target,
                    TargetExists = target is not null && fs.FileExists(target),
                });
            }
        }
        return list.OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private string? ResolveTarget(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        var exe = folders.Expand(CommandLine.Split(command).Executable);
        return WinPath.IsRooted(exe) ? exe : null;
    }

    /// <summary>StartupApproved data: first byte even = enabled, odd = disabled. Missing = enabled.</summary>
    private bool IsApproved(RegKeyPath approvalKey, string valueName)
    {
        if (registry.GetValue(approvalKey, valueName)?.Data is not byte[] { Length: > 0 } data) return true;
        return (data[0] & 1) == 0;
    }

    public void SetEnabled(AutorunEntry entry, bool enabled, DateTime nowUtc)
    {
        if (entry.ApprovalKey is not { } key) throw new InvalidOperationException("This entry can't be disabled, only deleted.");
        var valueName = entry.Source == AutorunSource.StartupFolder ? WinPath.GetFileName(entry.FilePath!) : entry.Name;
        var data = new byte[12];
        data[0] = enabled ? (byte)0x02 : (byte)0x03;
        if (!enabled) BitConverter.GetBytes(nowUtc.ToFileTimeUtc()).CopyTo(data, 4);
        registry.CreateKey(key);
        registry.SetValue(key, valueName, RegValue.Binary(data));
        entry.Enabled = enabled;
    }

    public void Delete(AutorunEntry entry)
    {
        if (entry.Source == AutorunSource.StartupFolder)
        {
            if (entry.FilePath is not null && fs.FileExists(entry.FilePath)) fs.DeleteFile(entry.FilePath, toRecycleBin: true);
            if (entry.ApprovalKey is { } k && entry.FilePath is not null) TryDeleteValue(k, WinPath.GetFileName(entry.FilePath));
        }
        else
        {
            if (entry.RunKey is { } run) registry.DeleteValue(run, entry.Name);
            if (entry.ApprovalKey is { } k) TryDeleteValue(k, entry.Name);
        }
    }

    private void TryDeleteValue(RegKeyPath key, string name)
    {
        if (registry.GetValue(key, name) is not null) registry.DeleteValue(key, name);
    }
}
