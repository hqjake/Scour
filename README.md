# Scour

An all-in-one Windows toolkit: a Revo-style uninstaller with deep leftover removal, junk and registry cleaning,
a WizTree-style disk space analyzer, USB formatting and bootable-USB creation, and a detailed system information
viewer — wrapped in a dark/light "cyber" interface with violet neon accents, animations and UI sounds.

## Screenshots

Captured automatically from the real app on Windows by the [build workflow](.github/workflows/build.yml) after every change.

| | |
| --- | --- |
| ![Dashboard with PC health score](docs/screenshots/dashboard.png) **Dashboard** — health score, live gauges, quick actions | ![Uninstaller](docs/screenshots/uninstaller.png) **Uninstaller** |
| ![Disk space analyzer](docs/screenshots/disk-analyzer.png) **Disk space analyzer** — MFT scan + treemap | ![Junk cleaner](docs/screenshots/junk-cleaner.png) **Junk cleaner** |
| ![System information](docs/screenshots/system-info.png) **System information** | ![Repair center](docs/screenshots/repair-center.png) **Repair center** |
| ![App updates](docs/screenshots/app-updates.png) **App updates** (winget) | ![Registry cleaner](docs/screenshots/registry-cleaner.png) **Registry cleaner** |
| ![Bloatware remover](docs/screenshots/bloatware.png) **Bloatware remover** | ![Privacy tweaks](docs/screenshots/privacy-tweaks.png) **Privacy tweaks** |
| ![USB tools](docs/screenshots/usb-tools.png) **USB tools** | ![Settings](docs/screenshots/settings.png) **Settings** — themes, sounds, automatic cleaning |
| ![Light theme dashboard](docs/screenshots/dashboard-light.png) **Light theme** | ![Light theme disk analyzer](docs/screenshots/disk-analyzer-light.png) **Light theme** |

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
- **Privacy tweaks** — telemetry, advertising ID, Start/Settings suggestions, tips, lock-screen ads, silent promo-app
  installs, Bing in Start, Copilot, Recall, activity history, typing personalisation and more; originals are saved so
  every switch can be turned back exactly
- **Browser privacy** — history, cookies, form entries and sessions per browser/profile, edited inside the
  browsers' SQLite databases so bookmarks, passwords and payment methods are untouched
- **Duplicates & large files** — duplicates confirmed by SHA-256 (size → partial hash → full hash)
- **Registry cleaner** — only entries that point at files that *definitely* no longer exist (never Windows
  folders, disconnected drives or unresolvable commands); everything is backed up first

**Health**
- **PC health score** on the dashboard (disk space, junk, startup load, registry, drive health and wear, antivirus,
  uptime, cleanup cadence, app updates) with one-click fixes
- **Automatic cleaning** on a daily/weekly schedule, **tray icon** with Quick clean, start with Windows, low-disk alerts

**Programs (more)**
- **Bloatware remover** — pre-installed Store apps, games, promotions and PC-maker trialware rated *Safe* or *Optional*;
  core Windows apps are never listed; optionally removes for all accounts and stops reinstalls
- **App updates** — finds outdated apps with winget and updates them all silently, with an ignore list

**System**
- **Repair center** — SFC, DISM, chkdsk scan, network/DNS reset, Windows Update reset, icon cache, Store and Start menu
  repair, stuck printing, clock sync — with live output
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

Built and unit-tested (109 tests) but **not yet run on a real Windows machine** — please report anything that misbehaves.
The process-sampling, network-per-process, file-unlocking and services code exists in `Platform/` but the task manager
screen is not part of this version.
