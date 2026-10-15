using Scour.Core.Platform;
using Scour.Core.Programs;

namespace Scour.Core.Shell;

public enum MenuEntryKind
{
    /// <summary>A static verb under ...\shell (has a command line or a delegate handler).</summary>
    Command,

    /// <summary>A COM context-menu handler under ...\shellex\ContextMenuHandlers.</summary>
    Handler,
}

public sealed record ContextMenuEntry(
    MenuEntryKind Kind,
    string Name,
    string AppliesTo,
    RegKeyPath Key,
    string? Clsid,
    string? Target,
    bool IsEnabled,
    bool IsWindows,
    bool ShiftOnly)
{
    public string Id => Key + "|" + Clsid;
}

/// <summary>
/// Explorer right-click menu entries added by programs. Entries are switched off the reversible ways
/// Windows supports: "LegacyDisable" on a verb and the "Shell Extensions\Blocked" list for handlers.
/// </summary>
public sealed class ContextMenu(IRegistry registry, KnownFolders folders, Func<string, string?>? resolveIndirect = null)
{
    private static readonly (string Path, string Label)[] Locations =
    [
        (@"*", "Files"),
        (@"AllFilesystemObjects", "Files and folders"),
        (@"Directory", "Folders"),
        (@"Folder", "Folders"),
        (@"Directory\Background", "Folder background"),
        (@"DesktopBackground", "Desktop"),
        (@"Drive", "Drives"),
    ];

    private static readonly RegKeyPath[] ClassRoots =
    [
        RegKeyPath.LocalMachine(@"SOFTWARE\Classes"),
        RegKeyPath.CurrentUser(@"Software\Classes"),
    ];

    public static readonly RegKeyPath BlockedMachine = RegKeyPath.LocalMachine(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Shell Extensions\Blocked");
    public static readonly RegKeyPath BlockedUser = RegKeyPath.CurrentUser(@"Software\Microsoft\Windows\CurrentVersion\Shell Extensions\Blocked");

    public List<ContextMenuEntry> Scan()
    {
        var blocked = new HashSet<string>(registry.GetValueNames(BlockedMachine).Concat(registry.GetValueNames(BlockedUser)), StringComparer.OrdinalIgnoreCase);
        var list = new List<ContextMenuEntry>();
        foreach (var root in ClassRoots)
        {
            foreach (var (path, label) in Locations)
            {
                var shell = root.Child(path).Child("shell");
                foreach (var verb in registry.GetSubKeyNames(shell))
                    if (ReadVerb(shell.Child(verb), label) is { } e) list.Add(e);

                var handlers = root.Child(path).Child(@"shellex\ContextMenuHandlers");
                foreach (var name in registry.GetSubKeyNames(handlers))
                    if (ReadHandler(handlers.Child(name), label, blocked) is { } e) list.Add(e);
            }
        }
        // "Directory" and "Folder" often register the same handler; blocking works per handler, so show it once.
        return list.DistinctBy(e => e.Kind == MenuEntryKind.Handler ? $"{e.Clsid}|{e.AppliesTo}" : e.Id)
                   .OrderBy(e => e.IsWindows).ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private ContextMenuEntry? ReadVerb(RegKeyPath key, string label)
    {
        // Hidden from the menu anyway.
        if (registry.ValueExists(key, "ProgrammaticAccessOnly")) return null;
        var name = DisplayName(registry.GetString(key, "MUIVerb")) ?? DisplayName(registry.GetString(key, "")) ?? key.Name;
        var command = registry.GetString(key.Child("command"), "");
        var clsid = registry.GetString(key.Child("command"), "DelegateExecute") ?? registry.GetString(key, "ExplorerCommandHandler");
        string? target = command;
        if (string.IsNullOrWhiteSpace(target) && clsid is not null) target = HandlerDll(clsid);
        if (string.IsNullOrWhiteSpace(target) && !registry.ValueExists(key, "SubCommands") && !registry.KeyExists(key.Child("shell"))) return null;

        bool windows = IsWindowsTarget(target) || IsBuiltInVerb(key.Name);
        return new ContextMenuEntry(MenuEntryKind.Command, name, label, key, clsid, target,
            IsEnabled: !registry.ValueExists(key, "LegacyDisable"), windows, ShiftOnly: registry.ValueExists(key, "Extended"));
    }

    private ContextMenuEntry? ReadHandler(RegKeyPath key, string label, HashSet<string> blocked)
    {
        // The key is either named after the CLSID or holds it as its default value.
        var clsid = IsClsid(key.Name) ? key.Name : registry.GetString(key, "");
        if (clsid is null || !IsClsid(clsid.Trim())) return null;
        clsid = clsid.Trim();
        var name = ClsidName(clsid) ?? (IsClsid(key.Name) ? clsid : key.Name);
        var dll = HandlerDll(clsid);
        return new ContextMenuEntry(MenuEntryKind.Handler, name, label, key, clsid, dll,
            IsEnabled: !blocked.Contains(clsid), IsWindows(dll), ShiftOnly: false);
    }

    // A handler's target is a bare file path (often unquoted with spaces), never a command line.
    private bool IsWindows(string? dll) => dll is null || (WinPath.IsRooted(dll) ? WinPath.IsSameOrUnder(dll, folders.WindowsDir) : !dll.Contains('\\'));

    /// <summary>Switches an entry on or off. Handlers are blocked by CLSID, so every place that handler appears changes.</summary>
    public void SetEnabled(ContextMenuEntry entry, bool enabled)
    {
        if (entry.Kind == MenuEntryKind.Command)
        {
            if (enabled) registry.DeleteValue(entry.Key, "LegacyDisable");
            else registry.SetValue(entry.Key, "LegacyDisable", RegValue.String(""));
            return;
        }
        if (entry.Clsid is null) return;
        if (enabled)
        {
            registry.DeleteValue(BlockedMachine, entry.Clsid);
            registry.DeleteValue(BlockedUser, entry.Clsid);
        }
        else
        {
            registry.CreateKey(BlockedMachine);
            registry.SetValue(BlockedMachine, entry.Clsid, RegValue.String(entry.Name));
        }
    }

    // ---- helpers

    private string? DisplayName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var text = raw.StartsWith('@') ? resolveIndirect?.Invoke(raw) : raw;
        if (string.IsNullOrWhiteSpace(text)) return null;
        // "&Open with Code" → "Open with Code" ("&&" is a literal ampersand).
        return text.Replace("&&", "\u0001").Replace("&", "").Replace("\u0001", "&").Trim();
    }

    private string? ClsidName(string clsid)
    {
        foreach (var root in ClassRoots)
            if (DisplayName(registry.GetString(root.Child("CLSID").Child(clsid), "")) is { } n) return n;
        return null;
    }

    private string? HandlerDll(string clsid)
    {
        foreach (var root in ClassRoots)
        {
            var server = root.Child("CLSID").Child(clsid);
            var dll = registry.GetString(server.Child("InprocServer32"), "") ?? registry.GetString(server.Child("LocalServer32"), "");
            if (!string.IsNullOrWhiteSpace(dll)) return folders.Expand(dll.Trim().Trim('"'));
        }
        return null;
    }

    private bool IsWindowsTarget(string? target)
    {
        if (string.IsNullOrWhiteSpace(target)) return false;
        var exe = folders.Expand(CommandLine.Split(target).Executable);
        if (!WinPath.IsRooted(exe)) // "shell32.dll", "explorer.exe /idlist…"
            return !exe.Contains('\\');
        return WinPath.IsSameOrUnder(exe, folders.WindowsDir);
    }

    private static readonly HashSet<string> BuiltInVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "open", "opennewwindow", "opennewprocess", "opennewtab", "explore", "find", "print", "printto", "edit", "runas", "runasuser",
        "cmd", "powershell", "pintohome", "pintostartscreen", "properties", "UpdateEncryptionSettings", "UpdateEncryptionSettingsWork",
        "change-passphrase", "encrypt-bde", "encrypt-bde-elev", "manage-bde", "resume-bde", "resume-bde-elev", "unlock-bde", "format",
    };

    private static bool IsBuiltInVerb(string verb) => BuiltInVerbs.Contains(verb);

    public static bool IsClsid(string text) => text.Length == 38 && text[0] == '{' && text[^1] == '}' && Guid.TryParse(text, out _);
}
