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
function Get-Items($paths) {
  foreach ($p in $paths) {
    Get-Item -Path $p -Force -ErrorAction Ignore | ForEach-Object {
      if ($_.PSIsContainer) { Get-ChildItem $_.FullName -Recurse -Force -File -ErrorAction Ignore } else { $_ }
    }
  }
}
