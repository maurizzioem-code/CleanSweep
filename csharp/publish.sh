#!/bin/bash
# Publishes a tested C# build as the update every installed CleanSweep (C# edition) will offer.
# Usage: csharp/publish.sh <GitHub Actions run id> "What's new text"
#   1. downloads CleanSweep.exe from that run (it must have passed the self-test)
#   2. copies it to releases/csharp/CleanSweep-<version>.exe (a unique name per build, so caches never serve an old file)
#   3. writes update/csharp.json with the version, download URL and SHA-256 fingerprint
# Then commit and push. Pushing releases/ and update/ does not start a new build.
set -e; cd "$(dirname "$0")/.."
RUN="$1"; NOTES="$2"; R=maurizzioem-code/CleanSweep
[ -n "$RUN" ] && [ -n "$NOTES" ] || { echo 'usage: csharp/publish.sh <run id> "notes"'; exit 1; }
[ "$(gh run view "$RUN" --repo $R --json conclusion --jq .conclusion)" = "success" ] || { echo "run $RUN did not pass - not publishing"; exit 1; }
T=$(mktemp -d)
ID=$(gh api repos/$R/actions/runs/$RUN/artifacts --jq '.artifacts[]|select(.name=="CleanSweep-csharp-exe")|.id')
gh api repos/$R/actions/artifacts/$ID/zip > "$T/exe.zip"; unzip -qo "$T/exe.zip" -d "$T"
VER=$(python3 - "$T/CleanSweep.exe" <<'EOF'
import sys, pefile
pe = pefile.PE(sys.argv[1]); fi = pe.VS_FIXEDFILEINFO[0]
print(f"{fi.FileVersionMS>>16}.{fi.FileVersionMS&0xFFFF}.{fi.FileVersionLS>>16}.{fi.FileVersionLS&0xFFFF}")
EOF
)
mkdir -p releases/csharp update
cp "$T/CleanSweep.exe" "releases/csharp/CleanSweep-$VER.exe"
SHA=$(sha256sum "releases/csharp/CleanSweep-$VER.exe" | cut -d' ' -f1 | tr a-f A-F)
SIZE=$(stat -c %s "releases/csharp/CleanSweep-$VER.exe")
python3 -c "import json,sys;json.dump({'version':sys.argv[1],'url':'https://raw.githubusercontent.com/$R/main/releases/csharp/CleanSweep-'+sys.argv[1]+'.exe','sha256':sys.argv[2],'size':int(sys.argv[3]),'notes':sys.argv[4]},open('update/csharp.json','w'),indent=2)" "$VER" "$SHA" "$SIZE" "$NOTES"
rm -rf "$T"
echo "Published $VER ($SHA, $SIZE bytes) - now commit and push releases/csharp and update/csharp.json"
