#!/bin/bash
# Publishes the PowerShell edition's LAST update (5.0): a package whose Install.ps1 installs the C# edition
# and retires the PowerShell edition. Every PowerShell edition that checks for updates will offer it.
# Usage: csharp/publish-bridge.sh <GitHub Actions run id>   (the run must have passed, including the real retire test)
set -e; cd "$(dirname "$0")/.."
RUN="$1"; R=maurizzioem-code/CleanSweep; VER=5.0
[ -n "$RUN" ] || { echo 'usage: csharp/publish-bridge.sh <run id>'; exit 1; }
[ "$(gh run view "$RUN" --repo $R --json conclusion --jq .conclusion)" = "success" ] || { echo "run $RUN did not pass - not publishing"; exit 1; }
T=$(mktemp -d)
ID=$(gh api repos/$R/actions/runs/$RUN/artifacts --jq '.artifacts[]|select(.name=="CleanSweep-csharp-exe")|.id')
gh api repos/$R/actions/artifacts/$ID/zip > "$T/exe.zip"; unzip -qo "$T/exe.zip" -d "$T/exe"
mkdir -p "$T/pkg/CleanSweep"
cp "$T/exe/CleanSweep.exe" csharp/bridge/Install.ps1 "$T/pkg/CleanSweep/"
(cd "$T/pkg" && zip -qrX "$T/CleanSweep-$VER.zip" CleanSweep)
cp "$T/CleanSweep-$VER.zip" "releases/CleanSweep-$VER.zip"
SHA=$(sha256sum "releases/CleanSweep-$VER.zip" | cut -d' ' -f1 | tr a-f A-F)
python3 -c "import json,sys;json.dump({'version':sys.argv[1],'url':'https://raw.githubusercontent.com/$R/main/releases/CleanSweep-'+sys.argv[1]+'.zip','sha256':sys.argv[2],'notes':sys.argv[3]},open('version.json','w'),indent=2)" "$VER" "$SHA" \
  "CleanSweep 5.0 replaces this PowerShell edition with the new native Windows version: faster, and it stays responsive while it works. Your settings, history and automatic cleanup schedule (same days and time) are kept. The PowerShell edition's files are kept as a backup you can put back from Settings. This is the last update for the PowerShell edition."
rm -rf "$T"
echo "Built the PowerShell edition's last update $VER ($SHA) - commit and push releases/CleanSweep-$VER.zip and version.json"
