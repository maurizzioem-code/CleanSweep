# CleanSweep - a simple disk, registry and shortcut cleaner for Windows 11
Add-Type -AssemblyName System.Windows.Forms, System.Drawing, Microsoft.VisualBasic
[System.Windows.Forms.Application]::EnableVisualStyles()
$Version = "2.7"
# Show any startup error instead of failing silently
trap { [void][System.Windows.Forms.MessageBox]::Show("CleanSweep hit an error:`n`n$_`n`nLine: $($_.InvocationInfo.ScriptLineNumber)","CleanSweep","OK","Error"); break }

# ---------------------------------------------------------------- helpers
function Fmt($b) { if ($b -ge 1GB) { "{0:N2} GB" -f ($b/1GB) } elseif ($b -ge 1MB) { "{0:N1} MB" -f ($b/1MB) } else { "{0:N0} KB" -f ($b/1KB) } }

# Pull a file path out of a command line like: "C:\App\app.exe" /arg  or  C:\App\app.exe -x
function Get-CmdPath([string]$cmd) {
  if ([string]::IsNullOrWhiteSpace($cmd)) { return $null }
  $c = [Environment]::ExpandEnvironmentVariables($cmd.Trim())
  if ($c.StartsWith('"')) { $e = $c.IndexOf('"', 1); if ($e -gt 1) { return $c.Substring(1, $e - 1) } ; return $null }
  if ($c -match '^(.+?\.(exe|dll|com|bat|cmd|vbs|js|ps1|scr|cpl|msc|ico|lnk))(\s|,|$)') { return $matches[1] }
  return $c
}
# True only when the path is a real local path that definitely no longer exists
function Test-Missing([string]$p) {
  Check-Cancel
  if ([string]::IsNullOrWhiteSpace($p)) { return $false }
  if ($p -notmatch '^[A-Za-z]:\\') { return $false }          # skip relative, network, shell: paths
  if ($p -match '\\WindowsApps\\') { return $false }          # Store apps are access-restricted
  if (-not (Test-Path ($p.Substring(0,3)))) { return $false } # skip unplugged drives
  return -not (Test-Path -LiteralPath $p)
}

$form = New-Object Windows.Forms.Form -Property @{Text="CleanSweep $Version - PC Cleaner and Network Optimizer"; Size='820,620'; StartPosition='CenterScreen'; Font=New-Object Drawing.Font("Segoe UI",10); MinimumSize='700,500'}
$ico = Join-Path $PSScriptRoot "CleanSweep.ico"; if (Test-Path $ico) { $form.Icon = New-Object Drawing.Icon($ico) }
$tabs = New-Object Windows.Forms.TabControl -Property @{Dock='Fill'}
$form.Controls.Add($tabs)

# Layout uses docking (not fixed positions) so buttons stay visible at any window size or display scaling
$script:Cancel = $false
function Check-Cancel { [Windows.Forms.Application]::DoEvents(); if ($script:Cancel) { throw "CANCELLED" } }

function New-Tab($title, $cols, $hint) {
  $t = @{}
  $t.Page   = New-Object Windows.Forms.TabPage -Property @{Text=$title; Padding='10,10,10,10'}
  $t.Status = New-Object Windows.Forms.Label -Property @{Dock='Top'; Height=36; Text=$hint; TextAlign='MiddleLeft'; Font=New-Object Drawing.Font("Segoe UI",12,[Drawing.FontStyle]::Bold)}
  $t.List   = New-Object Windows.Forms.ListView -Property @{View='Details'; CheckBoxes=$true; FullRowSelect=$true; Dock='Fill'}
  foreach ($c in $cols) { [void]$t.List.Columns.Add($c[0], $c[1]) }
  $t.Extra  = New-Object Windows.Forms.FlowLayoutPanel -Property @{Dock='Bottom'; AutoSize=$true; AutoSizeMode='GrowAndShrink'; WrapContents=$true; Padding='0,6,0,0'}
  $t.Bar    = New-Object Windows.Forms.FlowLayoutPanel -Property @{Dock='Bottom'; AutoSize=$true; AutoSizeMode='GrowAndShrink'; WrapContents=$true; Padding='0,6,0,0'}
  $b = @{AutoSize=$true; MinimumSize='110,36'; Margin='0,0,8,0'}
  $t.Scan   = New-Object Windows.Forms.Button -Property ($b + @{Text="Scan"})
  $t.Clean  = New-Object Windows.Forms.Button -Property ($b + @{Text="Clean"; Enabled=$false})
  $t.Stop   = New-Object Windows.Forms.Button -Property ($b + @{Text="Cancel"; Enabled=$false})
  $t.All    = New-Object Windows.Forms.Button -Property ($b + @{Text="Select all"})
  $t.None   = New-Object Windows.Forms.Button -Property ($b + @{Text="Select none"})
  $t.Bar.Controls.AddRange(@($t.Scan, $t.Clean, $t.Stop, $t.All, $t.None))
  $lv = $t.List
  $t.All.Add_Click({ foreach ($i in $lv.Items) { $i.Checked = $true } }.GetNewClosure())
  $t.None.Add_Click({ foreach ($i in $lv.Items) { $i.Checked = $false } }.GetNewClosure())
  $t.Stop.Add_Click({ $script:Cancel = $true })
  # Docking order: Fill must be added first so the bars and title keep their space
  $t.Page.Controls.Add($t.List); $t.Page.Controls.Add($t.Extra); $t.Page.Controls.Add($t.Bar); $t.Page.Controls.Add($t.Status)
  $tabs.TabPages.Add($t.Page)
  return $t
}
# Switch a tab between busy (scan/clean running) and idle
function Set-Busy($t, [bool]$busy) {
  $script:Cancel = $false
  $t.Scan.Enabled = -not $busy; $t.All.Enabled = -not $busy; $t.None.Enabled = -not $busy
  $t.Stop.Enabled = $busy
  if ($busy) { $t.Clean.Enabled = $false }
  $form.Cursor = if ($busy) { 'WaitCursor' } else { 'Default' }
}

# ================================================================ 1. JUNK FILES
$L = $env:LOCALAPPDATA; $W = $env:WINDIR
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
  "Recycle Bin"                 = @("RECYCLE")
}
function Get-Items($paths) {
  foreach ($p in $paths) {
    if ($p -eq "RECYCLE") { continue }
    Get-Item -Path $p -Force -ErrorAction SilentlyContinue | ForEach-Object {
      if ($_.PSIsContainer) { Get-ChildItem $_.FullName -Recurse -Force -File -ErrorAction SilentlyContinue } else { $_ }
    }
  }
}
function Get-RecycleSize { $s = 0; try { (New-Object -ComObject Shell.Application).NameSpace(10).Items() | ForEach-Object { $s += $_.Size } } catch {}; $s }

$junk = New-Tab "Junk Files" @(,@("Category",480)) "Click Scan to find junk files."
[void]$junk.List.Columns.Add("Size",200)
foreach ($k in $targets.Keys) { $i = $junk.List.Items.Add($k); [void]$i.SubItems.Add("-"); $i.Checked = $true }
$disk = New-Object Windows.Forms.Label -Property @{AutoSize=$true; Margin='12,9,0,0'}
$junk.Bar.Controls.Add($disk)
function Upd-Disk { $d = Get-PSDrive C; $disk.Text = "C: free " + (Fmt $d.Free) + " of " + (Fmt ($d.Free+$d.Used)) }
Upd-Disk

$junk.Scan.Add_Click({
  Set-Busy $junk $true; $total = 0
  try {
    foreach ($i in $junk.List.Items) {
      $junk.Status.Text = "Scanning $($i.Text)..."; Check-Cancel
      $p = $targets[$i.Text]
      $sz = if ($p -contains "RECYCLE") { Get-RecycleSize } else { (Get-Items $p | ForEach-Object { Check-Cancel; $_ } | Measure-Object Length -Sum).Sum }
      if (-not $sz) { $sz = 0 }; $i.Tag = $sz; $i.SubItems[1].Text = Fmt $sz; $total += $sz
    }
    $junk.Status.Text = "Found " + (Fmt $total) + " of junk."; Set-Busy $junk $false; $junk.Clean.Enabled = $true
  } catch { Set-Busy $junk $false; $junk.Status.Text = "Scan cancelled." }
})
$junk.Clean.Add_Click({
  if ([Windows.Forms.MessageBox]::Show("Delete the selected junk files?","Confirm","YesNo","Warning") -ne "Yes") { return }
  Set-Busy $junk $true; $freed = 0
  try {
    foreach ($i in $junk.List.Items) {
      if (-not $i.Checked) { continue }
      $junk.Status.Text = "Cleaning $($i.Text)..."; Check-Cancel
      $p = $targets[$i.Text]
      if ($p -contains "RECYCLE") { Clear-RecycleBin -Force -ErrorAction SilentlyContinue; $freed += [long]$i.Tag }
      else { Get-Items $p | ForEach-Object { Check-Cancel; $len = $_.Length; try { Remove-Item -LiteralPath $_.FullName -Force -ErrorAction Stop; $freed += $len } catch {} } }
      $i.SubItems[1].Text = "Cleaned"
    }
    $junk.Status.Text = "Done. Freed " + (Fmt $freed) + " (files in use were skipped)."
  } catch { $junk.Status.Text = "Cleaning stopped. Freed " + (Fmt $freed) + " before cancelling." }
  Set-Busy $junk $false; Upd-Disk
})

# ================================================================ 2. REGISTRY
$HKLM = "HKEY_LOCAL_MACHINE"; $HKCU = "HKEY_CURRENT_USER"
$backupDir = Join-Path ([Environment]::GetFolderPath('MyDocuments')) "CleanSweep Backups"

function Add-RegIssue($issue, $key, $value, $target) {
  $i = $reg.List.Items.Add($issue)
  [void]$i.SubItems.Add($(if ($value) { "$key  [$value]" } else { $key }))
  [void]$i.SubItems.Add($target)
  $i.Tag = [pscustomobject]@{ Key=$key; Value=$value }
  $i.Checked = $true
}

function Scan-Registry {
  # Startup entries pointing to missing programs
  foreach ($k in "$HKCU\Software\Microsoft\Windows\CurrentVersion\Run", "$HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run", "$HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run") {
    $item = Get-Item -LiteralPath "Registry::$k" -ErrorAction SilentlyContinue; if (-not $item) { continue }
    foreach ($n in $item.GetValueNames()) { if (-not $n) { continue }
      $p = Get-CmdPath ($item.GetValue($n)); if (Test-Missing $p) { Add-RegIssue "Startup entry" $k $n $p } }
  }
  # App Paths pointing to missing programs
  foreach ($k in "$HKCU\Software\Microsoft\Windows\CurrentVersion\App Paths", "$HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths", "$HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\App Paths") {
    Get-ChildItem -LiteralPath "Registry::$k" -ErrorAction SilentlyContinue | ForEach-Object {
      $p = Get-CmdPath ($_.GetValue("")); if (Test-Missing $p) { Add-RegIssue "Application path" ($_.Name) $null $p } }
  }
  # Uninstall entries for programs that are gone
  foreach ($k in "$HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall", "$HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", "$HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall") {
    Get-ChildItem -LiteralPath "Registry::$k" -ErrorAction SilentlyContinue | ForEach-Object {
      if ($_.GetValue("SystemComponent") -eq 1) { return }
      $u = Get-CmdPath ($_.GetValue("UninstallString")); $loc = $_.GetValue("InstallLocation")
      if ((Test-Missing $u) -and ([string]::IsNullOrWhiteSpace($loc) -or (Test-Missing $loc.Trim('"').TrimEnd('\')))) {
        $name = $_.GetValue("DisplayName"); if (-not $name) { $name = $_.PSChildName }
        Add-RegIssue "Leftover uninstall entry: $name" ($_.Name) $null $u }
    }
  }
  # Shared DLL references to missing files
  foreach ($k in "$HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\SharedDLLs", "$HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\SharedDLLs") {
    $item = Get-Item -LiteralPath "Registry::$k" -ErrorAction SilentlyContinue; if (-not $item) { continue }
    foreach ($n in $item.GetValueNames()) { if (Test-Missing $n) { Add-RegIssue "Missing shared DLL" $k $n $n } }
  }
  # MUI cache entries for programs that no longer exist
  $k = "$HKCU\Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\MuiCache"
  $item = Get-Item -LiteralPath "Registry::$k" -ErrorAction SilentlyContinue
  if ($item) { foreach ($n in $item.GetValueNames()) {
    if ($n -match '^(.+?\.(exe|dll|com|bat|cmd|msc|cpl))\.[A-Za-z]+$') { if (Test-Missing $matches[1]) { Add-RegIssue "Obsolete program cache" $k $n $matches[1] } } } }
}

function Backup-Registry($keys) {
  if (-not (Test-Path $backupDir)) { [void](New-Item -ItemType Directory $backupDir) }
  $out = Join-Path $backupDir ("Registry backup " + (Get-Date -Format "yyyy-MM-dd HH-mm-ss") + ".reg")
  $lines = New-Object System.Collections.Generic.List[string]; $lines.Add("Windows Registry Editor Version 5.00")
  foreach ($k in $keys) {
    $tmp = [IO.Path]::GetTempFileName()
    & reg.exe export $k $tmp /y 2>$null | Out-Null
    if ($LASTEXITCODE -eq 0) { Get-Content -LiteralPath $tmp -Encoding Unicode | Select-Object -Skip 1 | ForEach-Object { $lines.Add($_) } }
    Remove-Item $tmp -ErrorAction SilentlyContinue
  }
  Set-Content -LiteralPath $out -Value $lines -Encoding Unicode
  return $out
}

# Create a System Restore point. Returns $true only if a new point really exists afterwards.
function New-RestorePoint([string]$desc = "CleanSweep - before registry cleaning") {
  $srKey = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore'
  $old = (Get-ItemProperty $srKey -Name SystemRestorePointCreationFrequency -ErrorAction SilentlyContinue).SystemRestorePointCreationFrequency
  $last = (Get-ComputerRestorePoint -ErrorAction SilentlyContinue | Measure-Object SequenceNumber -Maximum).Maximum
  try {
    # Windows normally allows only one restore point per 24 hours; lift that limit just for this call
    Set-ItemProperty $srKey -Name SystemRestorePointCreationFrequency -Value 0 -Type DWord -ErrorAction SilentlyContinue
    Checkpoint-Computer -Description $desc -RestorePointType MODIFY_SETTINGS -ErrorAction Stop -WarningAction SilentlyContinue
  } catch { } finally {
    if ($null -eq $old) { Remove-ItemProperty $srKey -Name SystemRestorePointCreationFrequency -ErrorAction SilentlyContinue }
    else { Set-ItemProperty $srKey -Name SystemRestorePointCreationFrequency -Value $old -Type DWord -ErrorAction SilentlyContinue }
  }
  $now = (Get-ComputerRestorePoint -ErrorAction SilentlyContinue | Measure-Object SequenceNumber -Maximum).Maximum
  return ($now -and ($now -ne $last))
}

$reg = New-Tab "Registry" @(@("Issue",260), @("Registry location",330), @("Missing file",300)) "Click Scan to find invalid registry entries."
$restore = New-Object Windows.Forms.Button -Property @{Text="Restore a backup..."; AutoSize=$true; MinimumSize='150,36'; Margin='0,0,8,0'}
$reg.Bar.Controls.Add($restore)
$mkRp  = New-Object Windows.Forms.Button -Property @{Text="Create restore point"; AutoSize=$true; MinimumSize='170,36'; Margin='0,0,8,0'}
$openRp = New-Object Windows.Forms.Button -Property @{Text="Open System Restore"; AutoSize=$true; MinimumSize='170,36'; Margin='0,0,8,0'}
$reg.Extra.Controls.AddRange(@($mkRp, $openRp))
$mkRp.Add_Click({
  $form.Cursor='WaitCursor'; $mkRp.Enabled = $false; $reg.Scan.Enabled = $false; $reg.Clean.Enabled = $false
  $reg.Status.Text = "Creating a restore point (this can take a minute)..."; $form.Refresh()
  $ok = New-RestorePoint "CleanSweep - manual restore point"
  if (-not $ok) {
    $form.Cursor='Default'
    if ([Windows.Forms.MessageBox]::Show("Could not create a restore point. System Protection may be turned off for drive $env:SystemDrive.`n`nTurn on System Protection and try again?","Restore point","YesNo","Question") -eq "Yes") {
      $form.Cursor='WaitCursor'; $reg.Status.Text = "Turning on System Protection and creating a restore point..."; $form.Refresh()
      try { Enable-ComputerRestore -Drive "$env:SystemDrive\" -ErrorAction Stop } catch {}
      $ok = New-RestorePoint "CleanSweep - manual restore point"
    }
  }
  $form.Cursor='Default'; $mkRp.Enabled = $true; $reg.Scan.Enabled = $true; $reg.Clean.Enabled = ($reg.List.Items.Count -gt 0)
  if ($ok) {
    $reg.Status.Text = "Restore point created."
    [void][Windows.Forms.MessageBox]::Show("A System Restore point named 'CleanSweep - manual restore point' was created on " + (Get-Date).ToString("MMM d, yyyy h:mm tt") + ".`n`nTo use it later, click 'Open System Restore'.","CleanSweep","OK","Information")
  } else {
    $reg.Status.Text = "No restore point was created."
    [void][Windows.Forms.MessageBox]::Show("Windows could not create a restore point.","CleanSweep","OK","Warning")
  }
})
$openRp.Add_Click({ Start-Process "$env:SystemRoot\System32\rstrui.exe" })
$rpBox = New-Object Windows.Forms.CheckBox -Property @{Text="Create a System Restore point before cleaning (recommended)"; AutoSize=$true; Checked=$true}
$reg.Extra.Controls.Add($rpBox); $reg.Extra.Controls.SetChildIndex($rpBox, 0); $reg.Extra.SetFlowBreak($rpBox, $true)  # checkbox on its own line, buttons below

$reg.Scan.Add_Click({
  Set-Busy $reg $true; $reg.List.Items.Clear(); $reg.Status.Text = "Scanning the registry..."
  try { Scan-Registry; $n = $reg.List.Items.Count
    $reg.Status.Text = if ($n) { "Found $n invalid registry entries." } else { "No registry issues found." }
  } catch { $reg.Status.Text = "Scan cancelled. Showing what was found so far." }
  Set-Busy $reg $false; $reg.Clean.Enabled = ($reg.List.Items.Count -gt 0)
})
$reg.Clean.Add_Click({
  $sel = @($reg.List.Items | Where-Object Checked); if (-not $sel) { return }
  if ([Windows.Forms.MessageBox]::Show("Remove $($sel.Count) registry entries?`n`nA backup will be saved to:`n$backupDir","Confirm","YesNo","Warning") -ne "Yes") { return }
  $form.Cursor='WaitCursor'; $rp = $false; $reg.Scan.Enabled = $false
  if ($rpBox.Checked) {
    $reg.Status.Text = "Creating a restore point (this can take a minute)..."; $form.Refresh()
    $rp = New-RestorePoint
    if (-not $rp) {
      $form.Cursor='Default'
      $ans = [Windows.Forms.MessageBox]::Show("Could not create a restore point. System Protection may be turned off for drive $env:SystemDrive.`n`nTurn on System Protection and try again?","Restore point","YesNoCancel","Question")
      if ($ans -eq "Cancel") { $reg.Status.Text = "Cleaning cancelled."; $reg.Scan.Enabled = $true; return }
      $form.Cursor='WaitCursor'
      if ($ans -eq "Yes") {
        $reg.Status.Text = "Turning on System Protection and creating a restore point..."; $form.Refresh()
        try { Enable-ComputerRestore -Drive "$env:SystemDrive\" -ErrorAction Stop } catch {}
        $rp = New-RestorePoint
      }
      if (-not $rp) {
        $form.Cursor='Default'
        if ([Windows.Forms.MessageBox]::Show("No restore point was created.`n`nContinue anyway? A registry backup file will still be saved.","Restore point","YesNo","Warning") -ne "Yes") { $reg.Status.Text = "Cleaning cancelled."; $reg.Scan.Enabled = $true; $form.Cursor='Default'; return }
        $form.Cursor='WaitCursor'
      }
    }
  }
  $reg.Status.Text = "Backing up..."; $form.Refresh()
  $bk = Backup-Registry ($sel | ForEach-Object { $_.Tag.Key } | Select-Object -Unique)
  $ok = 0
  foreach ($i in $sel) {
    try {
      if ($i.Tag.Value) { Remove-ItemProperty -LiteralPath "Registry::$($i.Tag.Key)" -Name $i.Tag.Value -Force -ErrorAction Stop }
      else { Remove-Item -LiteralPath "Registry::$($i.Tag.Key)" -Recurse -Force -ErrorAction Stop }
      $reg.List.Items.Remove($i); $ok++
    } catch { $i.SubItems[2].Text = "Could not remove: access denied" }
  }
  $reg.Status.Text = "Removed $ok entries. Backup saved."; $reg.Scan.Enabled = $true; $reg.Clean.Enabled = ($reg.List.Items.Count -gt 0); $form.Cursor='Default'
  $rpMsg = if ($rp) { "`n`nA System Restore point named 'CleanSweep - before registry cleaning' was created." } else { "" }
  [void][Windows.Forms.MessageBox]::Show("Removed $ok registry entries.`n`nBackup file:`n$bk$rpMsg","CleanSweep","OK","Information")
})
$restore.Add_Click({
  $dlg = New-Object Windows.Forms.OpenFileDialog -Property @{Filter="Registry backups (*.reg)|*.reg"; InitialDirectory=$backupDir}
  if ($dlg.ShowDialog() -ne "OK") { return }
  & reg.exe import $dlg.FileName 2>$null | Out-Null
  $msg = if ($LASTEXITCODE -eq 0) { "Backup restored." } else { "Some entries could not be restored." }
  [void][Windows.Forms.MessageBox]::Show($msg,"CleanSweep","OK","Information")
})

# ================================================================ 3. BROKEN SHORTCUTS
$shortcutDirs = @(
  [Environment]::GetFolderPath('Desktop'),
  [Environment]::GetFolderPath('CommonDesktopDirectory'),
  [Environment]::GetFolderPath('StartMenu'),
  [Environment]::GetFolderPath('CommonStartMenu'),
  "$env:APPDATA\Microsoft\Internet Explorer\Quick Launch",
  "$env:APPDATA\Microsoft\Windows\SendTo"
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -Unique

$sc = New-Tab "Broken Shortcuts" @(@("Shortcut",240), @("Location",360), @("Missing target",290)) "Click Scan to find shortcuts that point to missing files."
$wsh = New-Object -ComObject WScript.Shell

$sc.Scan.Add_Click({
  Set-Busy $sc $true; $sc.List.Items.Clear()
  try {
    foreach ($d in $shortcutDirs) {
      $sc.Status.Text = "Scanning $d..."; Check-Cancel
      Get-ChildItem -LiteralPath $d -Filter *.lnk -Recurse -Force -File -ErrorAction SilentlyContinue | ForEach-Object {
        try { $t = [Environment]::ExpandEnvironmentVariables($wsh.CreateShortcut($_.FullName).TargetPath) } catch { return }
        if (Test-Missing $t) {
          $i = $sc.List.Items.Add($_.BaseName); [void]$i.SubItems.Add($_.DirectoryName); [void]$i.SubItems.Add($t)
          $i.Tag = $_.FullName; $i.Checked = $true }
      }
    }
    $n = $sc.List.Items.Count
    $sc.Status.Text = if ($n) { "Found $n broken shortcuts." } else { "No broken shortcuts found." }
  } catch { $sc.Status.Text = "Scan cancelled. Showing what was found so far." }
  Set-Busy $sc $false; $sc.Clean.Enabled = ($sc.List.Items.Count -gt 0)
})
$sc.Clean.Add_Click({
  $sel = @($sc.List.Items | Where-Object Checked); if (-not $sel) { return }
  if ([Windows.Forms.MessageBox]::Show("Move $($sel.Count) broken shortcuts to the Recycle Bin?","Confirm","YesNo","Warning") -ne "Yes") { return }
  $form.Cursor='WaitCursor'; $ok = 0
  foreach ($i in $sel) {
    try { [Microsoft.VisualBasic.FileIO.FileSystem]::DeleteFile($i.Tag, 'OnlyErrorDialogs', 'SendToRecycleBin'); $sc.List.Items.Remove($i); $ok++ }
    catch { $i.SubItems[2].Text = "Could not remove" }
  }
  $sc.Status.Text = "Moved $ok broken shortcuts to the Recycle Bin."; $sc.Clean.Enabled = ($sc.List.Items.Count -gt 0); $form.Cursor='Default'
})

# ================================================================ 4. NETWORK OPTIMIZER (Wi-Fi and Ethernet)
$script:NetKind = "Wi-Fi"
function Get-EthAdapter {
  Get-NetAdapter -Physical -ErrorAction SilentlyContinue |
    Where-Object { $_.NdisPhysicalMedium -eq 14 -and $_.InterfaceDescription -notmatch 'Wi-?Fi|Wireless|802\.11|WLAN|Bluetooth|Virtual|VPN|TAP' } |
    Sort-Object @{e={$_.Status -eq 'Up'}; Descending=$true} | Select-Object -First 1
}
# The adapter chosen in the Connection box
function Get-NetTarget { if ($script:NetKind -eq "Ethernet") { Get-EthAdapter } else { Get-WifiAdapter } }
function Get-WifiAdapter {
  Get-NetAdapter -Physical -ErrorAction SilentlyContinue | Where-Object { $_.NdisPhysicalMedium -eq 9 -or $_.InterfaceDescription -match 'Wi-?Fi|Wireless|802\.11|WLAN' } |
    Sort-Object @{e={$_.Status -eq 'Up'}; Descending=$true} | Select-Object -First 1
}
# Reads "Name : Value" lines from netsh wlan show interfaces
$LocationHelp = "Windows needs Location turned on to show Wi-Fi details.`nOpen Settings > Privacy & security > Location, turn on Location services and 'Let desktop apps access your location'."
function Get-WifiInfo {
  $info = @{}
  $out = netsh wlan show interfaces 2>$null
  if (($out -join " ") -match 'location permission|location services') { $info['_NeedsLocation'] = $true }
  foreach ($line in $out) {
    if ($line -match '^\s*([^:]+?)\s*:\s*(.+)$') { $k = $matches[1].Trim(); if (-not $info.ContainsKey($k)) { $info[$k] = $matches[2].Trim() } }
  }
  $info
}
function Update-WifiInfo {
  $a = Get-NetTarget
  if ($script:NetKind -eq "Ethernet") {
    if (-not $a) { $wifiInfo.Text = "No Ethernet port was found on this PC."; return }
    if ($a.Status -ne 'Up') { $wifiInfo.Text = "Ethernet adapter: $($a.InterfaceDescription)`nNo cable connected (or the cable/router port isn't working)."; return }
    $dns = (Get-DnsClientServerAddress -InterfaceIndex $a.ifIndex -AddressFamily IPv4 -ErrorAction SilentlyContinue).ServerAddresses -join ", "
    $bps = [double]$a.ReceiveLinkSpeed; $mbps = [int]($bps / 1e6)
    $hint = if ($mbps -gt 0 -and $mbps -le 100) { "  - Only 100 Mbps: the cable or router port may be limiting you. Try a Cat5e/Cat6 cable or another port." } else { "" }
    $duplex = if ($a.FullDuplex) { "Full duplex" } else { "Half duplex (slow - check the cable)" }
    $wifiInfo.Text = "Ethernet adapter: $($a.InterfaceDescription)`n" +
      "Link speed: $($a.LinkSpeed)     $duplex$hint`n" +
      "Adapter name: $($a.Name)     DNS: $dns"
    return
  }
  if (-not $a) { $wifiInfo.Text = "No Wi-Fi adapter was found on this PC."; return }
  $i = Get-WifiInfo
  if ($i['_NeedsLocation']) { $wifiInfo.Text = $LocationHelp; return }
  $dns = (Get-DnsClientServerAddress -InterfaceIndex $a.ifIndex -AddressFamily IPv4 -ErrorAction SilentlyContinue).ServerAddresses -join ", "
  if ($a.Status -ne 'Up' -or -not $i['SSID']) { $wifiInfo.Text = "Wi-Fi adapter: $($a.InterfaceDescription)`nNot connected to a Wi-Fi network."; return }
  $sig = $i['Signal']; $pct = 0; [void][int]::TryParse(($sig -replace '[^\d]'), [ref]$pct)
  $quality = if ($pct -ge 80) { "Excellent" } elseif ($pct -ge 60) { "Good" } elseif ($pct -ge 40) { "Fair - moving closer to the router will help" } else { "Weak - move closer to the router" }
  $band = if ($i['Band']) { $i['Band'] } else { "-" }
  $wifiInfo.Text = "Network: $($i['SSID'])     Signal: $sig ($quality)`n" +
    "Band: $band     Channel: $($i['Channel'])     Type: $($i['Radio type'])`n" +
    "Speed: $($i['Receive rate (Mbps)']) Mbps down / $($i['Transmit rate (Mbps)']) Mbps up (link speed)     DNS: $dns"
}
# Latency, jitter, packet loss and DNS lookup time
function Test-Net {
  $times = @(); $lost = 0
  foreach ($n in 1..10) {
    Check-Cancel
    $r = Test-Connection -ComputerName 1.1.1.1 -Count 1 -ErrorAction SilentlyContinue
    if ($r) { $times += [int]$r.ResponseTime } else { $lost++ }
  }
  $dnsMs = $null
  try { Clear-DnsClientCache; $dnsMs = [int](Measure-Command { Resolve-DnsName ("www.microsoft.com") -DnsOnly -ErrorAction Stop | Out-Null }).TotalMilliseconds } catch {}
  $avg = if ($times) { [int](($times | Measure-Object -Average).Average) } else { $null }
  $jit = if ($times.Count -gt 1) { [int](((1..($times.Count-1)) | ForEach-Object { [math]::Abs($times[$_] - $times[$_-1]) } | Measure-Object -Average).Average) } else { 0 }
  [pscustomobject]@{ Ping=$avg; Jitter=$jit; Loss=[int]($lost*10); Dns=$dnsMs }
}
function Fmt-Net($t) {
  if ($null -eq $t.Ping) { return "No internet response (100% packet loss)." }
  "Ping $($t.Ping) ms, jitter $($t.Jitter) ms, packet loss $($t.Loss)%, DNS lookup " + $(if ($null -ne $t.Dns) { "$($t.Dns) ms" } else { "failed" })
}

$wifi = New-Tab "Network Optimizer" @(@("Optimization",300), @("What it does",560)) "Click Test connection to measure your connection, then Optimize."
$wifi.Scan.Text = "Test connection"; $wifi.Clean.Text = "Optimize"; $wifi.Clean.Enabled = $true
$wifiInfo = New-Object Windows.Forms.Label -Property @{Dock='Top'; Height=70; Padding='0,4,0,4'; Text="Reading Wi-Fi details..."}
$wifi.Page.Controls.Add($wifiInfo); $wifi.Page.Controls.SetChildIndex($wifiInfo, 3)   # title, then Wi-Fi details, then list

$dnsLabel = New-Object Windows.Forms.Label -Property @{Text="DNS server:"; AutoSize=$true; Margin='8,9,4,0'}
$dnsBox = New-Object Windows.Forms.ComboBox -Property @{DropDownStyle='DropDownList'; Width=230; Margin='0,5,8,0'}
[void]$dnsBox.Items.AddRange(@("Keep current", "Cloudflare (1.1.1.1) - fastest", "Google (8.8.8.8)", "Automatic (from router)"))
$dnsBox.SelectedIndex = 0
$nearBtn = New-Object Windows.Forms.Button -Property @{Text="Nearby networks"; AutoSize=$true; MinimumSize='150,36'; Margin='0,0,8,0'}
$connLabel = New-Object Windows.Forms.Label -Property @{Text="Connection:"; AutoSize=$true; Margin='0,9,4,0'}
$connBox = New-Object Windows.Forms.ComboBox -Property @{DropDownStyle='DropDownList'; Width=120; Margin='0,5,8,0'}
[void]$connBox.Items.AddRange(@("Wi-Fi", "Ethernet"))
$wifi.Extra.Controls.AddRange(@($connLabel, $connBox, $dnsLabel, $dnsBox, $nearBtn))
# Start on whichever connection Windows is actually using for the internet
try {
  $route = Get-NetRoute -DestinationPrefix 0.0.0.0/0 -ErrorAction Stop | Sort-Object { $_.RouteMetric + (Get-NetIPInterface -InterfaceIndex $_.ifIndex -AddressFamily IPv4 -ErrorAction SilentlyContinue).InterfaceMetric } | Select-Object -First 1
  $eth = Get-EthAdapter
  if ($route -and $eth -and $route.ifIndex -eq $eth.ifIndex) { $script:NetKind = "Ethernet" }
} catch {}
$connBox.SelectedItem = $script:NetKind
$connBox.Add_SelectedIndexChanged({
  $script:NetKind = $connBox.SelectedItem; $script:NetBefore = $null
Load-NetOpts
  $nearBtn.Enabled = ($script:NetKind -eq "Wi-Fi")
  $wifi.Status.Text = "Click Test connection to measure your $($script:NetKind), then Optimize."
  Load-NetOpts; Update-WifiInfo
})
$nearBtn.Enabled = ($script:NetKind -eq "Wi-Fi")

$commonOpts = [ordered]@{
  "Flush DNS cache"                 = @("Clears stored website addresses so stale entries can't slow or break loading.", $true)
  "Clear ARP cache"                 = @("Clears the local device address table, which fixes some router connection glitches.", $true)
  "Renew IP address"                = @("Asks your router for a fresh IP address. The connection drops for a few seconds.", $true)
}
$wifiOnly = [ordered]@{ "Wi-Fi power: max performance" = @("Stops Windows throttling the Wi-Fi card to save power when plugged in (light saving on battery).", $true) }
$ethOnly  = [ordered]@{ "Ethernet power saving off" = @("Turns off Energy Efficient / Green Ethernet, which can add lag. The cable reconnects briefly.", $true) }
$tailOpts = [ordered]@{
  "Reset TCP auto-tuning to normal"  = @("Restores Windows' default download window scaling if another tool changed it.", $true)
  "Reset network stack (Winsock/IP)" = @("Deep repair for broken connections. Needs a restart. Only use if your internet is misbehaving.", $false)
}
function Load-NetOpts {
  $wifi.List.BeginUpdate(); $wifi.List.Items.Clear()
  $extra = if ($script:NetKind -eq "Ethernet") { $ethOnly } else { $wifiOnly }
  foreach ($set in $commonOpts, $extra, $tailOpts) { foreach ($k in $set.Keys) { $i = $wifi.List.Items.Add($k); [void]$i.SubItems.Add($set[$k][0]); $i.Checked = $set[$k][1] } }
  $wifi.List.EndUpdate()
}
$script:NetBefore = $null

$wifi.Scan.Add_Click({
  Set-Busy $wifi $true; Update-WifiInfo
  try { $wifi.Status.Text = "Testing connection (about 10 seconds)..."; $t = Test-Net; $script:NetBefore = $t; $wifi.Status.Text = Fmt-Net $t }
  catch { $wifi.Status.Text = "Test cancelled." }
  Set-Busy $wifi $false; $wifi.Clean.Enabled = $true
})

$wifi.Clean.Add_Click({
  $sel = @($wifi.List.Items | Where-Object Checked | ForEach-Object Text)
  if (-not $sel -and $dnsBox.SelectedIndex -eq 0) { return }
  $a = Get-NetTarget
  if (-not $a) { [void][Windows.Forms.MessageBox]::Show("No $($script:NetKind) adapter was found.","CleanSweep","OK","Warning"); return }
  if ([Windows.Forms.MessageBox]::Show("Apply the selected $($script:NetKind) optimizations?`n`nYour connection may drop for a few seconds.","Confirm","YesNo","Question") -ne "Yes") { return }
  Set-Busy $wifi $true; $script:restart = $false; $script:ethChanged = $false
  function Step($name, [scriptblock]$action) { $wifi.Status.Text = "$name..."; [Windows.Forms.Application]::DoEvents(); try { & $action; $script:done += $name } catch {} }
  $script:done = @()
  if ($sel -contains "Flush DNS cache") { Step "Flushing DNS cache" { ipconfig /flushdns | Out-Null; Clear-DnsClientCache } }
  if ($sel -contains "Clear ARP cache") { Step "Clearing ARP cache" { netsh interface ip delete arpcache | Out-Null } }
  if ($sel -contains "Wi-Fi power: max performance") { Step "Setting Wi-Fi power to max performance" {
      $sub = "19cbb8fa-5279-450e-9fac-8a3d5fedd0c1"; $set = "12bbebe6-58d6-4636-95bb-3217ef867c1a"
      powercfg /setacvalueindex SCHEME_CURRENT $sub $set 0 | Out-Null   # plugged in: Maximum Performance
      powercfg /setdcvalueindex SCHEME_CURRENT $sub $set 1 | Out-Null   # battery: Low Power Saving
      powercfg /setactive SCHEME_CURRENT | Out-Null } }
  if ($sel -contains "Ethernet power saving off") { Step "Turning off Ethernet power saving" {
      # Driver setting names differ between Intel, Realtek, Killer etc., so match the common ones
      Get-NetAdapterAdvancedProperty -Name $a.Name -ErrorAction SilentlyContinue |
        Where-Object { $_.DisplayName -match 'Energy.?Efficient|Green Ethernet|Power Saving Mode|Advanced EEE|Gigabit Lite|Ultra Low Power|System Idle Power Saver' } |
        ForEach-Object {
          $off = $_.ValidDisplayValues | Where-Object { $_ -match '^(Disabled|Off)$' } | Select-Object -First 1
          if ($off -and $_.DisplayValue -ne $off) { Set-NetAdapterAdvancedProperty -Name $a.Name -DisplayName $_.DisplayName -DisplayValue $off -NoRestart -ErrorAction SilentlyContinue; $script:ethChanged = $true }
        }
      if ($script:ethChanged) { Restart-NetAdapter -Name $a.Name -Confirm:$false -ErrorAction SilentlyContinue }
    } }
  if ($sel -contains "Reset TCP auto-tuning to normal") { Step "Resetting TCP auto-tuning" { netsh int tcp set global autotuninglevel=normal | Out-Null } }
  switch ($dnsBox.SelectedIndex) {
    1 { Step "Switching DNS to Cloudflare" { Set-DnsClientServerAddress -InterfaceIndex $a.ifIndex -ServerAddresses ("1.1.1.1","1.0.0.1") -ErrorAction Stop } }
    2 { Step "Switching DNS to Google" { Set-DnsClientServerAddress -InterfaceIndex $a.ifIndex -ServerAddresses ("8.8.8.8","8.8.4.4") -ErrorAction Stop } }
    3 { Step "Switching DNS to automatic" { Set-DnsClientServerAddress -InterfaceIndex $a.ifIndex -ResetServerAddresses -ErrorAction Stop } }
  }
  if ($sel -contains "Renew IP address") { Step "Renewing IP address" {
      ipconfig /release "$($a.Name)" | Out-Null; Start-Sleep 1; ipconfig /renew "$($a.Name)" | Out-Null } }
  if ($sel -contains "Reset network stack (Winsock/IP)") { Step "Resetting network stack" { netsh winsock reset | Out-Null; netsh int ip reset | Out-Null; $script:restart = $true } }

  # Wait for Wi-Fi to reconnect, then measure again
  $wifi.Status.Text = "Waiting for $($script:NetKind) to reconnect..."
  foreach ($n in 1..20) { [Windows.Forms.Application]::DoEvents(); if (Test-Connection 1.1.1.1 -Count 1 -Quiet -ErrorAction SilentlyContinue) { break }; Start-Sleep -Milliseconds 700 }
  Update-WifiInfo
  $msg = "Applied:`n - " + ($script:done -join "`n - ")
  try {
    $wifi.Status.Text = "Testing connection again..."; $after = Test-Net; $wifi.Status.Text = "After: " + (Fmt-Net $after)
    if ($script:NetBefore) { $msg += "`n`nBefore: " + (Fmt-Net $script:NetBefore) }
    $msg += "`nAfter:  " + (Fmt-Net $after); $script:NetBefore = $after
  } catch { $wifi.Status.Text = "Optimizations applied." }
  if ($script:restart) { $msg += "`n`nRestart your PC to finish the network stack reset." }
  Set-Busy $wifi $false; $wifi.Clean.Enabled = $true
  [void][Windows.Forms.MessageBox]::Show($msg,"Network Optimizer","OK","Information")
})

# Nearby networks: shows which channels are crowded
$nearBtn.Add_Click({
  $form.Cursor = 'WaitCursor'
  $nets = @(); $ssid = ""; $cur = $null
  $out = netsh wlan show networks mode=bssid 2>$null
  if (($out -join " ") -match 'location permission|location services') { $form.Cursor = 'Default'; [void][Windows.Forms.MessageBox]::Show($LocationHelp,"Nearby networks","OK","Information"); return }
  foreach ($line in $out) {
    if ($line -match '^SSID \d+ : (.*)$') { $ssid = $matches[1].Trim(); if (-not $ssid) { $ssid = "(hidden)" } }
    elseif ($line -match '^\s+BSSID \d+') { $cur = [ordered]@{ SSID=$ssid; Signal=""; Channel=""; Band="" }; $nets += $cur }
    elseif ($cur -and $line -match '^\s+Signal\s*:\s*(.+)$') { $cur.Signal = $matches[1].Trim() }
    elseif ($cur -and $line -match '^\s+Channel\s*:\s*(\d+)') { $cur.Channel = [int]$matches[1] }
    elseif ($cur -and $line -match '^\s+Band\s*:\s*(.+)$') { $cur.Band = $matches[1].Trim() }
  }
  $form.Cursor = 'Default'
  $dlg = New-Object Windows.Forms.Form -Property @{Text="Nearby Wi-Fi networks"; Size='720,480'; StartPosition='CenterParent'; Font=$form.Font; Icon=$form.Icon}
  $lv = New-Object Windows.Forms.ListView -Property @{View='Details'; FullRowSelect=$true; Dock='Fill'}
  foreach ($c in @(@("Network",260),@("Signal",80),@("Channel",80),@("Band",100),@("Networks on this channel",170))) { [void]$lv.Columns.Add($c[0],$c[1]) }
  $counts = $nets | Group-Object Channel -AsHashTable -AsString
  foreach ($n in ($nets | Sort-Object { [int]($_.Signal -replace '[^\d]') } -Descending)) {
    $it = $lv.Items.Add($n.SSID); foreach ($v in $n.Signal, "$($n.Channel)", $n.Band, "$(@($counts["$($n.Channel)"]).Count)") { [void]$it.SubItems.Add($v) } }
  $cur = (Get-WifiInfo)['Channel']
  $tip = if (-not $nets) { "No networks found. Make sure Wi-Fi is turned on." }
    elseif ($cur -and $counts[$cur] -and @($counts[$cur]).Count -ge 4) { "Your channel ($cur) is crowded with $(@($counts[$cur]).Count) networks. If your router supports 5 GHz, connect to that network, or change the router's channel in its settings." }
    elseif ($cur) { "Your channel ($cur) is not heavily crowded." } else { "" }
  $tipL = New-Object Windows.Forms.Label -Property @{Dock='Bottom'; Height=56; Padding='8,6,8,6'; Text=$tip}
  $dlg.Controls.Add($lv); $dlg.Controls.Add($tipL)
  [void]$dlg.ShowDialog($form)
})
$tabs.Add_SelectedIndexChanged({ if ($tabs.SelectedTab -eq $wifi.Page) { Update-WifiInfo } })

# ================================================================ 5. UPDATES
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
$AppDir = Join-Path $env:LOCALAPPDATA "CleanSweep"
$settingsFile = Join-Path $AppDir "settings.json"
# Where CleanSweep looks for new versions (a small JSON file). Can be changed in settings.json.
$DefaultUpdateUrl = "https://raw.githubusercontent.com/maurizzioem-code/CleanSweep/main/version.json"

$settings = @{ AutoCheck = $true; UpdateUrl = $DefaultUpdateUrl; LastCheck = "" }
if (Test-Path $settingsFile) {
  try { (Get-Content -Raw $settingsFile | ConvertFrom-Json).PSObject.Properties | ForEach-Object { $settings[$_.Name] = $_.Value } } catch {}
}
if ($settings.UpdateUrl -match '/OWNER/') { $settings.UpdateUrl = $DefaultUpdateUrl }  # upgrade from 2.2 placeholder
function Save-Settings {
  New-Item -ItemType Directory -Path $AppDir -Force | Out-Null
  [pscustomobject]$settings | ConvertTo-Json | Set-Content -LiteralPath $settingsFile -Encoding UTF8
}
function Say($msg, $icon = "Information") { [void][Windows.Forms.MessageBox]::Show($msg, "CleanSweep Updates", "OK", $icon) }

function Install-Update($m) {
  $upd.Status.Text = "Downloading CleanSweep $($m.version)..."; $form.Cursor = 'WaitCursor'; $form.Refresh()
  $tmp = Join-Path $env:TEMP ("CleanSweepUpdate_" + [guid]::NewGuid().ToString("N"))
  New-Item -ItemType Directory -Path $tmp -Force | Out-Null
  $zip = Join-Path $tmp "update.zip"
  try { Invoke-WebRequest -Uri $m.url -OutFile $zip -UseBasicParsing -TimeoutSec 120 -ErrorAction Stop }
  catch { $form.Cursor = 'Default'; $upd.Status.Text = "Download failed."; Say "The update could not be downloaded.`n`n$_" "Warning"; return }
  # Integrity check: the download must match the fingerprint published in version.json
  if ($m.sha256 -and ((Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash -ne $m.sha256.ToUpper())) {
    $form.Cursor = 'Default'; $upd.Status.Text = "Update rejected (file check failed)."
    Say "The downloaded update did not pass the security check, so it was not installed." "Error"; return
  }
  Expand-Archive -LiteralPath $zip -DestinationPath (Join-Path $tmp "files") -Force
  $inst = Get-ChildItem (Join-Path $tmp "files") -Recurse -Filter Install.ps1 | Select-Object -First 1
  if (-not $inst) { $form.Cursor = 'Default'; Say "The update package is missing its installer." "Error"; return }
  # Hand over to the installer: it replaces the app files, keeps settings, and restarts CleanSweep
  Start-Process powershell.exe -WindowStyle Hidden -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$($inst.FullName)`" -Quiet"
  $form.Close()
}

function Check-Update([bool]$manual) {
  if ($settings.UpdateUrl -match '/OWNER/') {
    $upd.Status.Text = "No update server is set up yet."
    if ($manual) { Say "No update server is set up yet, so CleanSweep can't check for updates." }
    return
  }
  $upd.Status.Text = "Checking for updates..."; $form.Refresh()
  try { $m = Invoke-RestMethod -Uri ($settings.UpdateUrl + "?t=" + [DateTime]::UtcNow.Ticks) -TimeoutSec 6 -UseBasicParsing -ErrorAction Stop }
  catch { $upd.Status.Text = "Could not reach the update server."; if ($manual) { Say "Could not reach the update server. Check your internet connection." "Warning" }; return }
  $settings.LastCheck = (Get-Date).ToString("o"); Save-Settings
  $upd.Last.Text = "Last checked: " + (Get-Date).ToString("MMM d, yyyy h:mm tt")
  try { $newer = ([version]$m.version -gt [version]$Version) } catch { $newer = $false }
  if (-not $newer) { $upd.Status.Text = "You have the latest version ($Version)."; if ($manual) { Say "You have the latest version of CleanSweep ($Version)." }; return }
  $upd.Status.Text = "Version $($m.version) is available."
  $ans = [Windows.Forms.MessageBox]::Show("CleanSweep $($m.version) is available. You have $Version.`n`nWhat's new:`n$($m.notes)`n`nInstall it now? CleanSweep will restart.", "CleanSweep Updates", "YesNo", "Question")
  if ($ans -eq "Yes") { Install-Update $m }
}

$upd = @{}
$upd.Page   = New-Object Windows.Forms.TabPage -Property @{Text="Updates"}
$upd.Title  = New-Object Windows.Forms.Label -Property @{AutoSize=$true; Margin='0,0,0,10'; Text="CleanSweep $Version"; Font=New-Object Drawing.Font("Segoe UI",16,[Drawing.FontStyle]::Bold)}
$upd.Status = New-Object Windows.Forms.Label -Property @{AutoSize=$true; Margin='0,0,0,4'; Text="Updates have not been checked yet."}
$upd.Last   = New-Object Windows.Forms.Label -Property @{AutoSize=$true; Margin='0,0,0,12'; ForeColor='DimGray'}
if ($settings.LastCheck) { try { $upd.Last.Text = "Last checked: " + ([datetime]$settings.LastCheck).ToString("MMM d, yyyy h:mm tt") } catch {} }
$upd.Auto   = New-Object Windows.Forms.CheckBox -Property @{AutoSize=$true; Margin='0,0,0,12'; Text="Check for updates automatically (once a day, when CleanSweep opens)"; Checked=[bool]$settings.AutoCheck}
$upd.Check  = New-Object Windows.Forms.Button -Property @{AutoSize=$true; MinimumSize='190,36'; Text="Check for updates now"}
$upd.Auto.Add_CheckedChanged({ $settings.AutoCheck = $upd.Auto.Checked; Save-Settings })
$upd.Check.Add_Click({ Check-Update $true })
$updFlow = New-Object Windows.Forms.FlowLayoutPanel -Property @{Dock='Fill'; FlowDirection='TopDown'; WrapContents=$false; Padding='20,20,20,20'; AutoScroll=$true}
$updFlow.Controls.AddRange(@($upd.Title, $upd.Status, $upd.Last, $upd.Auto, $upd.Check))
$upd.Page.Controls.Add($updFlow)
$tabs.TabPages.Add($upd.Page)

$form.Add_Shown({
  $due = $true
  if ($settings.LastCheck) { try { $due = ((Get-Date) - [datetime]$settings.LastCheck).TotalHours -ge 24 } catch {} }
  if ($settings.AutoCheck -and $due) { Check-Update $false }
})

[void]$form.ShowDialog()
