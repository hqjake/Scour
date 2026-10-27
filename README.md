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
| ![Command palette](docs/screenshots/command-palette.png) **Command palette** (Ctrl+K) | ![Reinstall kit](docs/screenshots/reinstall-kit.png) **Reinstall kit** |
| ![Network](docs/screenshots/network.png) **Network** — DNS switcher, speed test, Wi-Fi, hosts | ![Drivers](docs/screenshots/drivers.png) **Drivers** — old versions, backup & restore |
| ![Game mode](docs/screenshots/game-mode.png) **Game mode** — one switch for gaming performance | ![Startup programs](docs/screenshots/startup.png) **Startup programs** |
| ![Task manager](docs/screenshots/task-manager.png) **Task manager** — processes, services, unlock files | ![Sensors](docs/screenshots/sensors.png) **Sensors** — GPU temperature, fans, load |
| ![Boot time](docs/screenshots/boot-time.png) **Boot time** — history and what slows it | ![Windows Update](docs/screenshots/windows-update.png) **Windows Update** — pause, history, hide |
| ![Camera and microphone](docs/screenshots/camera-microphone.png) **Camera & microphone** history | ![Change history](docs/screenshots/change-history.png) **Change history** with undo |
| ![Context menu](docs/screenshots/context-menu.png) **Context menu** manager | ![Browser extensions](docs/screenshots/browser-extensions.png) **Browser extensions** |
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
- **Browser extensions** — every extension in every Chrome, Edge, Firefox, Brave, Opera and Vivaldi profile, with what
  it may do in plain words and a risk level; finds extensions forced in through policies or the registry (the
  "Managed by your organization" kind adware uses) and removes those entries with a backup
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
- **Reinstall kit** — save the apps on this PC to a small file (winget's own export format, so `winget import` reads it
  too), then on a new or reset PC open it, see what's missing and install everything silently with per-app status

**Gaming**
- **Automatic Game mode** — finds your Steam, Epic, GOG, Ubisoft, EA, Riot and Xbox games (or folders you add), switches
  Game mode on when one starts and off when it closes, with its own boosts per game if you like
- **In-game overlay** — FPS (from the game's own frame presents, like PresentMon), CPU, GPU load and temperature and
  memory in a corner of the screen; **Ctrl+Alt+O** shows or hides it
- **Game mode** — one switch (or **Ctrl+Alt+G** from inside a game, or the tray menu) that switches to the Ultimate/High
  performance power plan (Best performance mode on laptops), makes sure Windows Game Mode is on, stops Xbox Game Bar's
  background recording, silences notifications, lowers the priority of launchers, sync clients and updaters, and
  optionally turns off mouse acceleration. Scour's own cleaning and alerts pause while it's on. Every original setting is
  saved first and put back exactly when it's turned off, even after a restart

**Everywhere**
- **Change history** — everything Scour changed, with one-click undo for registry removals, privacy tweaks, Game mode,
  update pauses and camera/microphone blocks
- **Problem report** — Settings → Create problem report zips the error log and a system report onto the Desktop
- **Languages** — English, Deutsch, Español, Français, Italiano and Português (follows Windows by default)
- **Command palette** — press **Ctrl+K** (or the search box in the sidebar) and type a few letters to open any page,
  run an action (quick clean, health check, disk scan, repairs, theme…) or uninstall any program
- **Automatic updates** — Scour checks GitHub Releases on start, shows a banner when a new version is out and updates
  itself in place (downloads, verifies the SHA-256 checksum, swaps the exe and restarts); can be turned off in Settings

**System**
- **Task manager** — live CPU, memory, disk and network use per process (end, priority, VirusTotal check), services
  (start, stop, startup type) and **Unlock files**: which program holds a file that won't delete, and release it
- **Sensors** — GPU temperature, fan speed, power and load for NVIDIA, AMD and Intel straight from the display driver,
  ACPI thermal zones and drive temperatures — no kernel driver installed
- **Boot time** — every start of Windows charted from Windows' own measurements, plus the apps, drivers and services
  slowing it down
- **Windows Update** — pause updates (as Settings does), see the history with failures and search an error, hide an
  update you don't want such as a bad driver
- **Camera & microphone** — which apps used the camera, microphone and location and when, and block them
- **Battery health** on laptops (capacity left, charge cycles) in System information and the health check
- **Repair center** — SFC, DISM, chkdsk scan, network/DNS reset, Windows Update reset, icon cache, Store and Start menu
  repair, stuck printing, clock sync — with live output
- **System information** — CPU, every RAM module (type, speed, part number, slot), GPU (true VRAM), motherboard,
  BIOS/UEFI, Secure Boot, TPM, drives with health/temperature/wear, displays, network, audio, battery, OS; export a report
- **Startup programs** — enable/disable with the same switches Task Manager uses
- **Context menu** — switch off right-click menu entries and shell extensions programs added, reversibly
- **Network** — DNS switcher (Cloudflare, Google, Quad9, AdGuard, …) with live latency for each, internet speed test,
  saved Wi-Fi passwords and a hosts file editor
- **Drivers** — finds old driver versions left in the driver store after updates (Windows refuses to remove any in
  use), plus one-click backup and restore of all drivers for a Windows reset

**Disks**
- **Disk space analyzer** — reads the NTFS Master File Table directly (like WizTree) for whole-drive scans in seconds;
  treemap, folder tree, file-type breakdown, 1000 largest files, delete from the view; every scan is remembered and the
  **What changed** tab shows which folders grew since the last one
- **USB tools** — format FAT32/exFAT/NTFS (FAT32 on drives over 32 GB using Scour's own formatter), or create
  bootable sticks: Windows setup (splits `install.wim` over 4 GB, adds BIOS boot code), ISO file copy, or raw image (DD)

## Download

Get the latest `Scour.exe` (single file, no .NET install needed) from
[**Releases**](https://github.com/hqjake/Scour/releases/latest). Once installed, Scour keeps itself up to date.
Every push also builds a preview exe — see the **Actions** tab → latest run → *Scour* artifact.

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

**Releasing:** bump `<Version>` in `src/Scour.App/Scour.App.csproj` and update `docs/release-notes.md`. When that reaches
`main`, CI tags `v<version>` and publishes the GitHub Release with `Scour.exe`, which installed copies pick up through
the built-in updater. Code signing and winget publishing switch on with repository secrets — see
[docs/publishing.md](docs/publishing.md).

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

Unit-tested (193 tests) and exercised on Windows by CI, which runs the real app, opens every page (also in German) and fails the build on any error, and captures the screenshots above.
Please report anything that misbehaves, and attach `%ProgramData%\Scour\error.log`, where Scour records every error
with its details (Settings → Open data folder).
CPU core temperatures aren't shown: reading them needs a kernel driver, which Scour deliberately doesn't install.
