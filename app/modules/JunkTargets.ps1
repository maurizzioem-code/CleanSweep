# CleanSweep cleaning engine - shared by the Cleanup page, Drives, the Dashboard and the scheduled cleaner (AutoClean.ps1).
# One set of safety rules everywhere:
#   - only known junk locations, walked by CleanSweep itself so junctions/symbolic links are never followed out of them
#   - files changed more recently than the chosen age are skipped (they may still be in use)
#   - "Always ignore" rules are respected
#   - files that can't be deleted are reported, never forced
$L = $env:LOCALAPPDATA; $W = $env:WINDIR
if (-not $SysDrive) { $SysDrive = $env:SystemDrive.TrimEnd('\') }
$MySid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$MyTemp = try { (Get-Item -LiteralPath $env:TEMP -Force -ErrorAction Stop).FullName.TrimEnd('\') } catch { $env:TEMP }   # long form of 8.3 paths

function Get-OtherUserTemps {
  $mine = [Environment]::GetFolderPath('UserProfile')
  @(Get-ChildItem -LiteralPath (Split-Path $mine) -Directory -Force -ErrorAction Ignore |
    Where-Object { $_.FullName -ne $mine -and $_.Name -notin 'Default','Default User','Public','All Users' -and -not ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) } |
    ForEach-Object { Join-Path $_.FullName "AppData\Local\Temp" } | Where-Object { (Test-Path -LiteralPath $_) -and $_ -ne $MyTemp })
}

# Junk on the Windows drive. Names are stable (saved schedules refer to them).
# Prefetch Files was removed in 4.3: Windows uses those files to start apps faster.
$targets = [ordered]@{
  "User Temp Files"             = @($MyTemp)
  "Windows Temp Files"          = @("$W\Temp")
  "Other Users' Temp Files"     = @(Get-OtherUserTemps)
  "Internet Temporary Files"    = @("$L\Microsoft\Windows\INetCache")
  "Windows Update Cache"        = @("$W\SoftwareDistribution\Download")
  "Crash Dumps & Error Reports" = @("$L\CrashDumps","$L\Microsoft\Windows\WER","$env:ProgramData\Microsoft\Windows\WER")
  "Delivery Optimization"       = @("$W\ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Cache")
  "Thumbnail Cache"             = @("$L\Microsoft\Windows\Explorer\thumbcache_*.db")
  "Chrome Cache"                = @("$L\Google\Chrome\User Data\*\Cache","$L\Google\Chrome\User Data\*\Code Cache")
  "Edge Cache"                  = @("$L\Microsoft\Edge\User Data\*\Cache","$L\Microsoft\Edge\User Data\*\Code Cache")
  "Firefox Cache"               = @("$L\Mozilla\Firefox\Profiles\*\cache2")
  "DirectX Shader Cache"        = @("$L\D3DSCache")
}
if (-not $targets["Other Users' Temp Files"].Count) { $targets.Remove("Other Users' Temp Files") }
# Ticked by default (safe to remove, real space); the rest are offered with a note on the trade-off
$CleanDefaults = @("User Temp Files", "Windows Temp Files", "Other Users' Temp Files", "Internet Temporary Files", "Windows Update Cache", "Crash Dumps & Error Reports", "Delivery Optimization")
$CleanNotes = @{
  "Thumbnail Cache"      = "folders with pictures open slowly until rebuilt"
  "Chrome Cache"         = "websites load a little slower at first"
  "Edge Cache"           = "websites load a little slower at first"
  "Firefox Cache"        = "websites load a little slower at first"
  "DirectX Shader Cache" = "games rebuild it and may stutter briefly"
}

# ---------------------------------------------------------------- ignore rules ("Always ignore")
# Rules: file:<path>, folder:<path>\ or ext:<folder>\*.<ext>
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

# ---------------------------------------------------------------- finding files
function New-CleanStat { @{ Recent = 0; Ignored = 0; Links = 0; Unreadable = 0 } }
$script:CleanHasCancel = [bool](Get-Command Check-Cancel -ErrorAction Ignore)
# Files under the given paths (wildcards allowed) older than $cut. Never follows junctions or symbolic links.
# $nameFilter limits to matching file names; $skipRoot skips matching folder names at a drive root.
function Get-CleanFiles($paths, [datetime]$cut = [datetime]::MaxValue, $stat = $null, [string]$nameFilter = '', [string]$skipRoot = '') {
  if (-not $stat) { $stat = New-CleanStat }
  foreach ($p in $paths) {
    if (-not $p) { continue }
    foreach ($start in @(Get-Item -Path $p -Force -ErrorAction Ignore)) {
      if (-not $start.PSIsContainer) {
        if (Test-CSIgnored $start.FullName) { $stat.Ignored++ } elseif ($start.LastWriteTime -gt $cut) { $stat.Recent++ } else { $start }
        continue
      }
      $stack = New-Object System.Collections.Stack; $stack.Push($start.FullName); $n = 0
      while ($stack.Count) {
        $dir = $stack.Pop()
        if ($script:CleanHasCancel -and (++$n % 100) -eq 0) { Check-Cancel }
        $entries = try { ([IO.DirectoryInfo]$dir).GetFileSystemInfos() } catch { $stat.Unreadable++; @() }
        foreach ($e in $entries) {
          if ($e.Attributes -band [IO.FileAttributes]::Directory) {
            if ($e.Attributes -band [IO.FileAttributes]::ReparsePoint) { $stat.Links++; continue }
            if ($skipRoot -and $dir.Length -le 3 -and $e.Name -match $skipRoot) { continue }
            $stack.Push($e.FullName); continue
          }
          if ($nameFilter -and $e.Name -notmatch $nameFilter) { continue }
          if (Test-CSIgnored $e.FullName) { $stat.Ignored++; continue }
          if ($e.LastWriteTime -gt $cut) { $stat.Recent++; continue }
          $e
        }
      }
    }
  }
}
# All files (no age limit) - used for size estimates
function Get-Items($paths) { Get-CleanFiles $paths }

# Leftover temp files anywhere on a non-Windows drive (skips system and program folders)
$DriveSkipDirs = '^(\$Recycle\.Bin|System Volume Information|Windows|Program Files|Program Files \(x86\)|ProgramData|Recovery|\$WinREAgent|\$SysReset|Config\.Msi|MSOCache)$'
$DriveJunkNames = '(\.tmp|\.temp|\._mp|\.chk|\.gid|\.old\.tmp)$|^(Thumbs\.db|ehthumbs\.db|~\$.+)$'
function Get-DriveJunk([string]$drive, [datetime]$cut, $stat = $null) { Get-CleanFiles @("$drive\") $cut $stat $DriveJunkNames $DriveSkipDirs }

# Recycle Bin items deleted more than $days ago (each item = a $I info file + a $R data file or folder)
function Get-OldRecycleItems([int]$days, $drives = $null) {
  $old = (Get-Date).AddDays(-$days)
  if (-not $drives) { $drives = @(Get-CimInstance Win32_LogicalDisk -Filter "DriveType=2 OR DriveType=3" -ErrorAction Ignore | ForEach-Object DeviceID) }
  foreach ($d in $drives) {
    $bin = "$d\`$Recycle.Bin\$MySid"
    foreach ($i in @(Get-ChildItem -LiteralPath $bin -Force -Filter '$I*' -ErrorAction Ignore)) {
      if ($i.LastWriteTime -gt $old) { continue }
      $r = Join-Path $bin ('$R' + $i.Name.Substring(2)); $len = [long]0
      if (Test-Path -LiteralPath $r) {
        $ri = Get-Item -LiteralPath $r -Force
        $len = if ($ri.PSIsContainer) { [long](Get-ChildItem -LiteralPath $r -Recurse -Force -File -ErrorAction Ignore | Measure-Object Length -Sum).Sum } else { $ri.Length }
      }
      [pscustomobject]@{ Name = "Recycle Bin item ($d)"; FullName = $r; DirectoryName = $bin; Length = $len; Info = $i.FullName; Recycle = $true }
    }
  }
}

# ---------------------------------------------------------------- deleting
# Delete one file. Read-only files and very long paths are handled; anything else is returned as the error.
function Remove-CleanFile([string]$p) {
  for ($try = 0; $try -lt 2; $try++) {
    try { [IO.File]::Delete($p); return $null }
    catch {
      $e = $_.Exception; while ($e -is [Management.Automation.MethodInvocationException] -and $e.InnerException) { $e = $e.InnerException }
      if ($try -eq 0 -and $e -is [UnauthorizedAccessException]) {
        try { $a = [IO.File]::GetAttributes($p); if ($a -band [IO.FileAttributes]::ReadOnly) { [IO.File]::SetAttributes($p, ($a -band -bnot [IO.FileAttributes]::ReadOnly)); continue } } catch {}
      }
      if ($try -eq 0 -and $e -is [IO.PathTooLongException] -and -not $p.StartsWith('\\?\')) { $p = '\\?\' + $p; continue }
      return $e
    }
  }
}
# Remove one Recycle Bin item (data + info file)
function Remove-RecycleItem($item) {
  try {
    if (Test-Path -LiteralPath $item.FullName) { Remove-Item -LiteralPath $item.FullName -Recurse -Force -ErrorAction Stop }
    Remove-Item -LiteralPath $item.Info -Force -ErrorAction Stop; return $null
  } catch { $e = $_.Exception; return $e }
}
# Delete any of the above (file or Recycle Bin item)
function Remove-CleanItem($f) { if ($f.PSObject.Properties['Recycle']) { Remove-RecycleItem $f } else { Remove-CleanFile $f.FullName } }

# Remove folders left empty inside a junk folder (never the folder itself, never links)
function Remove-EmptyDirs([string]$root, [datetime]$cut) {
  if (-not (Test-Path -LiteralPath $root -PathType Container)) { return }
  $dirs = New-Object System.Collections.Generic.List[string]; $stack = New-Object System.Collections.Stack; $stack.Push($root)
  while ($stack.Count) { $d = $stack.Pop(); try { foreach ($s in ([IO.DirectoryInfo]$d).GetDirectories()) { if (-not ($s.Attributes -band [IO.FileAttributes]::ReparsePoint)) { $dirs.Add($s.FullName); $stack.Push($s.FullName) } } } catch {} }
  foreach ($d in ($dirs | Sort-Object Length -Descending)) {
    try { $di = [IO.DirectoryInfo]$d; if ($di.LastWriteTime -lt $cut -and -not $di.EnumerateFileSystemInfos().GetEnumerator().MoveNext()) { $di.Delete() } } catch {}
  }
}
