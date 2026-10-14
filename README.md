# Scour

An all-in-one Windows toolkit: a Revo-style uninstaller with deep leftover removal, junk and registry cleaning,
a WizTree-style disk space analyzer, USB formatting and bootable-USB creation, and a detailed system information
viewer — wrapped in a dark/light "cyber" interface with violet neon accents, animations and UI sounds.

## Features

**Programs**
- **Uninstaller** — runs each program's own uninstaller (waiting for the whole process tree, including
  Inno Setup/NSIS relaunches), then scans for leftovers in three depths:
  - *Safe*: direct evidence only (install folder, its shortcuts, startup entries, exactly-named registry keys)
  - *Moderate*: adds services, file associations, COM registrations, firewall rules, close name matches
  - *Advanced*: Windows Installer components, type libraries and loose matches
- Quick uninstall (silent where supported) and batch uninstall, optional restore point first
- **Install & trace** — snapshot before/after an installation for a perfect uninstall later
- **Forced uninstall** for half-removed programs, **Windows apps** (Store/MSIX) removal
- **Hunter mode** — drag the target onto any window to find, uninstall or end the program behind it
- **Backups** — every registry deletion is saved as a `.reg` file and can be restored; files go to the Recycle Bin

**Cleaning**
- **Junk cleaner** — temp files, error reports, crash dumps, logs, Prefetch, Delivery Optimization, browser caches
  (Chrome, Edge, Firefox, Brave, Opera, Vivaldi), app caches (Discord, Spotify, Steam, Teams, Slack), developer caches
  (VS Code, npm/Yarn, pip, NuGet, Gradle), Recycle Bin, plus deep cleanup (component store via DISM, Windows.old,
  old restore points)
- **Browser privacy** — history, cookies, form entries and sessions per browser/profile, edited inside the
  browsers' SQLite databases so bookmarks, passwords and payment methods are untouched
- **Duplicates & large files** — duplicates confirmed by SHA-256 (size → partial hash → full hash)
- **Registry cleaner** — only entries that point at files that *definitely* no longer exist (never Windows
  folders, disconnected drives or unresolvable commands); everything is backed up first

**System**
- **System information** — CPU, every RAM module (type, speed, part number, slot), GPU (true VRAM), motherboard,
  BIOS/UEFI, Secure Boot, TPM, drives with health/temperature/wear, displays, network, audio, battery, OS; export a report
- **Startup programs** — enable/disable with the same switches Task Manager uses

**Disks**
- **Disk space analyzer** — reads the NTFS Master File Table directly (like WizTree) for whole-drive scans in seconds;
  treemap, folder tree, file-type breakdown, 1000 largest files, delete from the view
- **USB tools** — format FAT32/exFAT/NTFS (FAT32 on drives over 32 GB using Scour's own formatter), or create
  bootable sticks: Windows setup (splits `install.wim` over 4 GB, adds BIOS boot code), ISO file copy, or raw image (DD)

## Download

Every push builds `Scour.exe` (single file, no .NET install needed) — see the **Actions** tab → latest run →
*Scour* artifact. Tagged versions (`v1.0.0`, …) are attached to GitHub Releases.

Scour asks for administrator rights at start; uninstalling and cleaning system locations need them.

## Building

Requires the .NET 10 SDK.

```powershell
dotnet test tests/Scour.Tests
dotnet run --project src/Scour.App
```

The engine (`src/Scour.Core`) is platform-neutral and fully unit-tested, including against a real NTFS volume
image (`tests/Scour.Tests/Fixtures`) and FAT32 volumes verified with `fsck.fat`/mtools when those tools are installed.
The WPF app (`src/Scour.App`) can be compiled on Linux/macOS too (`EnableWindowsTargeting`), but only runs on Windows.

## Project layout

| Path | What's there |
| --- | --- |
| `src/Scour.Core` | Engine: inventory, leftover scanner, safety guard, removal + `.reg` backups, tracing, junk/privacy/duplicates, registry cleaner, MFT scanner + treemap, FAT32 formatter, boot planner, system-info decoders |
| `src/Scour.App` | WPF app: Windows implementations (registry, file system, processes, WMI, volumes), view models, views, theme/sound services |
| `tests/Scour.Tests` | xUnit tests with in-memory registry/file system fakes and real disk-image fixtures |

## Safety

- A safety guard refuses to touch Windows, Program Files/AppData roots, other installed programs' folders, and
  structural registry keys, whatever a scan suggests.
- Only items marked *Safe* are pre-selected; Moderate/Risky items must be ticked deliberately.
- USB tools only list USB/SD disks that are not the boot or system disk, and re-check the disk right before erasing it.

## Status

Built and unit-tested (87 tests) but **not yet run on a real Windows machine** — please report anything that misbehaves.
The process-sampling, network-per-process, file-unlocking and services code exists in `Platform/` but the task manager
screen is not part of this version.
