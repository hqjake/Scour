using Scour.Core.Platform;

namespace Scour.Tests.Fakes;

public static class TestFolders
{
    public static KnownFolders Create() => new()
    {
        UserProfile = @"C:\Users\Alex",
        RoamingAppData = @"C:\Users\Alex\AppData\Roaming",
        LocalAppData = @"C:\Users\Alex\AppData\Local",
        LocalLowAppData = @"C:\Users\Alex\AppData\LocalLow",
        UserPrograms = @"C:\Users\Alex\AppData\Local\Programs",
        UserTemp = @"C:\Users\Alex\AppData\Local\Temp",
        Documents = @"C:\Users\Alex\Documents",
        UserDesktop = @"C:\Users\Alex\Desktop",
        UserStartMenuPrograms = @"C:\Users\Alex\AppData\Roaming\Microsoft\Windows\Start Menu\Programs",
        UserStartup = @"C:\Users\Alex\AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Startup",
    };
}
