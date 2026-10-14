using Scour.App.Platform;
using Scour.Core.Schedule;

namespace Scour.App.Services;

/// <summary>Keeps Windows Task Scheduler in sync with Scour's background settings.</summary>
public static class ScheduleService
{
    public static async Task<string?> ApplyAsync(AppSettings s)
    {
        var exe = Environment.ProcessPath;
        if (exe is null) return "Can't find Scour's own path.";
        var errors = new List<string>();

        async Task Run(string args, bool ignoreFailure = false)
        {
            var (code, output) = await ProcessRunner.RunHiddenAsync("schtasks.exe", args, TimeSpan.FromSeconds(30));
            if (code != 0 && !ignoreFailure) errors.Add(output.Trim());
        }

        if (s.AutoClean == CleaningSchedule.Off) await Run(SchtasksCommands.Delete(SchtasksCommands.CleanTask), ignoreFailure: true);
        else await Run(SchtasksCommands.CreateCleaning(exe, s.AutoClean, s.AutoCleanDay, s.AutoCleanHour));

        if (s.StartInTray) await Run(SchtasksCommands.CreateTrayAtLogon(exe));
        else await Run(SchtasksCommands.Delete(SchtasksCommands.TrayTask), ignoreFailure: true);

        return errors.Count == 0 ? null : string.Join("\n", errors);
    }
}
