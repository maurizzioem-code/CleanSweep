#!/bin/bash
# Builds CleanSweep.exe and CleanSweep.zip from app/ and writes version.json.
# Usage: ./release.sh "What's new text"
set -e; cd "$(dirname "$0")"
VER=$(grep -oP '^\$Version = "\K[^"]+' app/CleanSweep.ps1)
FILEVER="$VER.0.0"; FILEVER=$(echo "$FILEVER" | cut -d. -f1-4)
rm -rf build; mkdir -p build/CleanSweep dist

cp -r app/* build/CleanSweep/

# 2. Compile the launcher (.NET Framework 4.x, built into Windows 10/11)
sed -e "s/__APPVERSION__/$VER/" -e "s/__FILEVERSION__/$FILEVER/" launcher/Launcher.cs > build/Launcher.cs
# CleanSweep.exe is compiled on a real Windows VM by GitHub Actions (launcher/build-exe.ps1, csc.exe)
# and downloaded into dist/ before running this script.
[ -f dist/CleanSweep.exe ] || { echo "dist/CleanSweep.exe missing - download it from the Windows test run first"; exit 1; }


# 3. Update package (scripts + exe) used by the in-app updater
cp dist/CleanSweep.exe build/CleanSweep/
rm -f CleanSweep.zip; (cd build && zip -qrX ../CleanSweep.zip CleanSweep)
mkdir -p releases; cp CleanSweep.zip "releases/CleanSweep-$VER.zip"   # unique file per version avoids stale CDN copies
cp dist/CleanSweep.exe "releases/CleanSweep-$VER.exe"
SHA=$(sha256sum CleanSweep.zip | cut -d' ' -f1 | tr a-f A-F)
python3 -c "import json,sys;json.dump({'version':sys.argv[1],'url':'https://raw.githubusercontent.com/maurizzioem-code/CleanSweep/main/releases/CleanSweep-'+sys.argv[1]+'.zip','sha256':sys.argv[2],'notes':sys.argv[3]},open('version.json','w'),indent=2)" "$VER" "$SHA" "$1"
rm -rf build
echo "Built $VER ($SHA)"
