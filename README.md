# CleanSweep

A simple junk file, registry and broken shortcut cleaner with a Wi-Fi optimizer for Windows 11.

## Install
1. Download [CleanSweep.zip](https://raw.githubusercontent.com/maurizzioem-code/CleanSweep/main/CleanSweep.zip) and extract it.
2. Double-click `Install CleanSweep.bat`.

## Updates
CleanSweep checks `version.json` in this repository once a day. When a newer version is listed, it asks before downloading `CleanSweep.zip`, verifies its SHA-256 fingerprint, and reinstalls itself.

To publish a new version: bump `$Version` in `app/CleanSweep.ps1`, run `./release.sh "What's new"`, then commit and push.
