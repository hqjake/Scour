using System.Diagnostics;
using System.IO;
using Scour.Core.Repair;

namespace Scour.App.Platform;

public static class RepairRunner
{
    /// <summary>Runs every step of an action, streaming output lines. Returns false if a required step failed.</summary>
    public static async Task<bool> RunAsync(RepairAction action, Action<string> output, CancellationToken ct)
    {
        foreach (var step in action.Steps)
        {
            ct.ThrowIfCancellationRequested();
            bool ok;
            try
            {
                ok = step.Kind switch
                {
                    RepairStepKind.Command => await RunCommandAsync(step, output, ct),
                    RepairStepKind.DeleteFiles => DeleteFiles(step, output),
                    RepairStepKind.RenameFolder => RenameFolder(step, output),
                    _ => StartDetached(step, output),
                };
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e)
            {
                output($"  ! {e.Message}");
                ok = false;
            }
            if (!ok && !step.IgnoreFailure)
            {
                output("Stopped: a required step failed.");
                return false;
            }
        }
        return true;
    }

    private static async Task<bool> RunCommandAsync(RepairStep step, Action<string> output, CancellationToken ct)
    {
        output("> " + (step.Label ?? $"{Path.GetFileName(step.Path)} {step.Args}"));
        var psi = new ProcessStartInfo(step.Path, step.Args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {step.Path}.");
        using var reg = ct.Register(() => { try { p.Kill(true); } catch { /* gone */ } });
        var pump1 = Pump(p.StandardOutput.BaseStream, output);
        var pump2 = Pump(p.StandardError.BaseStream, output);
        await p.WaitForExitAsync(ct);
        await Task.WhenAll(pump1, pump2);
        output(p.ExitCode == 0 ? "  ✓ done" : $"  exit code {p.ExitCode}");
        return p.ExitCode == 0;
    }

    private static async Task Pump(Stream stream, Action<string> output)
    {
        var decoder = new OutputDecoder();
        var buffer = new byte[4096];
        int n;
        while ((n = await stream.ReadAsync(buffer)) > 0)
            foreach (var line in decoder.Feed(buffer.AsSpan(0, n))) output("  " + line);
        foreach (var line in decoder.Flush()) output("  " + line);
    }

    private static bool DeleteFiles(RepairStep step, Action<string> output)
    {
        output($"> Deleting {step.Pattern} in {step.Path}");
        if (!Directory.Exists(step.Path)) return true;
        int deleted = 0, failed = 0;
        foreach (var f in Directory.EnumerateFiles(step.Path, step.Pattern))
        {
            try { File.Delete(f); deleted++; }
            catch { failed++; }
        }
        output($"  {deleted} deleted" + (failed > 0 ? $", {failed} in use" : ""));
        return failed == 0;
    }

    private static bool RenameFolder(RepairStep step, Action<string> output)
    {
        if (!Directory.Exists(step.Path)) return true;
        var target = RepairCatalog.BackupName(step.Path, DateTime.Now);
        output($"> Setting aside {step.Path}");
        Directory.Move(step.Path, target);
        output($"  moved to {Path.GetFileName(target)}");
        return true;
    }

    private static bool StartDetached(RepairStep step, Action<string> output)
    {
        output($"> Starting {Path.GetFileName(step.Path)}");
        Process.Start(new ProcessStartInfo(step.Path, step.Args) { UseShellExecute = true });
        return true;
    }
}
