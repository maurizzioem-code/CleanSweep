# CleanSweep

A simple junk file, registry and broken shortcut cleaner with a Wi-Fi and Ethernet network optimizer for Windows 11.

## Install
1. Download [CleanSweep.exe](https://raw.githubusercontent.com/maurizzioem-code/CleanSweep/main/releases/CleanSweep-3.0.exe) and run it. It installs itself and adds Desktop and Start menu shortcuts.
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
