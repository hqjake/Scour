using Scour.Core.Platform;
using Scour.Core.Updates;
using Scour.Tests.Fakes;

namespace Scour.Tests;

public class WindowsUpdateTests
{
    private static readonly DateTime Now = new(2026, 10, 27, 15, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Pause_writes_what_settings_writes_and_resume_removes_it()
    {
        var reg = new InMemoryRegistry();
        var pause = new WindowsUpdatePause(reg);
        Assert.Null(pause.PausedUntilUtc(Now));

        var until = pause.Pause(100, Now); // capped at five weeks
        Assert.Equal(Now.AddDays(35), until);
        var key = RegKeyPath.Parse(@"HKLM\SOFTWARE\Microsoft\WindowsUpdate\UX\Settings");
        Assert.Equal("2026-12-01T15:00:00Z", reg.GetString(key, "PauseUpdatesExpiryTime"));
        Assert.Equal("2026-10-27T15:00:00Z", reg.GetString(key, "PauseQualityUpdatesStartTime"));
        Assert.Equal(until, pause.PausedUntilUtc(Now));
        Assert.Null(pause.PausedUntilUtc(Now.AddDays(40))); // expired

        pause.Resume();
        Assert.Empty(reg.GetValueNames(key));
    }

    [Fact]
    public void Parses_history_with_failures_and_single_items()
    {
        var history = WindowsUpdateJson.History("""
            [{"Title":"2026-10 Cumulative Update","Date":"2026-10-15T03:00:00.0000000Z","Result":2,"HResult":0,"Operation":1},
             {"Title":"Some Driver","Date":"2026-10-16T03:00:00Z","Result":4,"HResult":-2147023293,"Operation":1},
             {"Title":"","Date":"2026-10-16T03:00:00Z","Result":2}]
            """);
        Assert.Equal(["Some Driver", "2026-10 Cumulative Update"], history.Select(h => h.Title));
        Assert.True(history[0].IsFailure);
        Assert.Equal("0x80070643", history[0].ErrorCode);
        Assert.Null(history[1].ErrorCode);

        var one = WindowsUpdateJson.Pending("""{"Id":"abc","Title":"Intel - Display - 31.0","Driver":true,"Size":104857600,"Hidden":false}""");
        Assert.Equal(new PendingUpdate("abc", "Intel - Display - 31.0", true, 104857600, false), one.Single());
        Assert.Empty(WindowsUpdateJson.Pending(""));
    }
}
