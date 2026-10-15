# Writes the three winget manifest files for a Scour release.
# Usage: ./New-WingetManifest.ps1 -Version 1.2.0 -Sha256 <hash> [-OutDir manifests]
# Submit the output with: wingetcreate submit --token <github token> <OutDir>
param(
    [Parameter(Mandatory)] [string] $Version,
    [Parameter(Mandatory)] [string] $Sha256,
    [string] $OutDir = "manifests"
)

$id = 'hqjake.Scour'
$dir = Join-Path $OutDir "h/hqjake/Scour/$Version"
New-Item -ItemType Directory -Force $dir | Out-Null
$url = "https://github.com/hqjake/Scour/releases/download/v$Version/Scour.exe"
$schema = '1.6.0'

@"
PackageIdentifier: $id
PackageVersion: $Version
DefaultLocale: en-US
ManifestType: version
ManifestVersion: $schema
"@ | Set-Content -Encoding utf8 (Join-Path $dir "$id.yaml")

@"
PackageIdentifier: $id
PackageVersion: $Version
InstallerType: portable
Commands:
- scour
Installers:
- Architecture: x64
  InstallerUrl: $url
  InstallerSha256: $($Sha256.ToUpperInvariant())
ManifestType: installer
ManifestVersion: $schema
"@ | Set-Content -Encoding utf8 (Join-Path $dir "$id.installer.yaml")

@"
PackageIdentifier: $id
PackageVersion: $Version
PackageLocale: en-US
Publisher: hqjake
PublisherUrl: https://github.com/hqjake
PackageName: Scour
PackageUrl: https://github.com/hqjake/Scour
License: Proprietary
ShortDescription: All-in-one Windows toolkit - uninstaller, junk and registry cleaner, disk analyzer, USB tools and more.
Description: Scour removes programs with every trace they leave, cleans junk, browsers and the registry, analyzes disk space like WizTree, formats and writes bootable USB sticks, manages drivers, DNS and startup programs, and shows detailed hardware information.
Tags:
- uninstaller
- cleaner
- disk-analyzer
- system-tools
- usb
ReleaseNotesUrl: https://github.com/hqjake/Scour/releases/tag/v$Version
ManifestType: defaultLocale
ManifestVersion: $schema
"@ | Set-Content -Encoding utf8 (Join-Path $dir "$id.locale.en-US.yaml")

Write-Host "Wrote winget manifests to $dir"
