using System.Text.Json;
using Scour.Core.Platform;

namespace Scour.Core.Junk;

public enum ExtensionSource
{
    /// <summary>Installed by the user from the browser's add-on store.</summary>
    Store,

    /// <summary>Pushed in by a program on this PC (registry or external preferences).</summary>
    Sideloaded,

    /// <summary>Forced through a policy — the "Managed by your organization" kind; users can't remove it.</summary>
    Policy,

    /// <summary>Loaded unpacked from a folder (developer mode).</summary>
    Developer,
}

public enum ExtensionRisk
{
    Low,
    Medium,
    High,
}

public sealed record BrowserExtension(
    BrowserProfile Profile, string Id, string Name, string Version, string Description, bool Enabled,
    ExtensionSource Source, IReadOnlyList<string> Abilities, ExtensionRisk Risk);

/// <summary>A registry entry that installs an extension without the user (policy force-list or external extension key).</summary>
public sealed record ForcedInstall(string Browser, string ExtensionId, RegKeyPath Key, string? ValueName, bool IsPolicy)
{
    public string Location => ValueName is null ? Key.ToString() : $"{Key} → {ValueName}";
}

/// <summary>Lists browser extensions per profile, explains what each may do, and finds forced installs.</summary>
public sealed class BrowserExtensions(IFileSystem fs, IRegistry registry, BrowserPrivacy browsers)
{
    // Policy roots (force-installs) and external-extension roots per Chromium browser.
    private static readonly (string Browser, string Policy, string[] External)[] ChromiumRegistry =
    [
        ("Google Chrome", @"SOFTWARE\Policies\Google\Chrome", [@"SOFTWARE\Google\Chrome\Extensions", @"SOFTWARE\WOW6432Node\Google\Chrome\Extensions"]),
        ("Microsoft Edge", @"SOFTWARE\Policies\Microsoft\Edge", [@"SOFTWARE\Microsoft\Edge\Extensions", @"SOFTWARE\WOW6432Node\Microsoft\Edge\Extensions"]),
        ("Brave", @"SOFTWARE\Policies\BraveSoftware\Brave", [@"SOFTWARE\BraveSoftware\Brave-Browser\Extensions", @"SOFTWARE\WOW6432Node\BraveSoftware\Brave-Browser\Extensions"]),
        ("Vivaldi", @"SOFTWARE\Policies\Vivaldi", []),
        ("Opera", @"SOFTWARE\Policies\Opera Software\Opera", []),
    ];

    public List<BrowserExtension> FindAll()
    {
        var forced = ForcedInstalls();
        var list = new List<BrowserExtension>();
        foreach (var profile in browsers.FindProfiles())
        {
            try
            {
                list.AddRange(profile.Engine == BrowserEngine.Firefox ? ReadFirefox(profile) : ReadChromium(profile, forced));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { /* unreadable profile */ }
        }
        return list.OrderByDescending(e => e.Risk).ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    // ---- Chromium

    private IEnumerable<BrowserExtension> ReadChromium(BrowserProfile profile, List<ForcedInstall> forced)
    {
        var settings = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var file in new[] { "Preferences", "Secure Preferences" })
        {
            var path = WinPath.Combine(profile.Path, file);
            if (!fs.FileExists(path)) continue;
            try
            {
                using var doc = JsonDocument.Parse(fs.ReadAllText(path));
                if (doc.RootElement.TryGetProperty("extensions", out var ext) && ext.TryGetProperty("settings", out var s) && s.ValueKind == JsonValueKind.Object)
                    foreach (var p in s.EnumerateObject()) settings[p.Name] = p.Value.Clone();
            }
            catch (JsonException) { /* half-written by a running browser */ }
        }

        var root = WinPath.Combine(profile.Path, "Extensions");
        var ids = fs.DirectoryExists(root) ? fs.EnumerateDirectories(root).Select(WinPath.GetFileName).ToList() : [];
        // Unpacked (developer) extensions live elsewhere and are only known from the settings.
        foreach (var (id, s) in settings)
            if (!ids.Contains(id) && s.TryGetProperty("path", out var p) && p.GetString() is { } dir && WinPath.IsRooted(dir)) ids.Add(id);

        foreach (var id in ids)
        {
            settings.TryGetValue(id, out var s);
            int location = s.ValueKind == JsonValueKind.Object && s.TryGetProperty("location", out var l) && l.TryGetInt32(out var li) ? li : 1;
            if (location is 5 or 10) continue; // built into the browser

            string? folder = null;
            if (s.ValueKind == JsonValueKind.Object && s.TryGetProperty("path", out var sp) && sp.GetString() is { } spath && WinPath.IsRooted(spath))
                folder = spath;
            else if (fs.DirectoryExists(WinPath.Combine(root, id)))
                folder = fs.EnumerateDirectories(WinPath.Combine(root, id)).OrderByDescending(VersionKey).FirstOrDefault();
            if (folder is null || !fs.FileExists(WinPath.Combine(folder, "manifest.json"))) continue;

            // One broken manifest must not hide the rest of the profile's extensions.
            using var manifest = TryReadManifest(WinPath.Combine(folder, "manifest.json"));
            if (manifest is null) continue;
            var m = manifest.RootElement;
            if (m.TryGetProperty("theme", out _)) continue; // themes can't do anything
            var messages = LoadMessages(folder, Str(m, "default_locale"));
            var name = Localize(Str(m, "name"), messages) ?? id;
            var description = Localize(Str(m, "description"), messages) ?? "";

            bool enabled = true;
            if (s.ValueKind == JsonValueKind.Object)
            {
                if (s.TryGetProperty("state", out var st) && st.TryGetInt32(out var state)) enabled = state == 1;
                if (s.TryGetProperty("disable_reasons", out var dr) &&
                    (dr.ValueKind == JsonValueKind.Number && dr.GetInt32() != 0 || dr.ValueKind == JsonValueKind.Array && dr.GetArrayLength() > 0))
                    enabled = false;
            }

            var source = forced.Any(f => f.IsPolicy && f.ExtensionId == id) || location is 7 or 9 ? ExtensionSource.Policy
                       : location == 4 ? ExtensionSource.Developer
                       : forced.Any(f => f.ExtensionId == id) || location is 2 or 3 or 6 ? ExtensionSource.Sideloaded
                       : ExtensionSource.Store;

            var permissions = Strings(m, "permissions").Concat(Strings(m, "host_permissions")).Concat(ContentScriptHosts(m)).ToList();
            var (abilities, risk) = Assess(permissions);
            if (source is ExtensionSource.Policy or ExtensionSource.Sideloaded && risk < ExtensionRisk.Medium) risk = ExtensionRisk.Medium;
            yield return new BrowserExtension(profile, id, name, Str(m, "version") ?? "", description, enabled, source, abilities, risk);
        }
    }

    /// <summary>Chromium accepts comments and trailing commas in manifests, so this does too.</summary>
    private JsonDocument? TryReadManifest(string path)
    {
        try
        {
            return JsonDocument.Parse(fs.ReadAllText(path),
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static Version VersionKey(string dir)
    {
        var leaf = WinPath.GetFileName(dir);
        int us = leaf.IndexOf('_');
        return Version.TryParse(us > 0 ? leaf[..us] : leaf, out var v) ? v : new Version(0, 0);
    }

    private Dictionary<string, string> LoadMessages(string folder, string? locale)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var loc in new[] { locale, "en", "en_US" })
        {
            if (loc is null) continue;
            var path = WinPath.Combine(folder, "_locales", loc, "messages.json");
            if (!fs.FileExists(path)) continue;
            try
            {
                using var doc = JsonDocument.Parse(fs.ReadAllText(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                foreach (var p in doc.RootElement.EnumerateObject())
                    if (p.Value.ValueKind == JsonValueKind.Object && Str(p.Value, "message") is { } msg) result.TryAdd(p.Name, msg);
            }
            catch (JsonException) { /* ignore broken locale */ }
        }
        return result;
    }

    /// <summary>"__MSG_appName__" → the message from the extension's locale files.</summary>
    public static string? Localize(string? text, IReadOnlyDictionary<string, string> messages)
    {
        if (text is null) return null;
        if (text.StartsWith("__MSG_", StringComparison.Ordinal) && text.EndsWith("__", StringComparison.Ordinal))
            return messages.TryGetValue(text[6..^2], out var m) ? m : null;
        return text;
    }

    private static IEnumerable<string> ContentScriptHosts(JsonElement m)
    {
        if (!m.TryGetProperty("content_scripts", out var cs) || cs.ValueKind != JsonValueKind.Array) yield break;
        foreach (var s in cs.EnumerateArray())
            foreach (var h in Strings(s, "matches")) yield return h;
    }

    // ---- Firefox

    private IEnumerable<BrowserExtension> ReadFirefox(BrowserProfile profile)
    {
        var path = WinPath.Combine(profile.Path, "extensions.json");
        if (!fs.FileExists(path)) yield break;
        using var doc = JsonDocument.Parse(fs.ReadAllText(path));
        if (!doc.RootElement.TryGetProperty("addons", out var addons)) yield break;
        foreach (var a in addons.EnumerateArray())
        {
            if (Str(a, "type") != "extension") continue;
            var location = Str(a, "location") ?? "";
            if (location is "app-builtin" or "app-system-defaults" or "app-system-addons" or "app-system-share") continue;
            string name = a.TryGetProperty("defaultLocale", out var dl) && Str(dl, "name") is { } n ? n : Str(a, "id") ?? "";
            string description = a.TryGetProperty("defaultLocale", out var dl2) ? Str(dl2, "description") ?? "" : "";
            var permissions = new List<string>();
            if (a.TryGetProperty("userPermissions", out var up) && up.ValueKind == JsonValueKind.Object)
                permissions.AddRange(Strings(up, "permissions").Concat(Strings(up, "origins")));
            var (abilities, risk) = Assess(permissions);
            var source = location == "app-profile" ? ExtensionSource.Store
                       : location.Contains("temporary", StringComparison.Ordinal) ? ExtensionSource.Developer
                       : ExtensionSource.Sideloaded;
            if (source == ExtensionSource.Sideloaded && risk < ExtensionRisk.Medium) risk = ExtensionRisk.Medium;
            bool active = !a.TryGetProperty("active", out var act) || act.ValueKind != JsonValueKind.False;
            yield return new BrowserExtension(profile, Str(a, "id") ?? "", name, Str(a, "version") ?? "", description, active, source, abilities, risk);
        }
    }

    // ---- permissions in plain words

    public static (List<string> Abilities, ExtensionRisk Risk) Assess(IEnumerable<string> permissions)
    {
        var set = permissions.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var abilities = new List<string>();
        var risk = ExtensionRisk.Low;
        void Add(string text, ExtensionRisk level)
        {
            abilities.Add(text);
            if (level > risk) risk = level;
        }

        bool allSites = set.Any(p => p is "<all_urls>" or "*://*/*" or "http://*/*" or "https://*/*" or "*://*/" or "http://*/" or "https://*/");
        int someSites = set.Count(p => p.Contains("://", StringComparison.Ordinal) && !p.StartsWith("*://*/", StringComparison.Ordinal));
        if (allSites) Add("Read and change everything on every website", ExtensionRisk.High);
        else if (someSites > 0) Add($"Read and change data on {someSites} site{(someSites == 1 ? "" : "s")}", ExtensionRisk.Medium);
        if (set.Contains("nativeMessaging")) Add("Talk to programs installed on this PC", ExtensionRisk.High);
        if (set.Contains("debugger")) Add("Use the debugger on any page", ExtensionRisk.High);
        if (set.Contains("proxy")) Add("Route your traffic through a proxy", ExtensionRisk.High);
        if (set.Contains("management")) Add("Manage your other extensions", ExtensionRisk.High);
        if (set.Contains("webRequest") || set.Contains("webRequestBlocking")) Add("Watch and change network requests", allSites ? ExtensionRisk.High : ExtensionRisk.Medium);
        if (set.Contains("declarativeNetRequest")) Add("Block or redirect content", ExtensionRisk.Low);
        if (set.Contains("history")) Add("See your browsing history", ExtensionRisk.Medium);
        if (set.Contains("cookies")) Add("Read and change cookies", ExtensionRisk.Medium);
        if (set.Contains("tabs") || set.Contains("webNavigation")) Add("See the addresses of your open tabs", ExtensionRisk.Medium);
        if (set.Contains("downloads")) Add("Manage your downloads", ExtensionRisk.Medium);
        if (set.Contains("clipboardRead")) Add("Read your clipboard", ExtensionRisk.Medium);
        if (set.Contains("privacy")) Add("Change privacy settings", ExtensionRisk.Medium);
        if (set.Contains("bookmarks")) Add("Read and change bookmarks", ExtensionRisk.Low);
        if (set.Contains("geolocation")) Add("Know your location", ExtensionRisk.Medium);
        if (set.Contains("notifications")) Add("Show notifications", ExtensionRisk.Low);
        if (abilities.Count == 0) abilities.Add("No special permissions");
        return (abilities, risk);
    }

    // ---- forced installs

    public List<ForcedInstall> ForcedInstalls()
    {
        var list = new List<ForcedInstall>();
        foreach (var (browser, policy, external) in ChromiumRegistry)
        {
            foreach (var hive in new[] { RegHive.LocalMachine, RegHive.CurrentUser })
            {
                var key = new RegKeyPath(hive, policy + @"\ExtensionInstallForcelist");
                foreach (var value in registry.GetValueNames(key))
                    if (registry.GetString(key, value) is { Length: > 0 } data)
                        list.Add(new ForcedInstall(browser, data.Split(';')[0].Trim(), key, value, IsPolicy: true));
            }
            foreach (var ext in external)
            {
                var key = RegKeyPath.LocalMachine(ext);
                foreach (var id in registry.GetSubKeyNames(key))
                    list.Add(new ForcedInstall(browser, id, key.Child(id), null, IsPolicy: false));
            }
        }
        foreach (var hive in new[] { RegHive.LocalMachine, RegHive.CurrentUser })
        {
            var key = new RegKeyPath(hive, @"SOFTWARE\Policies\Mozilla\Firefox\Extensions\Install");
            foreach (var value in registry.GetValueNames(key))
                if (registry.GetString(key, value) is { Length: > 0 } url)
                    list.Add(new ForcedInstall("Mozilla Firefox", url, key, value, IsPolicy: true));
        }
        return list;
    }

    /// <summary>Removes the registry entry that keeps pushing the extension in. The browser drops it on its next start.</summary>
    public void Remove(ForcedInstall item)
    {
        if (item.ValueName is null) registry.DeleteKeyTree(item.Key);
        else registry.DeleteValue(item.Key, item.ValueName);
    }

    // ---- json helpers

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static IEnumerable<string> Strings(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array) yield break;
        foreach (var v in arr.EnumerateArray())
            if (v.ValueKind == JsonValueKind.String && v.GetString() is { } s) yield return s;
    }
}
