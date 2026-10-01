# CleanSweep automatic cleanup - started by Windows Task Scheduler (or "Run now" on the Dashboard).
# Runs without a window and only removes the junk categories chosen in the Dashboard schedule settings.
# Safety rules: never touches files changed in the last 24 hours, only empties Recycle Bin items older
# than the chosen age, skips files that are in use, and never changes settings, services or the registry.
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
  try { $cfg = (Get-Content -Raw $settingsFile | ConvertFrom-Json).AutoClean } catch {}
  if (-not $cfg) { Log "No schedule settings found - nothing to do."; exit 0 }
  $cats = @($cfg.Cats); $recycleDays = [int]$cfg.RecycleDays
  $sw = [Diagnostics.Stopwatch]::StartNew(); $cut = (Get-Date).AddDays(-1)
  $freed = [long]0; $files = 0; $skipped = 0; $parts = @()
  Log "Started ($Trigger). Categories: $($cats -join ', ')$(if ($recycleDays) { "; Recycle Bin items older than $recycleDays days" })"

  foreach ($name in $cats) {
    if (-not $targets.Contains($name)) { continue }
    $f0 = $freed
    foreach ($f in (Get-Items $targets[$name])) {
      if ($f.LastWriteTime -gt $cut -or $f.CreationTime -gt $cut) { $skipped++; continue }   # may still be in use
      $len = $f.Length
      try { Remove-Item -LiteralPath $f.FullName -Force -ErrorAction Stop; $freed += $len; $files++ } catch { $skipped++ }
    }
    # Remove empty folders left behind in temp locations (not the root folders themselves)
    if ($name -match 'Temp') {
      foreach ($root in $targets[$name]) {
        Get-ChildItem -LiteralPath $root -Directory -Recurse -Force -ErrorAction Ignore | Sort-Object { $_.FullName.Length } -Descending |
          Where-Object { -not ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -and $_.LastWriteTime -lt $cut -and -not (Get-ChildItem -LiteralPath $_.FullName -Force -ErrorAction Ignore | Select-Object -First 1) } |
          ForEach-Object { Remove-Item -LiteralPath $_.FullName -Force -ErrorAction Ignore }
      }
    }
    $parts += "$name $(Fmt ($freed - $f0))"
  }

  # Recycle Bin: only items deleted more than N days ago (each deleted item has a $I info file and a $R data item)
  if ($recycleDays -gt 0) {
    $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value; $old = (Get-Date).AddDays(-$recycleDays); $f0 = $freed
    foreach ($d in (Get-CimInstance Win32_LogicalDisk -Filter "DriveType=3")) {
      $bin = "$($d.DeviceID)\`$Recycle.Bin\$sid"
      foreach ($i in (Get-ChildItem -LiteralPath $bin -Force -Filter '$I*' -ErrorAction Ignore)) {
        if ($i.LastWriteTime -gt $old) { continue }
        $r = Join-Path $bin ('$R' + $i.Name.Substring(2))
        $len = 0; if (Test-Path -LiteralPath $r) { $len = (Get-ChildItem -LiteralPath $r -Recurse -Force -File -ErrorAction Ignore | Measure-Object Length -Sum).Sum; if (-not $len) { $len = (Get-Item -LiteralPath $r -Force).Length } }
        try { if (Test-Path -LiteralPath $r) { Remove-Item -LiteralPath $r -Recurse -Force -ErrorAction Stop }; Remove-Item -LiteralPath $i.FullName -Force -ErrorAction Stop; $freed += [long]$len; $files++ } catch { $skipped++ }
      }
    }
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
