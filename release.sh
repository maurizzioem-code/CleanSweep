#!/bin/bash
# Builds CleanSweep.zip from app/ and writes version.json. Usage: ./release.sh "What's new text"
set -e; cd "$(dirname "$0")"
VER=$(grep -oP '^\$Version = "\K[^"]+' app/CleanSweep.ps1)
rm -rf build CleanSweep.zip; mkdir -p build/CleanSweep; cp app/* build/CleanSweep/
(cd build && zip -qrX ../CleanSweep.zip CleanSweep); rm -rf build
mkdir -p releases; cp CleanSweep.zip "releases/CleanSweep-$VER.zip"   # unique file per version avoids stale CDN copies
SHA=$(sha256sum CleanSweep.zip | cut -d' ' -f1 | tr a-f A-F)
python3 -c "import json,sys;json.dump({'version':sys.argv[1],'url':'https://raw.githubusercontent.com/maurizzioem-code/CleanSweep/main/releases/CleanSweep-'+sys.argv[1]+'.zip','sha256':sys.argv[2],'notes':sys.argv[3]},open('version.json','w'),indent=2)" "$VER" "$SHA" "$1"
echo "Built $VER ($SHA)"
