#!/bin/bash
# Builds CleanSweep.exe and CleanSweep.zip from app/ and writes version.json.
# Usage: ./release.sh "What's new text"
set -e; cd "$(dirname "$0")"
VER=$(grep -oP '^\$Version = "\K[^"]+' app/CleanSweep.ps1)
FILEVER="$VER.0.0"; FILEVER=$(echo "$FILEVER" | cut -d. -f1-4)
rm -rf build; mkdir -p build/CleanSweep dist

# 1. Script bundle embedded inside the exe
cp -r app/* build/CleanSweep/
(cd build && rm -f app.zip && zip -qrX app.zip CleanSweep)

# 2. Compile the launcher (.NET Framework 4.x, built into Windows 10/11)
sed -e "s/__APPVERSION__/$VER/" -e "s/__FILEVERSION__/$FILEVER/" launcher/Launcher.cs > build/Launcher.cs
# Windows resources: icon, admin manifest and version info
C=$(echo "$FILEVER" | tr . ,)
cp app/CleanSweep.ico build/app.ico; cp launcher/app.manifest build/app.manifest
cat > build/app.rc <<RC
1 ICON "app.ico"
1 24 "app.manifest"
1 VERSIONINFO
FILEVERSION $C
PRODUCTVERSION $C
FILEOS 0x4
FILETYPE 0x1
BEGIN
  BLOCK "StringFileInfo"
  BEGIN
    BLOCK "040904b0"
    BEGIN
      VALUE "CompanyName", "CleanSweep"
      VALUE "FileDescription", "CleanSweep PC Cleaner"
      VALUE "FileVersion", "$FILEVER"
      VALUE "InternalName", "CleanSweep.exe"
      VALUE "OriginalFilename", "CleanSweep.exe"
      VALUE "ProductName", "CleanSweep"
      VALUE "ProductVersion", "$VER"
    END
  END
  BLOCK "VarFileInfo"
  BEGIN
    VALUE "Translation", 0x409, 1200
  END
END
RC
(cd build && x86_64-w64-mingw32-windres --preprocessor=../launcher/rcpp.sh -O coff -i app.rc -o app.res)
mcs -nologo -target:winexe -sdk:4.5 -optimize+ -out:build/CleanSweep.exe \
  -win32res:build/app.res \
  -r:System.Windows.Forms.dll -r:System.IO.Compression.dll -r:System.IO.Compression.FileSystem.dll \
  -resource:build/app.zip,app.zip build/Launcher.cs
cp build/CleanSweep.exe dist/CleanSweep.exe

# 3. Update package (scripts + exe) used by the in-app updater
cp build/CleanSweep.exe build/CleanSweep/
rm -f CleanSweep.zip; (cd build && zip -qrX ../CleanSweep.zip CleanSweep)
mkdir -p releases; cp CleanSweep.zip "releases/CleanSweep-$VER.zip"   # unique file per version avoids stale CDN copies
cp dist/CleanSweep.exe "releases/CleanSweep-$VER.exe"
SHA=$(sha256sum CleanSweep.zip | cut -d' ' -f1 | tr a-f A-F)
python3 -c "import json,sys;json.dump({'version':sys.argv[1],'url':'https://raw.githubusercontent.com/maurizzioem-code/CleanSweep/main/releases/CleanSweep-'+sys.argv[1]+'.zip','sha256':sys.argv[2],'notes':sys.argv[3]},open('version.json','w'),indent=2)" "$VER" "$SHA" "$1"
rm -rf build
echo "Built $VER ($SHA)"
