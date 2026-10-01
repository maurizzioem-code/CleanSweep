# ================================================================ DRIVES: every volume with a checkbox, and actions on the ticked drives
# Read-only by default. Actions use Windows' own tools (chkdsk /scan, defrag /O). Partitions are never changed;
# hidden Windows partitions (EFI, recovery, reserved) are listed for reference but can't be ticked.

$drv = @{}
$drv.Page   = New-Object Windows.Forms.TabPage -Property @{Text="Drives"; Padding='10,10,10,10'}
$drv.Status = New-Object Windows.Forms.Label -Property @{Dock='Top'; Height=36; TextAlign='MiddleLeft'; Text="Tick the drives you want, then choose an action."; Font=New-Object Drawing.Font("Segoe UI",12,[Drawing.FontStyle]::Bold)}
$drv.List   = New-Object Windows.Forms.ListView -Property @{View='Details'; CheckBoxes=$true; FullRowSelect=$true; Dock='Fill'; HideSelection=$false}
foreach ($c in @(@("Volume",190), @("Drive type",120), @("File system",85), @("Status",80), @("Capacity",90), @("Free space",90), @("% Free",60), @("Disk",170))) { [void]$drv.List.Columns.Add($c[0], $c[1]) }
$drv.Prog   = New-Object Windows.Forms.ProgressBar -Property @{Dock='Bottom'; Height=16; Minimum=0; Maximum=100}
$drv.Out    = New-Object Windows.Forms.TextBox -Property @{Dock='Bottom'; Height=120; Multiline=$true; ReadOnly=$true; ScrollBars='Vertical'; Font=New-Object Drawing.Font("Consolas",9); BackColor='White'}
$drv.Bar    = New-Object Windows.Forms.FlowLayoutPanel -Property @{Dock='Bottom'; AutoSize=$true; AutoSizeMode='GrowAndShrink'; WrapContents=$true; Padding='0,6,0,0'}
$bp = @{AutoSize=$true; MinimumSize='110,36'; Margin='0,0,8,6'}
$drv.Junk   = New-Object Windows.Forms.Button -Property ($bp + @{Text="Clean junk"; Enabled=$false})
$drv.Large  = New-Object Windows.Forms.Button -Property ($bp + @{Text="Find large files"; Enabled=$false})
$drv.Check  = New-Object Windows.Forms.Button -Property ($bp + @{Text="Check for errors"; Enabled=$false})
$drv.Opt    = New-Object Windows.Forms.Button -Property ($bp + @{Text="Optimize"; Enabled=$false})
$drv.Stop   = New-Object Windows.Forms.Button -Property ($bp + @{Text="Cancel"; Enabled=$false})
$drv.Refresh= New-Object Windows.Forms.Button -Property ($bp + @{Text="Refresh"})
$drv.DiskMgmt = New-Object Windows.Forms.Button -Property ($bp + @{Text="Disk Management"})
$drv.Bar.Controls.AddRange(@($drv.Junk, $drv.Large, $drv.Check, $drv.Opt, $drv.Stop, $drv.Refresh, $drv.DiskMgmt))
$drv.Page.Controls.Add($drv.List); $drv.Page.Controls.Add($drv.Prog); $drv.Page.Controls.Add($drv.Out); $drv.Page.Controls.Add($drv.Bar); $drv.Page.Controls.Add($drv.Status)
$drv.UI = @{ Out=$drv.Out; Prog=$drv.Prog; Status=$drv.Status }

# Put the Drives tab right after Junk Files
$others = @($tabs.TabPages | ForEach-Object { $_ }); $tabs.TabPages.Clear()
foreach ($p in $others) { $tabs.TabPages.Add($p); if ($p -eq $junk.Page) { $tabs.TabPages.Add($drv.Page) } }

$PartitionNames = @{ 'System'='EFI system partition'; 'Recovery'='Recovery partition'; 'Reserved'='Microsoft reserved'; 'Basic'='Partition'; 'IFS'='Partition'; 'FAT32'='Partition' }
function Get-DriveRows {
  $phys = @{}; foreach ($pd in (Get-PhysicalDisk -ErrorAction Ignore)) { $phys["$($pd.DeviceId)"] = $pd }
  $disks = @{}; foreach ($d in (Get-Disk -ErrorAction Ignore)) { $disks["$($d.Number)"] = $d }
  $rows = @()
  foreach ($p in (Get-Partition -ErrorAction Ignore | Sort-Object DiskNumber, Offset)) {
    $v = $null; try { $v = $p | Get-Volume -ErrorAction Stop } catch {}
    $pd = $phys["$($p.DiskNumber)"]; $dk = $disks["$($p.DiskNumber)"]
    $media = if ($pd -and $pd.MediaType -in 'SSD','HDD') { "$($pd.MediaType)" } elseif ($pd -and $pd.BusType -eq 'USB') { "USB" } else { "Disk" }
    if (($dk -and $dk.BusType -eq 'USB') -or ($pd -and $pd.BusType -eq 'USB')) { $media = "USB drive" }
    $letter = if ($p.DriveLetter -and [char]::IsLetter($p.DriveLetter)) { "$($p.DriveLetter):" } else { $null }
    $label = if ($v -and $v.FileSystemLabel) { $v.FileSystemLabel } elseif ($letter -eq $SysDrive) { "Windows" } elseif ($letter) { "Local Disk" } else { $PartitionNames["$($p.Type)"] }
    if (-not $label) { $label = "Partition" }
    $size = if ($v -and $v.Size) { [double]$v.Size } else { [double]$p.Size }
    $free = if ($v -and $v.Size) { [double]$v.SizeRemaining } else { $null }
    $rows += [pscustomobject]@{
      Letter=$letter; Name=$(if ($letter) { "$letter $label" } else { "($label)" }); Media=$(if ($letter -eq $SysDrive) { "$media (boot)" } else { $media })
      FS=$(if ($v) { $v.FileSystem } else { "" }); Health=$(if ($v -and $v.HealthStatus) { "$($v.HealthStatus)" } else { "Healthy" })
      Size=$size; Free=$free; Pct=$(if ($free -ne $null -and $size) { [math]::Round($free / $size * 100) } else { $null })
      Disk=("Disk {0}{1}" -f $p.DiskNumber, $(if ($dk) { " - " + $dk.FriendlyName } else { "" })); IsSystemPart=(-not $letter) }
  }
  # Drives without partition info (some USB card readers, network-less removable media)
  foreach ($ld in (Get-CimInstance Win32_LogicalDisk -Filter "DriveType=2 OR DriveType=3" -ErrorAction Ignore | Where-Object { $_.Size -gt 0 })) {
    if ($rows | Where-Object Letter -eq $ld.DeviceID) { continue }
    $rows += [pscustomobject]@{ Letter=$ld.DeviceID; Name="$($ld.DeviceID) $(if ($ld.VolumeName) { $ld.VolumeName } else { 'Removable' })"; Media="Removable"; FS=$ld.FileSystem; Health="Healthy"
      Size=[double]$ld.Size; Free=[double]$ld.FreeSpace; Pct=[math]::Round($ld.FreeSpace / $ld.Size * 100); Disk=""; IsSystemPart=$false }
  }
  # Lettered drives first (like Disk Management's volume list), then hidden partitions
  $rows | Sort-Object IsSystemPart, Letter
}

$script:DrvLoading = $false
function Load-DriveList {
  $prev = @{}; foreach ($i in $drv.List.Items) { if ($i.Tag.Letter) { $prev[$i.Tag.Letter] = $i.Checked } }
  $script:DrvLoading = $true; $drv.List.BeginUpdate(); $drv.List.Items.Clear()
  $rows = @(Get-DriveRows)
  foreach ($r in $rows) {
    $it = $drv.List.Items.Add($r.Name); $it.Tag = $r
    foreach ($s in @($r.Media, $r.FS, $r.Health, (Fmt $r.Size), $(if ($r.Free -ne $null) { Fmt $r.Free } else { "" }), $(if ($r.Pct -ne $null) { "$($r.Pct) %" } else { "" }), $r.Disk)) { [void]$it.SubItems.Add([string]$s) }
    if ($r.IsSystemPart) { $it.ForeColor = 'Gray'; $it.ToolTipText = "Windows needs this partition. CleanSweep never changes it." }
    else {
      $it.Checked = if ($prev.ContainsKey($r.Letter)) { $prev[$r.Letter] } else { $false }
      if ($r.Pct -ne $null -and $r.Pct -lt 10) { $it.UseItemStyleForSubItems = $false; $it.SubItems[6].ForeColor = 'Firebrick'; $it.SubItems[5].ForeColor = 'Firebrick' }
      if ($r.Health -ne 'Healthy') { $it.UseItemStyleForSubItems = $false; $it.SubItems[3].ForeColor = 'Firebrick' }
    }
  }
  $drv.List.EndUpdate(); $script:DrvLoading = $false; $drv.List.ShowItemToolTips = $true
  Update-DriveButtons
  $low = @($rows | Where-Object { $_.Letter -and $_.Pct -ne $null -and $_.Pct -lt 10 })
  $drv.Status.Text = if ($low) { "$(($low | ForEach-Object Letter) -join ', ') almost full. Tick it and try Clean junk or Find large files." } else { "Tick the drives you want, then choose an action." }
}
# Hidden Windows partitions can't be ticked
$drv.List.Add_ItemCheck({ param($s, $e) if (-not $script:DrvLoading -and $drv.List.Items[$e.Index].Tag.IsSystemPart) { $e.NewValue = 'Unchecked' } })
$drv.List.Add_ItemChecked({ if (-not $script:DrvLoading) { Update-DriveButtons } })
function Get-CheckedDrives { @($drv.List.CheckedItems | ForEach-Object { $_.Tag } | Where-Object Letter) }
function Update-DriveButtons {
  $n = (Get-CheckedDrives).Count; $idle = -not $script:RepairBusy
  foreach ($b in $drv.Junk, $drv.Large, $drv.Check, $drv.Opt) { $b.Enabled = $idle -and $n -gt 0 }
}
function Set-DriveBusy([bool]$b) {
  $script:RepairBusy = $b; $drv.Stop.Enabled = $b; $drv.List.Enabled = -not $b; $drv.Refresh.Enabled = -not $b
  $form.Cursor = if ($b) { 'AppStarting' } else { 'Default' }; Update-DriveButtons
}

# ---------------------------------------------------------------- actions
$drv.Junk.Add_Click({
  $want = @(Get-CheckedDrives | ForEach-Object Letter)
  Load-Drives; foreach ($cb in $script:DriveChecks) { $cb.Checked = $want -contains $cb.Tag }
  $tabs.SelectedTab = $junk.Page; [Windows.Forms.Application]::DoEvents(); $junk.Scan.PerformClick()
})

function Invoke-DriveTool($kind) {
  $drives = Get-CheckedDrives; if (-not $drives) { return }
  if (-not (Test-IsAdmin)) { [void][CSMsg]::Show("This needs administrator rights. Open CleanSweep from its Desktop shortcut and try again.","CleanSweep","OK","Warning"); return }
  $drv.Out.Clear(); $script:RepairCancel = $false; $script:ToolUI = $drv.UI; Set-DriveBusy $true; $summary = @()
  try {
    foreach ($d in $drives) {
      if ($script:RepairCancel) { break }
      $drv.Prog.Value = 0
      if ($kind -eq 'check') {
        # Online scan: the drive stays usable and nothing is changed
        $r = Invoke-ConsoleTool (Get-SysExe "chkdsk.exe") "$($d.Letter) /scan" "Checking $($d.Letter) for errors"
        $v = if ($r.Cancelled) { "cancelled" }
             elseif ($r.Code -eq 0 -or $r.Text -match 'found no problems') { "no problems found" }
             elseif ($r.Text -match 'not supported|cannot be scanned|not available') { "not supported for this file system" }
             else { "problems found - see the output. Windows can fix them: Settings > System > Storage, or run 'chkdsk $($d.Letter) /spotfix' (C: is fixed at the next restart)" }
      } else {
        # Windows picks the right method: TRIM for SSDs, defragment for hard drives
        $r = Invoke-ConsoleTool (Get-SysExe "defrag.exe") "$($d.Letter) /O /U /V" "Optimizing $($d.Letter)"
        $v = if ($r.Cancelled) { "cancelled" } elseif ($r.Code -eq 0) { if ($d.Media -match 'SSD') { "optimized (TRIM)" } else { "optimized" } }
             elseif ($r.Text -match 'not supported|cannot be optimized|not eligible') { "can't be optimized (Windows doesn't support it for this drive)" } else { "failed (code $($r.Code)) - see the output" }
      }
      $summary += "$($d.Letter) $v"; $drv.Out.AppendText("Result: $($d.Letter) $v`r`n`r`n")
    }
  } finally { $script:ToolUI = $null; Set-DriveBusy $false }
  $drv.Prog.Value = if ($script:RepairCancel) { 0 } else { 100 }
  $drv.Status.Text = if ($script:RepairCancel) { "Cancelled." } else { ($summary -join "; ") }
}
$drv.Check.Add_Click({ Invoke-DriveTool 'check' })
$drv.Opt.Add_Click({ Invoke-DriveTool 'optimize' })
$drv.Stop.Add_Click({ $script:RepairCancel = $true; $drv.Status.Text = "Cancelling..." })
$drv.Refresh.Add_Click({ Load-DriveList })
$drv.DiskMgmt.Add_Click({ Start-Process diskmgmt.msc })

# ---------------------------------------------------------------- large file finder
$LargeMin = 100MB
function Find-LargeFiles($letters) {
  $found = New-Object System.Collections.Generic.List[object]; $n = 0; $sw = [Diagnostics.Stopwatch]::StartNew()
  $skip = @("$env:WINDIR".ToLower())
  foreach ($l in $letters) {
    $stack = New-Object System.Collections.Generic.Stack[string]; $stack.Push("$l\")
    while ($stack.Count) {
      if ($script:RepairCancel) { return $found }
      $dir = $stack.Pop()
      try {
        foreach ($sub in [IO.Directory]::EnumerateDirectories($dir)) {
          $di = New-Object IO.DirectoryInfo $sub
          if ($di.Attributes -band [IO.FileAttributes]::ReparsePoint) { continue }
          if ($skip -contains $sub.ToLower() -or $di.Name -in 'System Volume Information','$Recycle.Bin','$WinREAgent') { continue }
          $stack.Push($sub)
        }
        foreach ($f in [IO.Directory]::EnumerateFiles($dir)) {
          $n++
          if ($n % 500 -eq 0) { $drv.Status.Text = "Looking for large files on $l ... {0:N0} files checked" -f $n; [Windows.Forms.Application]::DoEvents(); if ($script:RepairCancel) { return $found } }
          try {
            $fi = New-Object IO.FileInfo $f
            if ($fi.Length -ge $LargeMin -and $fi.Name -notin 'pagefile.sys','hiberfil.sys','swapfile.sys','DumpStack.log') {
              $found.Add([pscustomobject]@{ Path=$fi.FullName; Name=$fi.Name; Folder=$fi.DirectoryName; Size=[double]$fi.Length; Modified=$fi.LastWriteTime })
            }
          } catch {}
        }
      } catch {}
    }
  }
  $drv.Status.Text = "Checked {0:N0} files in {1:N0} s." -f $n, $sw.Elapsed.TotalSeconds
  return $found
}

function Show-LargeFiles($files, $letters) {
  $f = New-Object Windows.Forms.Form -Property @{Text="Large files on $($letters -join ', ') (100 MB or more)"; Size='900,560'; StartPosition='CenterParent'; Font=$form.Font; MinimumSize='640,400'; Icon=$form.Icon}
  $top = New-Object Windows.Forms.Label -Property @{Dock='Top'; Height=44; Padding='8,6,8,0'
    Text="$($files.Count) large files, $(Fmt (($files | Measure-Object Size -Sum).Sum)) in total. Nothing is ticked: tick only files you are sure you don't need. Files in Program Files belong to apps - uninstall the app instead."}
  $lv = New-Object Windows.Forms.ListView -Property @{View='Details'; CheckBoxes=$true; FullRowSelect=$true; Dock='Fill'}
  foreach ($c in @(@("Size",90), @("Name",250), @("Modified",110), @("Folder",420))) { [void]$lv.Columns.Add($c[0], $c[1]) }
  foreach ($x in ($files | Sort-Object Size -Descending | Select-Object -First 300)) {
    $it = $lv.Items.Add((Fmt $x.Size)); [void]$it.SubItems.Add($x.Name); [void]$it.SubItems.Add($x.Modified.ToString("yyyy-MM-dd")); [void]$it.SubItems.Add($x.Folder); $it.Tag = $x
    if ($x.Folder -match '\\Program Files|\\ProgramData\\|\\AppData\\') { $it.ForeColor = 'Gray' }
  }
  $bar = New-Object Windows.Forms.FlowLayoutPanel -Property @{Dock='Bottom'; AutoSize=$true; AutoSizeMode='GrowAndShrink'; Padding='6,6,6,6'}
  $open = New-Object Windows.Forms.Button -Property ($bp + @{Text="Open file location"})
  $del  = New-Object Windows.Forms.Button -Property ($bp + @{Text="Move ticked to Recycle Bin"; Enabled=$false})
  $close= New-Object Windows.Forms.Button -Property ($bp + @{Text="Close"; DialogResult='Cancel'})
  $bar.Controls.AddRange(@($open, $del, $close)); $f.CancelButton = $close
  $f.Controls.Add($lv); $f.Controls.Add($bar); $f.Controls.Add($top)
  $lv.Add_ItemChecked({ $del.Enabled = $lv.CheckedItems.Count -gt 0 }.GetNewClosure())
  $open.Add_Click({ $s = if ($lv.SelectedItems.Count) { $lv.SelectedItems[0] } elseif ($lv.CheckedItems.Count) { $lv.CheckedItems[0] } else { $null }
    if ($s) { Start-Process explorer.exe "/select,`"$($s.Tag.Path)`"" } }.GetNewClosure())
  $lv.Add_DoubleClick({ if ($lv.SelectedItems.Count) { Start-Process explorer.exe "/select,`"$($lv.SelectedItems[0].Tag.Path)`"" } }.GetNewClosure())
  $del.Add_Click({
    $items = @($lv.CheckedItems); $sum = ($items | ForEach-Object { $_.Tag.Size } | Measure-Object -Sum).Sum
    if ([CSMsg]::Show("Move $($items.Count) file(s) ($(Fmt $sum)) to the Recycle Bin?`n`nYou can restore them from the Recycle Bin. Space is freed when the Recycle Bin is emptied (Junk Files tab).","CleanSweep","YesNo","Question") -ne 'Yes') { return }
    Add-Type -AssemblyName Microsoft.VisualBasic; $ok = 0
    foreach ($it in $items) {
      try { [Microsoft.VisualBasic.FileIO.FileSystem]::DeleteFile($it.Tag.Path, 'OnlyErrorDialogs', 'SendToRecycleBin'); $lv.Items.Remove($it); $ok++ }
      catch { [void][CSMsg]::Show("Could not move $($it.Tag.Name): $($_.Exception.Message)","CleanSweep","OK","Warning") }
    }
    $top.Text = "Moved $ok file(s) to the Recycle Bin. Empty it from the Junk Files tab to free the space."
  }.GetNewClosure())
  if (-not $TestMode) { [void]$f.ShowDialog($form) }
  return $f
}
$drv.Large.Add_Click({
  $letters = @(Get-CheckedDrives | ForEach-Object Letter); $script:RepairCancel = $false; $drv.Out.Clear()
  Set-DriveBusy $true
  try { $files = Find-LargeFiles $letters } finally { Set-DriveBusy $false }
  $script:LargeFiles = $files
  if ($script:RepairCancel) { $drv.Status.Text = "Cancelled."; return }
  $drv.Out.AppendText(("Largest files:`r`n" + (($files | Sort-Object Size -Descending | Select-Object -First 10 | ForEach-Object { "{0,10}  {1}" -f (Fmt $_.Size), $_.Path }) -join "`r`n")) + "`r`n")
  if (-not $files.Count) { $drv.Status.Text = "No files of 100 MB or more found."; return }
  $drv.Status.Text = "Found $($files.Count) large files ($(Fmt (($files | Measure-Object Size -Sum).Sum)))."
  $script:LargeForm = Show-LargeFiles $files $letters
})

$tabs.Add_SelectedIndexChanged({ if ($tabs.SelectedTab -eq $drv.Page -and -not $script:RepairBusy) { Load-DriveList } })
Load-DriveList
