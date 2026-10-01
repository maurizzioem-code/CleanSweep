# CleanSweep

A Windows 11 style PC health app: a dark dashboard with a health score, live hardware monitoring and one-click actions, plus junk cleaning, drive tools, repair tools and a network optimizer.

## Features
- **Dashboard**: health score out of 100 with recommendations, score history, live CPU / memory / GPU / disk / network graphs, battery and temperatures, and one-click actions (Quick clean, Free up space, Repair Windows, Optimize drives, Network check, Restore point).
- **Automatic cleanup**: schedule junk cleaning daily, weekly or every 4 weeks with Windows Task Scheduler. You choose what gets cleaned; files changed in the last 24 hours are skipped, and Recycle Bin items are only removed once they are older than the age you pick. Turning it off removes the task.
- **Junk Files**: temp files, caches, Windows Update leftovers and Recycle Bin on any drive.
- **Drives**: every volume with a checkbox (hidden Windows partitions are shown but protected); clean junk, find large files, check for errors (`chkdsk /scan`), optimize (TRIM / defrag).
- **Repair**: DISM, System File Checker and Windows Update repair (with undo), live output and logs.
- **Registry** and **Broken Shortcuts**: conservative cleaning with backups.
- **Network Optimizer**: Wi-Fi and Ethernet tests, standard repairs and Ethernet diagnostics.
- **Updates**: automatic, SHA-256 verified.

## Install
1. Download [CleanSweep.exe](https://raw.githubusercontent.com/maurizzioem-code/CleanSweep/main/releases/CleanSweep-4.1.exe) and run it. It installs itself and adds Desktop and Start menu shortcuts.
2. Or download [CleanSweep.zip](https://raw.githubusercontent.com/maurizzioem-code/CleanSweep/main/CleanSweep.zip), extract it and run `Install CleanSweep.bat`.

## Updates
CleanSweep checks `version.json` in this repository once a day. When a newer version is listed, it asks before downloading `CleanSweep.zip`, verifies its SHA-256 fingerprint, and reinstalls itself.

To publish a new version: bump `$Version` in `app/CleanSweep.ps1`, run `./release.sh "What's new"`, then commit and push.

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
