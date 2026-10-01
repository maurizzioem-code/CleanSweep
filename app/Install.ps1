# Installs (or updates) CleanSweep to a fixed folder and creates the desktop shortcut
# -Quiet is used by the built-in updater (no console messages)
param([switch]$Quiet)
$src  = $PSScriptRoot
$dest = Join-Path $env:LOCALAPPDATA "CleanSweep"

# Close any running copy so its files can be replaced
Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" -ErrorAction SilentlyContinue |
  Where-Object { $_.CommandLine -like "*CleanSweep.ps1*" } |
  ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
Start-Sleep -Seconds 1

# Copy everything in the package (scripts, modules, icon, CleanSweep.exe) but never overwrite user settings
New-Item -ItemType Directory -Path $dest -Force | Out-Null
Get-ChildItem -LiteralPath $src -Recurse -File | Where-Object { $_.Name -ne "settings.json" } | ForEach-Object {
  $rel = $_.FullName.Substring($src.Length).TrimStart('\')
  $to  = Join-Path $dest $rel
  New-Item -ItemType Directory -Path (Split-Path $to) -Force | Out-Null
  Copy-Item -LiteralPath $_.FullName -Destination $to -Force -ErrorAction SilentlyContinue
}
Get-ChildItem $dest -Recurse -File | Unblock-File -ErrorAction SilentlyContinue

# Shortcuts open CleanSweep.exe (no console window); fall back to the VBS launcher on older packages
$exe = Join-Path $dest "CleanSweep.exe"
$ico = Join-Path $dest "CleanSweep.ico"
$wsh = New-Object -ComObject WScript.Shell
foreach ($lnk in (Join-Path ([Environment]::GetFolderPath('Desktop')) "CleanSweep.lnk"),
                 (Join-Path ([Environment]::GetFolderPath('Programs')) "CleanSweep.lnk")) {
  Remove-Item -LiteralPath $lnk -Force -ErrorAction SilentlyContinue
  $s = $wsh.CreateShortcut($lnk)
  if (Test-Path $exe) { $s.TargetPath = $exe }
  else { $s.TargetPath = "$env:SystemRoot\System32\wscript.exe"; $s.Arguments = '"' + (Join-Path $dest "CleanSweep.vbs") + '"' }
  $s.WorkingDirectory = $dest; $s.IconLocation = "$ico,0"; $s.Description = "CleanSweep"; $s.Save()
}
& ie4uinit.exe -show 2>$null

$ver = (Select-String -LiteralPath (Join-Path $dest "CleanSweep.ps1") -Pattern '^\$Version = "(.+)"').Matches.Groups[1].Value
if (-not $Quiet) {
  Write-Host ""
  Write-Host "CleanSweep $ver installed to $dest" -ForegroundColor Green
  Write-Host "Desktop and Start menu shortcuts updated. Starting CleanSweep..."
}
if (Test-Path $exe) { Start-Process $exe }
else { Start-Process "$env:SystemRoot\System32\wscript.exe" -ArgumentList ('"' + (Join-Path $dest "CleanSweep.vbs") + '"') }
if (-not $Quiet) { Start-Sleep -Seconds 3 }
