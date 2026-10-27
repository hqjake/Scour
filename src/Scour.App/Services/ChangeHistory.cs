using System.IO;
using Scour.Core.Platform;
using Scour.Core.Removal;

namespace Scour.App.Services;

/// <summary>
/// Records what Scour changes, for the Change history page. Recording never fails an action: a log that
/// can't be written is skipped.
/// </summary>
public static class ChangeHistory
{
    // Undo kinds the history page knows how to reverse.
    public const string RegistryBackup = "registry-backup";
    public const string PrivacyTweak = "privacy";
    public const string GameModeOn = "game-mode";
    public const string UpdatePause = "update-pause";
    public const string DeviceAccess = "device-access";

    private static ActivityLog? _log;

    public static void Initialize(IFileSystem fs, string dataDir) => _log = new ActivityLog(fs, Path.Combine(dataDir, "history.jsonl"));

    public static void Record(string category, string title, string? detail = null, UndoInfo? undo = null)
    {
        try { _log?.Record(category, title, detail, undo); }
        catch (Exception e) { ErrorLog.Write("Recording change history", e); }
    }

    public static List<ActivityEntry> List() => _log?.List() ?? [];

    public static void MarkUndone(string id) => _log?.MarkUndone(id);

    /// <summary>
    /// The change was reversed some other way (Game mode switched off, updates resumed): earlier entries of
    /// this kind can no longer be undone, and undoing them would reverse the current state instead.
    /// </summary>
    public static void Superseded(string undoKind)
    {
        try
        {
            foreach (var e in List().Where(e => e.CanUndo && e.Undo!.Kind == undoKind)) _log?.MarkUndone(e.Id);
        }
        catch (Exception e) { ErrorLog.Write("Recording change history", e); }
    }

    /// <summary>Undo info for a removal that saved a registry backup (null when nothing was backed up).</summary>
    public static UndoInfo? ForBackup(BackupManifest? backup) => backup?.RegistryFile is null ? null : new UndoInfo(RegistryBackup, backup.Id);
}
