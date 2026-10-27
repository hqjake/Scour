using System.IO;
using System.Windows;

namespace Scour.App.Services;

/// <summary>
/// Records errors to %ProgramData%\Scour\error.log, so a problem on someone else's PC can be reported with
/// the details instead of "it didn't work".
/// </summary>
public static class ErrorLog
{
    private static readonly object Gate = new();

    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Scour", "error.log");

    public static void Write(string context, Exception e)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                var info = new FileInfo(FilePath);
                if (info.Exists && info.Length > 1 << 20) File.Move(FilePath, FilePath + ".old", overwrite: true);
                File.AppendAllText(FilePath,
                    $"[{DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)}] Scour {typeof(ErrorLog).Assembly.GetName().Version} on {Environment.OSVersion.VersionString} " +
                    $"({System.Globalization.CultureInfo.CurrentUICulture.Name}) — {context}{Environment.NewLine}{e}{Environment.NewLine}{Environment.NewLine}");
            }
            ScreenshotRunner.Problem($"{context}: {e}");
        }
        catch { /* logging must never throw */ }
    }

    /// <summary>
    /// Awaits a task nobody else awaits (event handlers, "start this in the background"). Failures are
    /// logged and, when <paramref name="report"/> is set, shown instead of vanishing silently.
    /// </summary>
    public static async void Forget(this Task task, string context, bool report = true)
    {
        try { await task; }
        catch (OperationCanceledException) { /* the user cancelled */ }
        catch (Exception e)
        {
            Write(context, e);
            if (report) Application.Current?.Dispatcher.Invoke(() => Dialogs.Error($"{context} failed:\n\n{e.Message}"));
        }
    }
}
