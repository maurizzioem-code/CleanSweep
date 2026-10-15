# CleanSweep 5.0 - the PowerShell edition's last update.
# The PowerShell edition's updater downloads this package, checks its SHA-256 fingerprint and runs this file with -Quiet.
# It installs the new CleanSweep (C# edition) for this account and retires the PowerShell edition:
#   - the automatic cleanup keeps its days, time and conditions (only the program it runs changes)
#   - the PowerShell edition's program files move to "%LOCALAPPDATA%\CleanSweep\old PowerShell edition" (nothing is deleted)
#   - settings, history, logs and registry backups stay where they are
# If anything goes wrong the PowerShell edition is left as it was and opened again.
param([switch]$Quiet)
$src  = $PSScriptRoot
$data = Join-Path $env:LOCALAPPDATA "CleanSweep"
$logf = Join-Path $data "logs\move-to-5.0.log"
function Log($t) {
  try { New-Item -ItemType Directory -Path (Split-Path $logf) -Force | Out-Null; Add-Content -LiteralPath $logf -Value ((Get-Date).ToString("yyyy-MM-dd HH:mm:ss") + "  " + $t) } catch {}
  if (-not $Quiet) { Write-Host $t }
}
Log "Moving to CleanSweep 5.0 from $src"
$exe = Join-Path $src "CleanSweep.exe"
if (-not (Test-Path $exe)) { Log "CleanSweep.exe is missing from the package - nothing was changed."; exit 1 }
Unblock-File -LiteralPath $exe -ErrorAction SilentlyContinue

# Close the PowerShell edition so its files can be moved (the new version also checks this)
Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" -ErrorAction SilentlyContinue |
  Where-Object { $_.CommandLine -like "*CleanSweep.ps1*" } |
  ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
Start-Sleep -Seconds 1

try {
  $p = Start-Process -FilePath $exe -ArgumentList "--install", "--quiet", "--retire-powershell" -PassThru -Wait -ErrorAction Stop
  Log "Installer exit code: $($p.ExitCode)"
  $ok = $p.ExitCode -eq 0
} catch { Log "Could not start the new CleanSweep: $_"; $ok = $false }

$installed = Join-Path $env:LOCALAPPDATA "Programs\CleanSweep\CleanSweep.exe"
if ($ok -and (Test-Path $installed)) {
  if (Test-Path (Join-Path $data "CleanSweep.ps1")) { Log "Installed CleanSweep 5.0. The PowerShell edition was kept (see app.log) - retire it from Settings." }
  else { Log "Installed CleanSweep 5.0 and retired the PowerShell edition." }
} else {
  Log "The new CleanSweep was not installed - reopening the PowerShell edition unchanged."
  $old = Join-Path $data "CleanSweep.exe"
  if (Test-Path $old) { Start-Process $old }
  exit 1
}
