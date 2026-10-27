using Scour.Core.Platform;
using Scour.Core.Privacy;
using Scour.Tests.Fakes;

namespace Scour.Tests;

public class DeviceAccessTests
{
    private const string Cam = @"HKCU\Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\webcam";

    private static RegValue Ft(DateTime utc) => RegValue.QWord((ulong)utc.ToFileTimeUtc());

    [Fact]
    public void Lists_apps_newest_first_with_the_one_in_use_on_top()
    {
        var old = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        var recent = new DateTime(2026, 10, 1, 10, 0, 0, DateTimeKind.Utc);
        var reg = new InMemoryRegistry()
            .Set(Cam, "Value", "Allow")
            .Set($@"{Cam}\Microsoft.WindowsCamera_8wekyb3d8bbwe", "Value", "Allow")
            .Set($@"{Cam}\Microsoft.WindowsCamera_8wekyb3d8bbwe", "LastUsedTimeStart", Ft(old))
            .Set($@"{Cam}\Microsoft.WindowsCamera_8wekyb3d8bbwe", "LastUsedTimeStop", Ft(old.AddMinutes(5)))
            .Set($@"{Cam}\Some.NeverUsed_abc", "Value", "Deny")
            .Set($@"{Cam}\NonPackaged", "Value", "Allow")
            .Set($@"{Cam}\NonPackaged\C:#Program Files#Zoom#bin#Zoom.exe", "LastUsedTimeStart", Ft(recent))
            .Set($@"{Cam}\NonPackaged\C:#Program Files#Zoom#bin#Zoom.exe", "LastUsedTimeStop", Ft(recent.AddHours(1)))
            .Set($@"{Cam}\NonPackaged\C:#Apps#Teams.exe", "LastUsedTimeStart", Ft(recent.AddDays(1)))
            .Set($@"{Cam}\NonPackaged\C:#Apps#Teams.exe", "LastUsedTimeStop", RegValue.QWord(0));

        var uses = new DeviceAccess(reg).Uses(Capability.Camera);
        Assert.Equal([@"C:\Apps\Teams.exe", @"C:\Program Files\Zoom\bin\Zoom.exe", "Microsoft.WindowsCamera_8wekyb3d8bbwe"], uses.Select(u => u.Id));
        Assert.True(uses[0].InUse);
        Assert.False(uses[1].InUse);
        Assert.True(uses[1].IsDesktopApp);
        Assert.Equal(old.AddMinutes(5), uses[2].LastUsedUtc);
    }

    [Fact]
    public void Switches_read_and_write_the_same_values_as_settings()
    {
        var reg = new InMemoryRegistry()
            .Set(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone", "Value", "Deny");
        var access = new DeviceAccess(reg);
        Assert.Equal(new CapabilitySwitches(Capability.Microphone, true, true, true), access.Switches(Capability.Microphone));

        access.SetDesktopAppsAllowed(Capability.Camera, false);
        access.SetAppAllowed(Capability.Camera, "Microsoft.WindowsCamera_8wekyb3d8bbwe", false);
        Assert.Equal("Deny", reg.GetString(RegKeyPath.Parse($@"{Cam}\NonPackaged"), "Value"));
        Assert.Equal("Deny", reg.GetString(RegKeyPath.Parse($@"{Cam}\Microsoft.WindowsCamera_8wekyb3d8bbwe"), "Value"));
        Assert.False(access.Switches(Capability.Camera).DesktopAppsAllowed);
    }
}
