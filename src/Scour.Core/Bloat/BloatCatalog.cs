using System.Text.RegularExpressions;
using Scour.Core.Programs;

namespace Scour.Core.Bloat;

public enum BloatAdvice
{
    /// <summary>Promotional or rarely used; removing it has no side effects. Pre-selected.</summary>
    Safe,
    /// <summary>Some people use it; remove only if you don't.</summary>
    Optional,
}

public enum BloatKind
{
    StoreApp,
    DesktopProgram,
}

public sealed record BloatEntry(string Pattern, string Name, string Category, BloatAdvice Advice, string Description, BloatKind Kind = BloatKind.StoreApp);

public sealed record AppxRef(string Name, string FullName, string FamilyName);

public sealed class BloatMatch
{
    public required BloatEntry Entry { get; init; }
    public List<AppxRef> Packages { get; } = [];
    public InstalledProgram? Program { get; init; }
}

/// <summary>
/// Pre-installed apps people usually don't want. Store apps are matched by package name, desktop
/// trialware by display name. Core Windows components are never matched (see <see cref="Protected"/>).
/// </summary>
public static class BloatCatalog
{
    public static readonly IReadOnlyList<BloatEntry> Entries =
    [
        // Microsoft extras
        new("Microsoft.BingNews", "Microsoft News", "Microsoft extras", BloatAdvice.Safe, "News feed app."),
        new("Microsoft.BingWeather", "Weather", "Microsoft extras", BloatAdvice.Optional, "Weather app (the taskbar weather is separate)."),
        new("Microsoft.BingSearch", "Bing Search", "Microsoft extras", BloatAdvice.Safe, "Bing search integration app."),
        new("Microsoft.GetHelp", "Get Help", "Microsoft extras", BloatAdvice.Safe, "Microsoft support chat app."),
        new("Microsoft.Getstarted", "Tips", "Microsoft extras", BloatAdvice.Safe, "Windows tips and tutorials."),
        new("Microsoft.MicrosoftOfficeHub", "Microsoft 365 (Office) hub", "Microsoft extras", BloatAdvice.Safe, "Office launcher and subscription promotion. Office itself is not affected."),
        new("Microsoft.WindowsFeedbackHub", "Feedback Hub", "Microsoft extras", BloatAdvice.Safe, "Sends feedback to Microsoft."),
        new("Microsoft.People", "People", "Microsoft extras", BloatAdvice.Safe, "Contacts app."),
        new("Microsoft.ZuneVideo", "Movies & TV", "Microsoft extras", BloatAdvice.Safe, "Video store and player."),
        new("Microsoft.ZuneMusic", "Media Player / Groove", "Microsoft extras", BloatAdvice.Optional, "Music player."),
        new("Microsoft.SkypeApp", "Skype", "Microsoft extras", BloatAdvice.Safe, "Skype (discontinued by Microsoft)."),
        new("MicrosoftTeams", "Teams (personal)", "Microsoft extras", BloatAdvice.Safe, "Built-in personal Teams chat. Work Teams is separate."),
        new("Microsoft.549981C3F5F10", "Cortana", "Microsoft extras", BloatAdvice.Safe, "Retired voice assistant."),
        new("Clipchamp.Clipchamp", "Clipchamp", "Microsoft extras", BloatAdvice.Safe, "Online video editor."),
        new("Microsoft.Todos", "Microsoft To Do", "Microsoft extras", BloatAdvice.Optional, "Task list app."),
        new("Microsoft.PowerAutomateDesktop", "Power Automate", "Microsoft extras", BloatAdvice.Optional, "Desktop automation tool."),
        new("Microsoft.WindowsMaps", "Maps", "Microsoft extras", BloatAdvice.Optional, "Offline maps app."),
        new("Microsoft.YourPhone", "Phone Link", "Microsoft extras", BloatAdvice.Optional, "Connects your phone to the PC."),
        new("Microsoft.WindowsAlarms", "Clock", "Microsoft extras", BloatAdvice.Optional, "Alarms, timers and focus sessions."),
        new("Microsoft.WindowsSoundRecorder", "Sound Recorder", "Microsoft extras", BloatAdvice.Optional, "Voice recorder."),
        new("Microsoft.OutlookForWindows", "Outlook (new)", "Microsoft extras", BloatAdvice.Optional, "New Outlook mail app."),
        new("Microsoft.windowscommunicationsapps", "Mail and Calendar", "Microsoft extras", BloatAdvice.Optional, "Old Mail and Calendar apps (retired)."),
        new("Microsoft.Copilot", "Copilot", "Microsoft extras", BloatAdvice.Optional, "Copilot app. See also Privacy tweaks."),
        new("MicrosoftCorporationII.QuickAssist", "Quick Assist", "Microsoft extras", BloatAdvice.Optional, "Remote help tool."),
        new("Microsoft.MixedReality.Portal", "Mixed Reality Portal", "Microsoft extras", BloatAdvice.Safe, "For VR headsets."),
        new("Microsoft.Microsoft3DViewer", "3D Viewer", "Microsoft extras", BloatAdvice.Safe, "3D model viewer."),
        new("Microsoft.Print3D", "Print 3D", "Microsoft extras", BloatAdvice.Safe, "3D printing app."),
        new("Microsoft.MSPaint", "Paint 3D", "Microsoft extras", BloatAdvice.Safe, "Paint 3D (classic Paint is separate)."),
        new("Microsoft.OneConnect", "Mobile Plans", "Microsoft extras", BloatAdvice.Safe, "Cellular data plan app."),
        new("Microsoft.Messaging", "Messaging", "Microsoft extras", BloatAdvice.Safe, "Old SMS app."),
        new("Microsoft.Wallet", "Wallet", "Microsoft extras", BloatAdvice.Safe, "Retired payment app."),

        // Xbox
        new("Microsoft.GamingApp", "Xbox app", "Xbox & games", BloatAdvice.Optional, "Needed for PC Game Pass."),
        new("Microsoft.XboxApp", "Xbox Console Companion", "Xbox & games", BloatAdvice.Safe, "Old Xbox console app."),
        new("Microsoft.XboxGamingOverlay", "Xbox Game Bar", "Xbox & games", BloatAdvice.Optional, "Win+G overlay and game recording."),
        new("Microsoft.XboxGameOverlay", "Xbox Game Overlay", "Xbox & games", BloatAdvice.Optional, "Part of Game Bar."),
        new("Microsoft.XboxSpeechToTextOverlay", "Xbox speech-to-text", "Xbox & games", BloatAdvice.Optional, "Game chat transcription."),
        new("Microsoft.Xbox.TCUI", "Xbox Live in-game UI", "Xbox & games", BloatAdvice.Optional, "Xbox sign-in pop-ups inside games."),
        new("Microsoft.MicrosoftSolitaireCollection", "Solitaire Collection", "Xbox & games", BloatAdvice.Safe, "Card games with ads."),

        // Third-party promotions
        new("*CandyCrush*", "Candy Crush", "Promoted apps", BloatAdvice.Safe, "Pre-installed game."),
        new("*BubbleWitch*", "Bubble Witch", "Promoted apps", BloatAdvice.Safe, "Pre-installed game."),
        new("king.com.*", "King games", "Promoted apps", BloatAdvice.Safe, "Pre-installed games."),
        new("*MarchofEmpires*", "March of Empires", "Promoted apps", BloatAdvice.Safe, "Pre-installed game."),
        new("*HiddenCity*", "Hidden City", "Promoted apps", BloatAdvice.Safe, "Pre-installed game."),
        new("*Asphalt*", "Asphalt", "Promoted apps", BloatAdvice.Safe, "Pre-installed game."),
        new("*RoyalRevolt*", "Royal Revolt", "Promoted apps", BloatAdvice.Safe, "Pre-installed game."),
        new("*Disney*", "Disney+", "Promoted apps", BloatAdvice.Optional, "Streaming app placeholder."),
        new("*Netflix*", "Netflix", "Promoted apps", BloatAdvice.Optional, "Streaming app."),
        new("AmazonVideo.PrimeVideo", "Prime Video", "Promoted apps", BloatAdvice.Optional, "Streaming app."),
        new("SpotifyAB.SpotifyMusic", "Spotify", "Promoted apps", BloatAdvice.Optional, "Music streaming app."),
        new("*Facebook*", "Facebook", "Promoted apps", BloatAdvice.Optional, "Social app."),
        new("*Instagram*", "Instagram", "Promoted apps", BloatAdvice.Optional, "Social app."),
        new("*TikTok*", "TikTok", "Promoted apps", BloatAdvice.Optional, "Social app."),
        new("*Twitter*", "X (Twitter)", "Promoted apps", BloatAdvice.Optional, "Social app."),
        new("7EE7776C.LinkedInforWindows", "LinkedIn", "Promoted apps", BloatAdvice.Optional, "Social app."),
        new("*Duolingo*", "Duolingo", "Promoted apps", BloatAdvice.Optional, "Language learning app."),
        new("*PandoraMedia*", "Pandora", "Promoted apps", BloatAdvice.Safe, "Music app."),
        new("*Dolby*", "Dolby Access", "Promoted apps", BloatAdvice.Optional, "Dolby Atmos setup (keep if you use Atmos)."),

        // PC makers (Store apps)
        new("AD2F1837.HPJumpStarts", "HP JumpStart", "PC maker apps", BloatAdvice.Safe, "HP welcome tour."),
        new("AD2F1837.HPPrivacySettings", "HP Privacy Settings", "PC maker apps", BloatAdvice.Safe, "HP data-sharing prompts."),
        new("AD2F1837.HPSureShieldAI", "HP Sure Shield", "PC maker apps", BloatAdvice.Optional, "HP security add-on."),
        new("*DellInc.DellDigitalDelivery*", "Dell Digital Delivery", "PC maker apps", BloatAdvice.Safe, "Delivers software bought with the PC."),
        new("*DellInc.PartnerPromo*", "Dell partner promotions", "PC maker apps", BloatAdvice.Safe, "Promotional offers."),
        new("E046963F.AIMeetingManager", "Lenovo AI Meeting Manager", "PC maker apps", BloatAdvice.Optional, "Lenovo meeting assistant."),
        new("*McAfee*", "McAfee (Store app)", "PC maker apps", BloatAdvice.Safe, "Trial antivirus. Windows Security protects you without it."),

        // Desktop trialware (uninstalled with the normal uninstaller + leftover scan)
        new("McAfee*", "McAfee trial", "Trialware", BloatAdvice.Safe, "Trial antivirus. Windows Security protects you without it.", BloatKind.DesktopProgram),
        new("Norton*Security*", "Norton trial", "Trialware", BloatAdvice.Optional, "Antivirus trial (keep if you paid for it).", BloatKind.DesktopProgram),
        new("WildTangent*", "WildTangent Games", "Trialware", BloatAdvice.Safe, "Pre-installed game store.", BloatKind.DesktopProgram),
        new("ExpressVPN*", "ExpressVPN (pre-installed)", "Trialware", BloatAdvice.Optional, "VPN trial.", BloatKind.DesktopProgram),
        new("Dropbox*Promotion*", "Dropbox promotion", "Trialware", BloatAdvice.Safe, "Storage offer.", BloatKind.DesktopProgram),
        new("Booking.com*", "Booking.com", "Trialware", BloatAdvice.Safe, "Travel site shortcut.", BloatKind.DesktopProgram),
    ];

    /// <summary>Packages Scour will never remove, whatever a pattern says.</summary>
    public static readonly IReadOnlyList<string> Protected =
    [
        "Microsoft.WindowsStore", "Microsoft.StorePurchaseApp", "Microsoft.DesktopAppInstaller", "Microsoft.WindowsTerminal",
        "Microsoft.WindowsCalculator", "Microsoft.WindowsNotepad", "Microsoft.Paint", "Microsoft.ScreenSketch", "Microsoft.Windows.Photos",
        "Microsoft.SecHealthUI", "Microsoft.WindowsCamera", "Microsoft.VCLibs*", "Microsoft.UI.Xaml*", "Microsoft.NET.Native*",
        "Microsoft.WindowsAppRuntime*", "Microsoft.Services.Store.Engagement", "Microsoft.*Extension", "Microsoft.WindowsDefender*",
        "Microsoft.AAD.BrokerPlugin", "Microsoft.AccountsControl", "Microsoft.Win32WebViewHost", "Microsoft.XboxIdentityProvider",
        "Microsoft.MicrosoftEdge*", "Microsoft.Windows.ShellExperienceHost", "Microsoft.Windows.StartMenuExperienceHost",
        "MicrosoftWindows.Client.*", "Microsoft.Windows.*", "windows.immersivecontrolpanel", "Microsoft.LockApp", "Microsoft.Winget*",
    ];

    public static bool Matches(string pattern, string name) =>
        Regex.IsMatch(name, "^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$", RegexOptions.IgnoreCase);

    public static bool IsProtected(string packageName) => Protected.Any(p => Matches(p, packageName));

    public static List<BloatMatch> Find(IEnumerable<AppxRef> packages, IEnumerable<InstalledProgram> programs)
    {
        var result = new List<BloatMatch>();
        var pkgs = packages.Where(p => !IsProtected(p.Name)).ToList();
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in Entries.Where(e => e.Kind == BloatKind.StoreApp))
        {
            var hits = pkgs.Where(p => !claimed.Contains(p.FullName) && Matches(entry.Pattern, p.Name)).ToList();
            if (hits.Count == 0) continue;
            var m = new BloatMatch { Entry = entry };
            m.Packages.AddRange(hits);
            foreach (var h in hits) claimed.Add(h.FullName);
            result.Add(m);
        }
        var progs = programs.ToList();
        foreach (var entry in Entries.Where(e => e.Kind == BloatKind.DesktopProgram))
            foreach (var p in progs.Where(p => Matches(entry.Pattern, p.DisplayName)))
                result.Add(new BloatMatch { Entry = entry with { Name = p.DisplayName }, Program = p });
        return result;
    }
}
