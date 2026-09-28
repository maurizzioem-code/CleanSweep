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

New-Item -ItemType Directory -Path $dest -Force | Out-Null
foreach ($f in "CleanSweep.ps1","CleanSweep.vbs","CleanSweep.ico") {
  Copy-Item -LiteralPath (Join-Path $src $f) -Destination $dest -Force
}
Get-ChildItem $dest | Unblock-File -ErrorAction SilentlyContinue

# Replace any old desktop shortcut
$desk = [Environment]::GetFolderPath('Desktop')
$lnk  = Join-Path $desk "CleanSweep.lnk"
Remove-Item -LiteralPath $lnk -Force -ErrorAction SilentlyContinue
$s = (New-Object -ComObject WScript.Shell).CreateShortcut($lnk)
$s.TargetPath       = "$env:SystemRoot\System32\wscript.exe"
$s.Arguments        = '"' + (Join-Path $dest "CleanSweep.vbs") + '"'
$s.WorkingDirectory = $dest
$s.IconLocation     = (Join-Path $dest "CleanSweep.ico") + ",0"
$s.Description      = "CleanSweep Disk Cleaner"
$s.Save()

# Refresh desktop icons
& ie4uinit.exe -show 2>$null

$ver = (Select-String -LiteralPath (Join-Path $dest "CleanSweep.ps1") -Pattern '^\$Version = "(.+)"').Matches.Groups[1].Value
if (-not $Quiet) {
  Write-Host ""
  Write-Host "CleanSweep $ver installed to $dest" -ForegroundColor Green
  Write-Host "Desktop shortcut updated. Starting CleanSweep..."
}
Start-Process "$env:SystemRoot\System32\wscript.exe" -ArgumentList ('"' + (Join-Path $dest "CleanSweep.vbs") + '"')
if (-not $Quiet) { Start-Sleep -Seconds 3 }
