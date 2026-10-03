# CleanSweep automatic cleanup - started by Windows Task Scheduler (or "Run now" on the Dashboard).
# Runs without a window and only removes the junk categories chosen in the Dashboard schedule settings.
# Uses the same cleaning engine and safety rules as the Cleanup page (modules\JunkTargets.ps1): files changed in the
# last 24 hours, ignored files and files in use are skipped; links are never followed; only old Recycle Bin items go.
param([string]$Trigger = "Scheduled")
$ErrorActionPreference = "SilentlyContinue"
$AppDir = Join-Path $env:LOCALAPPDATA "CleanSweep"
$settingsFile = Join-Path $AppDir "settings.json"
$HistoryFile = Join-Path $AppDir "autoclean-history.csv"
$LogDir = Join-Path $AppDir "logs"; New-Item -ItemType Directory $LogDir -Force | Out-Null
$LogFile = Join-Path $LogDir "autoclean.log"
function Log($t) { Add-Content -LiteralPath $LogFile -Value ((Get-Date).ToString("yyyy-MM-dd HH:mm:ss") + "  " + $t) -Encoding UTF8 }
function Fmt($b) { if ($b -ge 1GB) { "{0:N2} GB" -f ($b/1GB) } elseif ($b -ge 1MB) { "{0:N1} MB" -f ($b/1MB) } else { "{0:N0} KB" -f ($b/1KB) } }

# One run at a time
$mutex = New-Object Threading.Mutex($false, "Local\CleanSweepAutoClean")
if (-not $mutex.WaitOne(0)) { exit 0 }
try {
  if ((Test-Path $LogFile) -and (Get-Item $LogFile).Length -gt 1MB) { Move-Item $LogFile "$LogFile.old" -Force }
  . (Join-Path $PSScriptRoot "modules\JunkTargets.ps1")

  $cfg = $null
  try { $all = Get-Content -Raw $settingsFile | ConvertFrom-Json; $cfg = $all.AutoClean; $global:CSIgnore = @($all.TempIgnore | Where-Object { $_ }) } catch {}
  if (-not $cfg) { Log "No schedule settings found - nothing to do."; exit 0 }
  $cats = @($cfg.Cats); $recycleDays = [int]$cfg.RecycleDays
  $sw = [Diagnostics.Stopwatch]::StartNew(); $cut = (Get-Date).AddDays(-1)
  $freed = [long]0; $files = 0; $skipped = 0; $parts = @()
  Log "Started ($Trigger). Categories: $($cats -join ', ')$(if ($recycleDays) { "; Recycle Bin items older than $recycleDays days" })"

  foreach ($name in $cats) {
    if (-not $targets.Contains($name)) { continue }
    $f0 = $freed; $stat = New-CleanStat
    foreach ($f in (Get-CleanFiles $targets[$name] $cut $stat)) {
      $len = $f.Length
      if (Remove-CleanFile $f.FullName) { $skipped++ } else { $freed += $len; $files++ }
    }
    $skipped += $stat.Recent
    if ($name -match 'Temp') { foreach ($root in $targets[$name]) { Remove-EmptyDirs $root $cut } }
    $parts += "$name $(Fmt ($freed - $f0))"
  }

  # Recycle Bin: only items deleted more than N days ago
  if ($recycleDays -gt 0) {
    $f0 = $freed
    foreach ($it in (Get-OldRecycleItems $recycleDays)) { if (Remove-RecycleItem $it) { $skipped++ } else { $freed += [long]$it.Length; $files++ } }
    $parts += "Recycle Bin $(Fmt ($freed - $f0))"
  }

  $sec = [int]$sw.Elapsed.TotalSeconds
  $summary = "Freed $(Fmt $freed) ($files files). Skipped $skipped files that were recent or in use."
  Log "$summary  [$($parts -join '; ')]  ${sec}s"
  $row = [pscustomobject]@{ Time = (Get-Date).ToString("o"); Trigger = $Trigger; Freed = $freed; Files = $files; Skipped = $skipped; Seconds = $sec; Details = ($parts -join '; ') }
  $row | Export-Csv -LiteralPath $HistoryFile -Append -NoTypeInformation -Encoding UTF8
  # Keep the last 100 runs
  $all = @(Import-Csv -LiteralPath $HistoryFile); if ($all.Count -gt 100) { $all | Select-Object -Last 100 | Export-Csv -LiteralPath $HistoryFile -NoTypeInformation -Encoding UTF8 }

  if ($cfg.Notify -and $Trigger -eq "Scheduled" -and $env:CLEANSWEEP_TEST -ne "1") {
    Add-Type -AssemblyName System.Windows.Forms, System.Drawing
    $ni = New-Object Windows.Forms.NotifyIcon
    try { $ni.Icon = New-Object Drawing.Icon (Join-Path $PSScriptRoot "CleanSweep.ico") } catch { $ni.Icon = [Drawing.SystemIcons]::Information }
    $ni.Visible = $true; $ni.ShowBalloonTip(8000, "CleanSweep automatic cleanup", $summary, 'Info')
    $end = (Get-Date).AddSeconds(9); while ((Get-Date) -lt $end) { [Windows.Forms.Application]::DoEvents(); Start-Sleep -Milliseconds 200 }
    $ni.Dispose()
  }
} catch { Log "Error: $_" }
finally { $mutex.ReleaseMutex() }
