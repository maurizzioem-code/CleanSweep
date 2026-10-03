# Shared by the app and the scheduled cleaner (AutoClean.ps1)
$L = $env:LOCALAPPDATA; $W = $env:WINDIR
# Windows junk (only lives on the system drive)
$targets = [ordered]@{
  "User Temp Files"             = @("$env:TEMP")
  "Windows Temp Files"          = @("$W\Temp")
  "Windows Update Cache"        = @("$W\SoftwareDistribution\Download")
  "Prefetch Files"              = @("$W\Prefetch")
  "Thumbnail Cache"             = @("$L\Microsoft\Windows\Explorer\thumbcache_*.db")
  "Crash Dumps & Error Reports" = @("$L\CrashDumps","$L\Microsoft\Windows\WER","$env:ProgramData\Microsoft\Windows\WER")
  "Chrome Cache"                = @("$L\Google\Chrome\User Data\*\Cache","$L\Google\Chrome\User Data\*\Code Cache")
  "Edge Cache"                  = @("$L\Microsoft\Edge\User Data\*\Cache","$L\Microsoft\Edge\User Data\*\Code Cache")
  "Firefox Cache"               = @("$L\Mozilla\Firefox\Profiles\*\cache2")
  "Delivery Optimization"       = @("$W\ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Cache")
}
# Files the user chose to "Always ignore" (Temp Files page): rules are file:<path>, folder:<path>\ or ext:<folder>\*.<ext>
if (-not $global:CSIgnore) { $global:CSIgnore = @() }
function Test-CSIgnored([string]$p) {
  foreach ($r in $global:CSIgnore) {
    if (-not $r) { continue }
    if ($r.StartsWith('file:')) { if ($p -ieq $r.Substring(5)) { return $true } }
    elseif ($r.StartsWith('folder:')) { if ($p.StartsWith($r.Substring(7), [StringComparison]::OrdinalIgnoreCase)) { return $true } }
    elseif ($r.StartsWith('ext:')) {
      $v = $r.Substring(4); $dir = [IO.Path]::GetDirectoryName($v); $ext = [IO.Path]::GetExtension($v)
      if ([IO.Path]::GetDirectoryName($p) -ieq $dir -and [IO.Path]::GetExtension($p) -ieq $ext) { return $true }
    }
  }
  $false
}
function Get-Items($paths) {
  foreach ($p in $paths) {
    Get-Item -Path $p -Force -ErrorAction Ignore | ForEach-Object {
      if ($_.PSIsContainer) { Get-ChildItem $_.FullName -Recurse -Force -File -ErrorAction Ignore } else { $_ }
    } | Where-Object { -not $global:CSIgnore.Count -or -not (Test-CSIgnored $_.FullName) }
  }
}
