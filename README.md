# CleanSweep

A Windows 11 style PC health app: a dark dashboard with a health score, live hardware monitoring and one-click actions, plus junk cleaning, drive tools, repair tools and a network optimizer.

## Features
- **Dashboard**: health score out of 100 with recommendations, score history, live CPU / memory / GPU / disk / network graphs, battery and temperatures, and one-click actions (Quick clean, Free up space, Repair Windows, Optimize drives, Network check, Restore point).
- **Automatic cleanup**: schedule junk cleaning daily, weekly or every 4 weeks with Windows Task Scheduler. You choose what gets cleaned; files changed in the last 24 hours are skipped, and Recycle Bin items are only removed once they are older than the age you pick. Turning it off removes the task.
- **Cleanup**: one page for junk and temp files (your temp folder, Windows temp, other users' temp, internet temp files, Windows Update leftovers, crash reports, optional browser/thumbnail/shader caches, leftover temp files on other drives, and Recycle Bin items older than the age you pick). Files newer than the chosen age are skipped and links are never followed. Files that can't be deleted are listed with the reason and the app using them: Retry, Delete at next restart, Ignore this time, or Always ignore.
- **Drives**: every volume with a checkbox (hidden Windows partitions are shown but protected); clean junk, find large files, check for errors (`chkdsk /scan`), optimize (TRIM / defrag).
- **Repair**: DISM, System File Checker and Windows Update repair (with undo), live output and logs.
- **Broken Shortcuts**: removes shortcuts whose target is gone (to the Recycle Bin).
- **Registry cleaner** (advanced, hidden by default): conservative, with backups. Turn it on under Settings.
- **Network Optimizer**: Wi-Fi and Ethernet tests, standard repairs and Ethernet diagnostics.
- **Settings**: automatic updates (SHA-256 and signature verified), installation, and advanced tools.

## Install
Download [CleanSweep.exe](https://raw.githubusercontent.com/maurizzioem-code/CleanSweep/main/releases/csharp/CleanSweep.exe) and run it, then click **Install**. It installs for your account only (`%LOCALAPPDATA%\Programs\CleanSweep`), adds Start menu and Desktop shortcuts, and is listed in Settings > Apps so you can uninstall it like any other app.

## Editions
- **CleanSweep 5.0** (`csharp/`) is the current version: a native Windows app (.NET Framework 4.8, built into Windows 10/11).
- **The PowerShell edition (4.x)** (`app/`) is retired. Installing 5.0 offers to retire it: the automatic cleanup keeps its days, time and conditions, settings and history are shared, and its program files are kept as a backup you can put back from Settings. Its own updater offers 5.0 as its last update, which does the same.

## Updates
CleanSweep 5.0 checks `update/csharp.json` once a day. When a newer build is listed it shows a notice; nothing is downloaded until you click Install update. The download must match the published SHA-256 fingerprint, product name and build, and once CleanSweep is digitally signed, it must be signed by the same publisher.

To publish a build that passed the tests: `csharp/publish.sh <run id> "What's new"`, then commit and push.

## Code signing
See [docs/code-signing.md](docs/code-signing.md) for the code signing policy and how signing is set up.

## Design rules
CleanSweep focuses on monitoring, cleanup, repair and recommendations. It does not blindly change system settings:
- No aggressive registry cleaning: only entries pointing to files that no longer exist, low-value entries are left unticked, and every clean is backed up (plus an optional restore point).
- No automatic service disabling.
- No memory "boosters" that force RAM clearing.
- No driver-updater bundles.
- No TCP/IP "gaming tweaks" without evidence. Network fixes are standard Windows repairs (DNS flush, IP renew, restoring defaults), and optional changes are off unless you tick them.
- Nothing changes without you clicking, and changes can be undone.

## Testing
Every push is built and tested on a Windows virtual machine by GitHub Actions (`.github/workflows/test.yml`, `tests/Test-CleanSweep.ps1`): layout checks, every tab driven end to end, screenshots, and the exe install/launch.
