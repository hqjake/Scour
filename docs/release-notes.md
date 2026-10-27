Fixes for features that failed or showed empty pages on many PCs, especially non-English Windows.

**Fixed in 1.2.1**

- **Non-English Windows** — messages from Windows tools (repairs, drivers, DISM, services) were garbled; restore-point cleanup always said "nothing to remove"; removing Windows.old failed; number boxes rejected decimal commas; valid shortcuts with non-English folder names were flagged as broken.
- **Pages no longer stay empty** — if a page can't load, it says why and tries again next time; errors are saved to `%ProgramData%\Scour\error.log`.
- **Standard user accounts** — when Scour runs as a different administrator account than the one signed in, a banner explains why browser, junk, startup and Windows-app tools don't see your files. App updates now find winget in that case too.
- **Windows tools are always started from System32**, so a damaged PATH no longer breaks Windows apps, DNS switching, USB tools and restore points.
- **App updates** work with older winget versions on Windows 10.
- **OneDrive folders** (Desktop, Documents with Files On-Demand) are included in disk analysis, large files and duplicates; online-only files aren't downloaded just to compare them.
- **Browsers** — newer Opera profile layouts, Firefox profiles kept elsewhere or installed from the Store, clearer "close the browser" messages, and one broken extension no longer hides the rest.
- The dashboard no longer freezes with a sleeping external disk or card reader; the uninstall wizard can always be closed after an error; Explorer restarts no longer open a second window; Sync the clock retries; privacy-tweak undo restores non-standard original values exactly; re-enabling a context-menu entry no longer fails.

Download `Scour.exe` below — a single file, no installation or .NET runtime needed. Installed copies of Scour offer this update automatically.
