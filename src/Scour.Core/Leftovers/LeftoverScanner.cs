using Scour.Core.Matching;
using Scour.Core.Platform;
using Scour.Core.Programs;
using Scour.Core.Shortcuts;

namespace Scour.Core.Leftovers;

/// <summary>
/// Finds files, folders, registry keys and values a program left behind. Three depths, as in
/// classic uninstallers: Safe (direct evidence only), Moderate (adds common registry locations and
/// strong name matches) and Advanced (adds COM/MSI component sweeps and loose name matches).
/// </summary>
public sealed class LeftoverScanner(IRegistry registry, IFileSystem fs, KnownFolders folders)
{
    private readonly IRegistry _registry = registry;
    private readonly IFileSystem _fileSystem = fs;
    private readonly KnownFolders _knownFolders = folders;

    public List<LeftoverItem> Scan(ScanTarget target, ScanMode mode, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var run = new Run(this, target, mode, progress, ct);
        return run.Execute();
    }

    private sealed class Run
    {
        private readonly IRegistry _reg;
        private readonly IFileSystem _fs;
        private readonly KnownFolders _folders;
        private readonly ScanTarget _t;
        private readonly ScanMode _mode;
        private readonly IProgress<string>? _progress;
        private readonly CancellationToken _ct;
        private readonly NameMatcher _matcher;
        private readonly SafetyGuard _guard;
        private readonly string? _installDir;
        private readonly Dictionary<string, LeftoverItem> _found = new(StringComparer.Ordinal);
        private readonly HashSet<string> _flaggedProgIds = new(StringComparer.OrdinalIgnoreCase);

        public Run(LeftoverScanner owner, ScanTarget target, ScanMode mode, IProgress<string>? progress, CancellationToken ct)
        {
            _reg = owner._registry;
            _fs = owner._fileSystem;
            _folders = owner._knownFolders;
            _t = target;
            _mode = mode;
            _progress = progress;
            _ct = ct;
            _installDir = string.IsNullOrWhiteSpace(target.InstallLocation) ? null : WinPath.Normalize(target.InstallLocation);
            _matcher = new NameMatcher(target.DisplayName, target.Publisher, _installDir, target.ExtraNames);
            _guard = new SafetyGuard(_folders, target.ProtectedPaths, target.ProtectedKeys);
        }

        public List<LeftoverItem> Execute()
        {
            Step("Checking uninstall entry", ScanUninstallEntry);
            Step("Checking install folder", ScanInstallFolder);
            Step("Scanning program folders", ScanFileRoots);
            Step("Scanning shortcuts", ScanShortcuts);
            Step("Scanning software registry keys", ScanSoftwareKeys);
            Step("Scanning App Paths", ScanAppPaths);
            Step("Scanning startup entries", ScanRunKeys);
            Step("Scanning Windows Installer data", ScanMsi);
            Step("Scanning registered applications", ScanClients);
            if (_mode >= ScanMode.Moderate)
            {
                Step("Scanning services", ScanServices);
                Step("Scanning shell caches", ScanShellCaches);
                Step("Scanning firewall rules", ScanFirewall);
                Step("Scanning COM classes", ScanComClasses);
                Step("Scanning file associations", ScanFileAssociations);
            }
            if (_mode >= ScanMode.Advanced)
            {
                Step("Scanning type libraries", ScanTypeLibs);
                Step("Scanning Windows Installer components", ScanMsiComponents);
            }
            Step("Checking registered applications", ScanRegisteredApplications);
            _progress?.Report("Measuring sizes");
            return Finish();
        }

        private void Step(string label, Action action)
        {
            _ct.ThrowIfCancellationRequested();
            _progress?.Report(label);
            action();
        }

        // ---------------------------------------------------------------- helpers

        private static Confidence FromStrength(MatchStrength s) => s switch
        {
            MatchStrength.Exact => Confidence.Safe,
            MatchStrength.Strong => Confidence.Moderate,
            _ => Confidence.Risky,
        };

        private MatchStrength MinStrength => _mode switch
        {
            ScanMode.Safe => MatchStrength.Exact,
            ScanMode.Moderate => MatchStrength.Strong,
            _ => MatchStrength.Weak,
        };

        private void Add(LeftoverItem item)
        {
            if (_found.TryGetValue(item.Identity, out var existing))
            {
                if (item.Confidence < existing.Confidence) existing.Confidence = item.Confidence;
                foreach (var r in item.Reasons)
                    if (!existing.Reasons.Contains(r)) existing.Reasons.Add(r);
                return;
            }
            _found[item.Identity] = item;
        }

        private void AddKey(RegKeyPath key, Confidence c, string reason, string? service = null)
        {
            if (_guard.IsProtectedKey(key)) return;
            Add(LeftoverItem.ForKey(key, c, reason, service));
        }

        private void AddValue(RegKeyPath key, string name, Confidence c, string reason)
        {
            Add(LeftoverItem.ForValue(key, name, _reg.GetString(key, name), c, reason));
        }

        private void AddFolder(string path, Confidence c, string reason)
        {
            if (_guard.IsProtectedPath(path)) return;
            Add(LeftoverItem.ForFolder(path, c, reason));
        }

        private void AddFile(string path, Confidence c, string reason)
        {
            if (_guard.IsProtectedPath(path)) return;
            Add(LeftoverItem.ForFile(path, c, reason));
        }

        /// <summary>True if <paramref name="text"/> (a command line, path list, etc.) points into the install folder.</summary>
        private bool RefersToInstallDir(string? text)
        {
            if (_installDir is null || string.IsNullOrEmpty(text)) return false;
            var expanded = _folders.Expand(text);
            foreach (var p in CommandLine.ExtractPaths(expanded))
                if (WinPath.IsSameOrUnder(p, _installDir)) return true;
            var dir = _installDir.TrimEnd('\\');
            int i = expanded.IndexOf(dir, StringComparison.OrdinalIgnoreCase);
            while (i >= 0)
            {
                int end = i + dir.Length;
                if (end == expanded.Length || expanded[end] is '\\' or '"' or ',' or ' ' or '|' or ';') return true;
                i = expanded.IndexOf(dir, end, StringComparison.OrdinalIgnoreCase);
            }
            return false;
        }

        /// <summary>True if <paramref name="text"/> launches one of the program's executables that no longer exists.</summary>
        private bool RefersToMissingExecutable(string? text)
        {
            if (string.IsNullOrEmpty(text) || _t.ExecutableNames.Count == 0) return false;
            foreach (var p in CommandLine.ExtractPaths(_folders.Expand(text)))
            {
                if (WinPath.IsSameOrUnder(p, _folders.WindowsDir)) continue;
                if (_t.ExecutableNames.Contains(WinPath.GetFileName(p)) && !_fs.FileExists(p)) return true;
            }
            return false;
        }

        private string? DefaultValue(RegKeyPath key) => _reg.GetString(key, "");

        // ---------------------------------------------------------------- uninstall entry & install folder

        private void ScanUninstallEntry()
        {
            if (_t.UninstallKey is { } key && _reg.KeyExists(key))
                Add(LeftoverItem.ForKey(key, Confidence.Safe, "The program's uninstall entry is still registered"));
        }

        private void ScanInstallFolder()
        {
            if (_installDir is null || !_fs.DirectoryExists(_installDir)) return;
            if (_guard.ContainsProtectedInstall(_installDir))
            {
                if (!IsHardProtected(_installDir))
                    Add(LeftoverItem.ForFolder(_installDir, Confidence.Risky,
                        "Install folder — but another installed program also lives here"));
                return;
            }

            var strength = _matcher.Match(WinPath.GetFileName(_installDir));
            var confidence = !_t.InstallLocationInferred || strength >= MatchStrength.Strong ? Confidence.Safe : Confidence.Moderate;
            AddFolder(_installDir, confidence, _t.InstallLocationInferred ? "Install folder (detected from the program's files)" : "Install folder");
        }

        private bool IsHardProtected(string path)
        {
            var g = new SafetyGuard(_folders, [], []);
            return g.IsProtectedPath(path);
        }

        // ---------------------------------------------------------------- folders

        private IEnumerable<(string Root, Confidence Cap, string Label)> FileRoots()
        {
            yield return (_folders.ProgramFiles, Confidence.Safe, "Program Files");
            yield return (_folders.ProgramFilesX86, Confidence.Safe, "Program Files (x86)");
            yield return (_folders.CommonProgramFiles, Confidence.Safe, "Common Files");
            yield return (_folders.CommonProgramFilesX86, Confidence.Safe, "Common Files (x86)");
            yield return (_folders.ProgramData, Confidence.Safe, "ProgramData");
            yield return (_folders.RoamingAppData, Confidence.Safe, "AppData\\Roaming");
            yield return (_folders.LocalAppData, Confidence.Safe, "AppData\\Local");
            yield return (_folders.LocalLowAppData, Confidence.Safe, "AppData\\LocalLow");
            yield return (_folders.UserPrograms, Confidence.Safe, "AppData\\Local\\Programs");
            yield return (_folders.UserTemp, Confidence.Moderate, "Temp");
            if (_mode >= ScanMode.Advanced)
                yield return (_folders.Documents, Confidence.Risky, "Documents");
        }

        private void ScanFileRoots()
        {
            var seen = new HashSet<string>(WinPath.Comparer);
            foreach (var (root, cap, label) in FileRoots())
            {
                _ct.ThrowIfCancellationRequested();
                if (!seen.Add(WinPath.Normalize(root)) || !_fs.DirectoryExists(root)) continue;
                foreach (var dir in _fs.EnumerateDirectories(root))
                {
                    var name = WinPath.GetFileName(dir);
                    var strength = _matcher.Match(name);
                    if (strength >= MinStrength)
                    {
                        AddFolder(dir, Cap(FromStrength(strength), cap), $"Folder name matches the program ({label})");
                    }
                    else if (_matcher.MatchesPublisher(name))
                    {
                        ScanPublisherFolder(dir, cap, label);
                    }
                }
            }
        }

        private void ScanPublisherFolder(string publisherDir, Confidence cap, string label)
        {
            var children = _fs.EnumerateDirectories(publisherDir).ToList();
            int flagged = 0;
            var worst = Confidence.Safe;
            foreach (var child in children)
            {
                var strength = _matcher.Match(WinPath.GetFileName(child));
                bool refersToUs = _installDir is not null && WinPath.AreSame(child, _installDir);
                if (strength >= MinStrength || refersToUs)
                {
                    var c = refersToUs ? Confidence.Safe : Cap(FromStrength(strength), cap);
                    AddFolder(child, c, $"Folder name matches the program, inside the publisher's folder ({label})");
                    flagged++;
                    if (c > worst) worst = c;
                }
            }

            bool hasFiles = _fs.EnumerateFiles(publisherDir).Any();
            if (_guard.IsProtectedPath(publisherDir)) return;
            if (children.Count > 0 && flagged == children.Count && !hasFiles)
                AddFolder(publisherDir, Cap(worst == Confidence.Safe ? Confidence.Moderate : worst, cap),
                    "Publisher folder that only contains this program's leftovers");
            else if (children.Count == 0 && !hasFiles && _mode >= ScanMode.Moderate)
                AddFolder(publisherDir, Cap(Confidence.Moderate, cap), "Empty publisher folder");
        }

        private static Confidence Cap(Confidence c, Confidence cap) => c < cap ? cap : c;

        // ---------------------------------------------------------------- shortcuts

        private void ScanShortcuts()
        {
            var startMenus = new[] { _folders.UserStartMenuPrograms, _folders.CommonStartMenuPrograms };
            foreach (var root in startMenus)
            {
                if (!_fs.DirectoryExists(root)) continue;
                foreach (var dir in _fs.EnumerateDirectories(root))
                {
                    if (WinPath.GetFileName(dir).Equals("Startup", StringComparison.OrdinalIgnoreCase)) continue;
                    var strength = _matcher.Match(WinPath.GetFileName(dir));
                    var links = EnumerateFilesRecursive(dir, 2).Where(IsShortcut).ToList();
                    bool allOurs = links.Count > 0 && links.All(ShortcutPointsIntoInstallDir);
                    if (allOurs)
                        AddFolder(dir, Confidence.Safe, "Start menu folder whose shortcuts point into the program's folder");
                    else if (strength >= MinStrength)
                        AddFolder(dir, FromStrength(strength), "Start menu folder named after the program");
                    else
                        foreach (var l in links) CheckShortcut(l, "Start menu shortcut");
                }
                foreach (var f in _fs.EnumerateFiles(root).Where(IsShortcut)) CheckShortcut(f, "Start menu shortcut");
            }

            var flatLocations = new[]
            {
                (_folders.UserDesktop, "Desktop shortcut"),
                (_folders.CommonDesktop, "Public desktop shortcut"),
                (_folders.UserStartup, "Startup folder shortcut"),
                (_folders.CommonStartup, "Startup folder shortcut"),
                (WinPath.Combine(_folders.RoamingAppData, @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar"), "Pinned taskbar shortcut"),
                (WinPath.Combine(_folders.RoamingAppData, @"Microsoft\Internet Explorer\Quick Launch"), "Quick Launch shortcut"),
            };
            foreach (var (dir, label) in flatLocations)
            {
                if (!_fs.DirectoryExists(dir)) continue;
                foreach (var f in _fs.EnumerateFiles(dir).Where(IsShortcut)) CheckShortcut(f, label);
            }
        }

        private static bool IsShortcut(string path) =>
            WinPath.GetExtension(path).Equals(".lnk", StringComparison.OrdinalIgnoreCase) ||
            WinPath.GetExtension(path).Equals(".url", StringComparison.OrdinalIgnoreCase);

        private IEnumerable<string> EnumerateFilesRecursive(string dir, int depth)
        {
            foreach (var f in _fs.EnumerateFiles(dir)) yield return f;
            if (depth <= 0) yield break;
            foreach (var d in _fs.EnumerateDirectories(dir))
                foreach (var f in EnumerateFilesRecursive(d, depth - 1)) yield return f;
        }

        private LnkInfo? ReadShortcut(string path)
        {
            if (!WinPath.GetExtension(path).Equals(".lnk", StringComparison.OrdinalIgnoreCase)) return null;
            try { return LnkParser.TryParse(_fs.ReadAllBytes(path)); }
            catch { return null; }
        }

        private bool ShortcutPointsIntoInstallDir(string path)
        {
            var info = ReadShortcut(path);
            return info is not null && info.AllPaths().Any(RefersToInstallDir);
        }

        private void CheckShortcut(string path, string label)
        {
            var info = ReadShortcut(path);
            if (info is not null && info.AllPaths().Any(RefersToInstallDir))
            {
                AddFile(path, Confidence.Safe, $"{label} pointing into the program's folder");
                return;
            }

            var target = info?.EnvironmentTarget ?? info?.TargetPath;
            bool targetGone = target is not null && !_fs.Exists(_folders.Expand(target));
            bool targetElsewhereExists = target is not null && !targetGone;
            if (targetElsewhereExists) return; // a working shortcut to something else — leave it

            var strength = _matcher.Match(WinPath.GetFileNameWithoutExtension(path));
            if (strength >= MinStrength)
                AddFile(path, targetGone ? Confidence.Safe : FromStrength(strength) < Confidence.Moderate ? Confidence.Moderate : FromStrength(strength),
                    targetGone ? $"{label} named after the program, target is missing" : $"{label} named after the program");
        }

        // ---------------------------------------------------------------- registry: software keys

        private static readonly RegKeyPath[] SoftwareRoots =
        [
            RegKeyPath.LocalMachine("SOFTWARE"),
            RegKeyPath.LocalMachine(@"SOFTWARE\WOW6432Node"),
            RegKeyPath.CurrentUser("Software"),
            RegKeyPath.CurrentUser(@"Software\WOW6432Node"),
        ];

        private void ScanSoftwareKeys()
        {
            foreach (var root in SoftwareRoots)
            {
                foreach (var name in _reg.GetSubKeyNames(root))
                {
                    _ct.ThrowIfCancellationRequested();
                    var key = root.Child(name);
                    bool structural = SafetyGuard.ProtectedSoftwareNames.Contains(name);
                    var strength = structural ? MatchStrength.None : _matcher.Match(name);
                    if (strength >= MinStrength)
                        AddKey(key, FromStrength(strength), "Registry key named after the program");
                    else if (_matcher.MatchesPublisher(name))
                        ScanPublisherKey(key, structural);
                }
            }
        }

        private void ScanPublisherKey(RegKeyPath publisherKey, bool structural)
        {
            var children = _reg.GetSubKeyNames(publisherKey);
            int flagged = 0;
            var worst = Confidence.Safe;
            foreach (var name in children)
            {
                var strength = _matcher.Match(name);
                // Inside Microsoft's own key only exact names are trusted, and never preselected.
                if (structural && strength != MatchStrength.Exact) continue;
                if (strength < MinStrength) continue;
                var key = publisherKey.Child(name);
                var c = structural ? Confidence.Moderate : FromStrength(strength);
                if (_guard.IsProtectedKey(key)) continue;
                AddKey(key, c, "Registry key named after the program, under the publisher's key");
                flagged++;
                if (c > worst) worst = c;
            }

            if (structural || _guard.IsProtectedKey(publisherKey)) return;
            bool hasValues = _reg.GetValueNames(publisherKey).Any(v => v.Length > 0);
            if (children.Count > 0 && flagged == children.Count && !hasValues)
                AddKey(publisherKey, worst == Confidence.Safe ? Confidence.Moderate : worst,
                    "Publisher key that only contains this program's leftovers");
            else if (children.Count == 0 && !hasValues && _mode >= ScanMode.Moderate)
                AddKey(publisherKey, Confidence.Moderate, "Empty publisher key");
        }

        // ---------------------------------------------------------------- registry: well-known locations

        private void ScanAppPaths()
        {
            RegKeyPath[] roots =
            [
                RegKeyPath.LocalMachine(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths"),
                RegKeyPath.LocalMachine(@"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\App Paths"),
                RegKeyPath.CurrentUser(@"Software\Microsoft\Windows\CurrentVersion\App Paths"),
            ];
            foreach (var root in roots)
                foreach (var name in _reg.GetSubKeyNames(root))
                {
                    var key = root.Child(name);
                    var target = DefaultValue(key);
                    if (RefersToInstallDir(target) || RefersToInstallDir(_reg.GetString(key, "Path")))
                        AddKey(key, Confidence.Safe, "App Paths registration pointing into the program's folder");
                    else if (_t.ExecutableNames.Contains(name) && RefersToMissingExecutable(target))
                        AddKey(key, Confidence.Moderate, "App Paths registration for the program's missing executable");
                }
        }

        private void ScanRunKeys()
        {
            (RegKeyPath Run, RegKeyPath? Approved)[] runKeys =
            [
                (RegKeyPath.LocalMachine(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run"),
                 RegKeyPath.LocalMachine(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run")),
                (RegKeyPath.LocalMachine(@"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run"),
                 RegKeyPath.LocalMachine(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run32")),
                (RegKeyPath.CurrentUser(@"Software\Microsoft\Windows\CurrentVersion\Run"),
                 RegKeyPath.CurrentUser(@"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run")),
                (RegKeyPath.LocalMachine(@"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce"), null),
                (RegKeyPath.CurrentUser(@"Software\Microsoft\Windows\CurrentVersion\RunOnce"), null),
            ];
            foreach (var (run, approved) in runKeys)
                foreach (var name in _reg.GetValueNames(run))
                {
                    if (name.Length == 0) continue;
                    var data = _reg.GetString(run, name);
                    Confidence? c = null;
                    string reason = "";
                    if (RefersToInstallDir(data)) { c = Confidence.Safe; reason = "Startup entry that launches a file from the program's folder"; }
                    else if (RefersToMissingExecutable(data)) { c = Confidence.Safe; reason = "Startup entry for the program's missing executable"; }
                    else if (_matcher.Match(name) >= MatchStrength.Strong && TargetMissing(data))
                    { c = Confidence.Moderate; reason = "Startup entry named after the program, target is missing"; }
                    if (c is null) continue;
                    AddValue(run, name, c.Value, reason);
                    if (approved is { } a && _reg.ValueExists(a, name))
                        AddValue(a, name, c.Value, "Startup approval state of a removed startup entry");
                }
        }

        private bool TargetMissing(string? commandLine)
        {
            if (string.IsNullOrWhiteSpace(commandLine)) return true;
            var exe = _folders.Expand(CommandLine.Split(commandLine).Executable);
            return WinPath.IsRooted(exe) && !_fs.FileExists(exe);
        }

        private void ScanMsi()
        {
            if (_t.ProductCode is null || !MsiGuid.TryNormalize(_t.ProductCode, out var code)) return;
            var packed = MsiGuid.Pack(code);
            RegKeyPath[] keys =
            [
                RegKeyPath.LocalMachine($@"SOFTWARE\Classes\Installer\Products\{packed}"),
                RegKeyPath.LocalMachine($@"SOFTWARE\Classes\Installer\Features\{packed}"),
                RegKeyPath.LocalMachine($@"SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\UserData\S-1-5-18\Products\{packed}"),
                RegKeyPath.CurrentUser($@"Software\Microsoft\Installer\Products\{packed}"),
                RegKeyPath.CurrentUser($@"Software\Microsoft\Installer\Features\{packed}"),
                RegKeyPath.LocalMachine($@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{code}"),
                RegKeyPath.LocalMachine($@"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\{code}"),
            ];
            foreach (var k in keys)
                if (_reg.KeyExists(k)) Add(LeftoverItem.ForKey(k, Confidence.Safe, "Windows Installer registration of the product"));

            if (_mode < ScanMode.Moderate) return;
            var upgrade = RegKeyPath.LocalMachine(@"SOFTWARE\Classes\Installer\UpgradeCodes");
            foreach (var name in _reg.GetSubKeyNames(upgrade))
            {
                var key = upgrade.Child(name);
                var values = _reg.GetValueNames(key);
                if (!values.Contains(packed, StringComparer.OrdinalIgnoreCase)) continue;
                if (values.Count(v => v.Length > 0) == 1) AddKey(key, Confidence.Safe, "Windows Installer upgrade code that only lists this product");
                else AddValue(key, packed, Confidence.Safe, "Windows Installer upgrade code reference");
            }
        }

        private void ScanMsiComponents()
        {
            if (_t.ProductCode is null || !MsiGuid.TryNormalize(_t.ProductCode, out var code)) return;
            var packed = MsiGuid.Pack(code);
            var root = RegKeyPath.LocalMachine(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\UserData\S-1-5-18\Components");
            foreach (var name in _reg.GetSubKeyNames(root))
            {
                _ct.ThrowIfCancellationRequested();
                var key = root.Child(name);
                var values = _reg.GetValueNames(key);
                if (!values.Contains(packed, StringComparer.OrdinalIgnoreCase)) continue;
                if (values.Count(v => v.Length > 0) == 1) AddKey(key, Confidence.Moderate, "Windows Installer component used only by this product");
                else AddValue(key, packed, Confidence.Moderate, "Windows Installer component reference");
            }
        }

        private void ScanServices()
        {
            var root = RegKeyPath.LocalMachine(@"SYSTEM\CurrentControlSet\Services");
            foreach (var name in _reg.GetSubKeyNames(root))
            {
                var key = root.Child(name);
                var image = _reg.GetString(key, "ImagePath");
                if (string.IsNullOrEmpty(image)) continue;
                var expanded = _folders.Expand(image);
                if (expanded.Contains(@"\system32\", StringComparison.OrdinalIgnoreCase) && !RefersToInstallDir(expanded)) continue;
                if (RefersToInstallDir(image))
                    AddKey(key, Confidence.Moderate, "Windows service whose program is in the program's folder", name);
                else if (RefersToMissingExecutable(image) || _matcher.Match(name) == MatchStrength.Exact && TargetMissing(image))
                    AddKey(key, Confidence.Moderate, "Windows service for the program's missing executable", name);
            }
        }

        private void ScanShellCaches()
        {
            if (_installDir is null) return;
            RegKeyPath[] valueNameKeys =
            [
                RegKeyPath.CurrentUser(@"Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\MuiCache"),
                RegKeyPath.CurrentUser(@"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Compatibility Assistant\Store"),
                RegKeyPath.CurrentUser(@"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers"),
                RegKeyPath.LocalMachine(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers"),
            ];
            foreach (var key in valueNameKeys)
                foreach (var name in _reg.GetValueNames(key))
                    if (RefersToInstallDir(name))
                        AddValue(key, name, Confidence.Safe, "Windows shell cache entry for the program's files");
        }

        private void ScanFirewall()
        {
            var key = RegKeyPath.LocalMachine(@"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\FirewallRules");
            foreach (var name in _reg.GetValueNames(key))
            {
                var data = _reg.GetString(key, name);
                if (data is null) continue;
                int app = data.IndexOf("|App=", StringComparison.OrdinalIgnoreCase);
                if (app < 0) continue;
                int end = data.IndexOf('|', app + 5);
                var path = end < 0 ? data[(app + 5)..] : data[(app + 5)..end];
                if (RefersToInstallDir(path))
                    AddValue(key, name, Confidence.Safe, "Firewall rule for a program in the program's folder");
            }
        }

        private void ScanComClasses()
        {
            if (_installDir is null) return;
            RegKeyPath[] roots =
            [
                RegKeyPath.LocalMachine(@"SOFTWARE\Classes\CLSID"),
                RegKeyPath.LocalMachine(@"SOFTWARE\Classes\WOW6432Node\CLSID"),
                RegKeyPath.CurrentUser(@"Software\Classes\CLSID"),
            ];
            foreach (var root in roots)
                foreach (var clsid in _reg.GetSubKeyNames(root))
                {
                    _ct.ThrowIfCancellationRequested();
                    var key = root.Child(clsid);
                    if (RefersToInstallDir(DefaultValue(key.Child("InprocServer32"))) ||
                        RefersToInstallDir(DefaultValue(key.Child("LocalServer32"))))
                    {
                        AddKey(key, Confidence.Safe, "COM class implemented by a file in the program's folder");
                        var progId = DefaultValue(key.Child("ProgID"));
                        if (!string.IsNullOrEmpty(progId)) _flaggedProgIds.Add(progId);
                    }
                }
        }

        private static readonly HashSet<string> ClassesSkip = new(StringComparer.OrdinalIgnoreCase)
        {
            "CLSID", "TypeLib", "Interface", "AppID", "WOW6432Node", "Installer", "Local Settings", "Applications",
            "Directory", "Drive", "Folder", "*", "AllFilesystemObjects", "SystemFileAssociations", "MIME", "Component Categories",
            "exefile", "batfile", "cmdfile", "comfile", "lnkfile", "dllfile", "Unknown", "http", "https", "ftp", "mailto",
        };

        private void ScanFileAssociations()
        {
            RegKeyPath[] roots = [RegKeyPath.LocalMachine(@"SOFTWARE\Classes"), RegKeyPath.CurrentUser(@"Software\Classes")];
            var extensions = new List<RegKeyPath>();
            foreach (var root in roots)
            {
                foreach (var name in _reg.GetSubKeyNames(root))
                {
                    _ct.ThrowIfCancellationRequested();
                    if (name.StartsWith('.')) { extensions.Add(root.Child(name)); continue; }
                    if (ClassesSkip.Contains(name)) continue;
                    var key = root.Child(name);
                    if (_installDir is not null &&
                        (RefersToInstallDir(DefaultValue(key.Child(@"shell\open\command"))) ||
                         RefersToInstallDir(DefaultValue(key.Child("DefaultIcon")))))
                    {
                        AddKey(key, Confidence.Safe, "File type / protocol handled by the program");
                        _flaggedProgIds.Add(name);
                    }
                    else if (_flaggedProgIds.Contains(name))
                    {
                        AddKey(key, Confidence.Safe, "ProgID of a COM class implemented by the program");
                    }
                    else if (_mode >= ScanMode.Advanced && _matcher.Match(name) >= MatchStrength.Strong &&
                             TargetMissing(DefaultValue(key.Child(@"shell\open\command"))))
                    {
                        AddKey(key, Confidence.Moderate, "File type named after the program");
                        _flaggedProgIds.Add(name);
                    }
                }

                var apps = root.Child("Applications");
                foreach (var exe in _reg.GetSubKeyNames(apps))
                {
                    var key = apps.Child(exe);
                    if (RefersToInstallDir(DefaultValue(key.Child(@"shell\open\command"))))
                        AddKey(key, Confidence.Safe, "\"Open with\" registration for the program");
                    else if (_t.ExecutableNames.Contains(exe) && TargetMissing(DefaultValue(key.Child(@"shell\open\command"))))
                        AddKey(key, Confidence.Moderate, "\"Open with\" registration for the program's missing executable");
                }
            }

            if (_flaggedProgIds.Count == 0) return;
            foreach (var ext in extensions)
            {
                var owp = ext.Child("OpenWithProgids");
                foreach (var v in _reg.GetValueNames(owp))
                    if (_flaggedProgIds.Contains(v))
                        AddValue(owp, v, Confidence.Safe, "\"Open with\" entry for a removed file type");
            }
        }

        private void ScanTypeLibs()
        {
            if (_installDir is null) return;
            var root = RegKeyPath.LocalMachine(@"SOFTWARE\Classes\TypeLib");
            foreach (var guid in _reg.GetSubKeyNames(root))
            {
                _ct.ThrowIfCancellationRequested();
                var lib = root.Child(guid);
                bool ours = false;
                foreach (var ver in _reg.GetSubKeyNames(lib))
                {
                    foreach (var lcid in _reg.GetSubKeyNames(lib.Child(ver)))
                    {
                        var l = lib.Child(ver).Child(lcid);
                        if (RefersToInstallDir(DefaultValue(l.Child("win32"))) || RefersToInstallDir(DefaultValue(l.Child("win64"))))
                            ours = true;
                    }
                }
                if (ours) AddKey(lib, Confidence.Safe, "Type library registered from the program's folder");
            }
        }

        private void ScanClients()
        {
            RegKeyPath[] roots =
            [
                RegKeyPath.LocalMachine(@"SOFTWARE\Clients\StartMenuInternet"),
                RegKeyPath.LocalMachine(@"SOFTWARE\WOW6432Node\Clients\StartMenuInternet"),
                RegKeyPath.CurrentUser(@"Software\Clients\StartMenuInternet"),
                RegKeyPath.LocalMachine(@"SOFTWARE\Clients\Mail"),
                RegKeyPath.LocalMachine(@"SOFTWARE\Clients\Media"),
            ];
            foreach (var root in roots)
                foreach (var name in _reg.GetSubKeyNames(root))
                {
                    var key = root.Child(name);
                    if (RefersToInstallDir(DefaultValue(key.Child(@"shell\open\command"))) ||
                        RefersToInstallDir(DefaultValue(key.Child("DefaultIcon"))))
                        AddKey(key, Confidence.Safe, "Default-program registration pointing into the program's folder");
                    else if (_matcher.Match(name) >= MinStrength && TargetMissing(DefaultValue(key.Child(@"shell\open\command"))))
                        AddKey(key, Confidence.Moderate, "Default-program registration named after the program");
                }
        }

        private void ScanRegisteredApplications()
        {
            RegKeyPath[] keys = [RegKeyPath.LocalMachine(@"SOFTWARE\RegisteredApplications"), RegKeyPath.CurrentUser(@"Software\RegisteredApplications")];
            var flaggedKeys = _found.Values.Where(i => i.Kind == LeftoverKind.RegistryKey && i.RegistryKey is not null)
                                           .Select(i => i.RegistryKey!.Value).ToList();
            foreach (var key in keys)
                foreach (var name in _reg.GetValueNames(key))
                {
                    var data = _reg.GetString(key, name);
                    if (string.IsNullOrEmpty(data)) continue;
                    var target = new RegKeyPath(key.Hive, data.TrimStart('\\'));
                    if (flaggedKeys.Any(f => target.IsSameOrUnder(f)))
                        AddValue(key, name, Confidence.Safe, "Registered-application entry for a removed registry key");
                    else if (!_reg.KeyExists(target) && !_reg.KeyExists(new RegKeyPath(RegHive.LocalMachine, data.TrimStart('\\'))) &&
                             _matcher.Match(name) >= MinStrength)
                        AddValue(key, name, Confidence.Moderate, "Registered-application entry named after the program, target is missing");
                }
        }

        // ---------------------------------------------------------------- finish

        private List<LeftoverItem> Finish()
        {
            var maxConfidence = _mode switch
            {
                ScanMode.Safe => Confidence.Safe,
                ScanMode.Moderate => Confidence.Moderate,
                _ => Confidence.Risky,
            };
            var items = _found.Values.Where(i => i.Confidence <= maxConfidence).ToList();

            // Drop items already covered by a flagged ancestor that is at least as trustworthy.
            var folders = items.Where(i => i.Kind == LeftoverKind.Folder).ToList();
            var keys = items.Where(i => i.Kind == LeftoverKind.RegistryKey).ToList();
            items.RemoveAll(i =>
                i.Kind is LeftoverKind.Folder or LeftoverKind.File &&
                folders.Any(f => !ReferenceEquals(f, i) && f.Confidence <= i.Confidence && WinPath.IsStrictlyUnder(i.Path, f.Path)) ||
                i.Kind is LeftoverKind.RegistryKey or LeftoverKind.RegistryValue && i.RegistryKey is { } rk &&
                keys.Any(k => !ReferenceEquals(k, i) && k.Confidence <= i.Confidence &&
                              (i.Kind == LeftoverKind.RegistryValue ? rk.IsSameOrUnder(k.RegistryKey!.Value) : rk.IsSameOrUnder(k.RegistryKey!.Value) && !rk.Equals(k.RegistryKey!.Value))));

            foreach (var i in items)
            {
                _ct.ThrowIfCancellationRequested();
                try
                {
                    if (i.Kind == LeftoverKind.Folder) i.SizeBytes = _fs.GetDirectorySize(i.Path, ct: _ct);
                    else if (i.Kind == LeftoverKind.File) i.SizeBytes = _fs.GetFileSize(i.Path);
                }
                catch (OperationCanceledException) { throw; }
                catch { /* size is informational */ }
            }

            return items.OrderBy(i => i.IsRegistry ? 0 : 1)
                        .ThenBy(i => i.Path, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(i => i.ValueName, StringComparer.OrdinalIgnoreCase)
                        .ToList();
        }
    }
}
