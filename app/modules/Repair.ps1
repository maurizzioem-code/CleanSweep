# ================================================================ REPAIR: SFC, DISM and Windows Update repair
# Uses only Windows' own repair tools. Every tool runs only when you click it, output is shown live and saved,
# and the Windows Update repair keeps backups (renamed, never deleted) so it can be undone.

$RepairLogDir = Join-Path $AppDir "logs"
$WuJournal    = Join-Path $AppDir "wu-repair.json"
$script:RepairProc = $null; $script:RepairCancel = $false; $script:RepairBusy = $false
$script:RepairAutoCancelSec = 0   # used by the automated tests

$RepairTools = [ordered]@{
  "dism-check"   = @{ Name="Quick Windows health check"; Time="1 min";  Kind="check"
    What="Asks Windows if its image has been flagged as damaged (DISM /CheckHealth). Changes nothing." }
  "dism-scan"    = @{ Name="Scan Windows image"; Time="5-15 min"; Kind="check"
    What="Thoroughly checks the Windows component store for damage (DISM /ScanHealth). Changes nothing." }
  "dism-restore" = @{ Name="Repair Windows image"; Time="10-30 min"; Kind="repair"
    What="Repairs the component store using fresh copies from Windows Update (DISM /RestoreHealth). Run this before System File Checker." }
  "sfc"          = @{ Name="System File Checker"; Time="10-20 min"; Kind="repair"
    What="Checks all protected Windows files and replaces damaged ones with good copies (sfc /scannow)." }
  "wu-reset"     = @{ Name="Repair Windows Update"; Time="1-2 min"; Kind="repair"
    What="For updates that fail or get stuck: restarts the update services and sets aside the update download cache and catroot2 (kept as backups so it can be undone). Re-enables update services if another tool disabled them." }
  "wu-undo"      = @{ Name="Undo Windows Update repair"; Time="1 min"; Kind="undo"
    What="Puts back the update cache folders and service settings from the last Windows Update repair." }
  "wu-purge"     = @{ Name="Delete Windows Update repair backups"; Time="1 min"; Kind="cleanup"
    What="Frees the space used by the backup folders once updates are working again. After this the repair can't be undone." }
  "dism-cleanup" = @{ Name="Clean up old update components"; Time="5-20 min"; Kind="cleanup"
    What="Lets Windows remove superseded update files from WinSxS (DISM /StartComponentCleanup). Safe, but older updates can no longer be uninstalled afterwards." }
}

# 64-bit tools even if CleanSweep was started from a 32-bit process (32-bit DISM can't service 64-bit Windows)
function Get-SysExe($name) { $n = "$env:WINDIR\Sysnative\$name"; if (Test-Path $n) { $n } else { "$env:WINDIR\System32\$name" } }
function Test-IsAdmin { ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator) }

# ---------------------------------------------------------------- UI
$rep = @{}
$rep.Page = New-Object Windows.Forms.TabPage -Property @{Text="Repair"; Padding='10,10,10,10'}
$rep.Status = New-Object Windows.Forms.Label -Property @{Dock='Top'; Height=36; TextAlign='MiddleLeft'; Text="Select a repair tool, then click Run. Not sure? Click Recommended repair."; Font=New-Object Drawing.Font("Segoe UI",12,[Drawing.FontStyle]::Bold)}
$rep.List = New-Object Windows.Forms.ListView -Property @{View='Details'; FullRowSelect=$true; Dock='Top'; Height=190; HideSelection=$false; MultiSelect=$false}
foreach ($c in @(@("Tool",260), @("Time",100), @("Last result",420))) { [void]$rep.List.Columns.Add($c[0], $c[1]) }
$rep.Info = New-Object Windows.Forms.Label -Property @{Dock='Top'; Height=44; Padding='2,4,2,0'; ForeColor='DimGray'; Text="DISM repairs Windows' own store of system files; System File Checker then uses it to fix your installed files."}
$rep.Prog = New-Object CSProgress -Property @{Dock='Top'; Height=18; Minimum=0; Maximum=100}
$rep.Out  = New-Object Windows.Forms.TextBox -Property @{Dock='Fill'; Multiline=$true; ReadOnly=$true; ScrollBars='Vertical'; WordWrap=$true; Font=New-Object Drawing.Font("Consolas",9)}
$rep.Opt  = New-Object Windows.Forms.FlowLayoutPanel -Property @{Dock='Bottom'; AutoSize=$true; AutoSizeMode='GrowAndShrink'; Padding='0,4,0,0'}
$rep.Rp   = New-Object Windows.Forms.CheckBox -Property @{Text="Create a System Restore point before repairs (recommended)"; AutoSize=$true; Checked=$true}
$rep.Opt.Controls.Add($rep.Rp)
$rep.Bar  = New-Object Windows.Forms.FlowLayoutPanel -Property @{Dock='Bottom'; AutoSize=$true; AutoSizeMode='GrowAndShrink'; WrapContents=$true; Padding='0,6,0,0'}
$bp = @{AutoSize=$true; MinimumSize='110,36'; Margin='0,0,8,0'}
$rep.Run  = New-Object Windows.Forms.Button -Property ($bp + @{Text="Run selected"; Enabled=$false})
$rep.Rec  = New-Object Windows.Forms.Button -Property ($bp + @{Text="Recommended repair"})
$rep.Stop = New-Object Windows.Forms.Button -Property ($bp + @{Text="Cancel"; Enabled=$false})
$rep.Logs = New-Object Windows.Forms.Button -Property ($bp + @{Text="Open logs"})
$rep.Cbs  = New-Object Windows.Forms.Button -Property ($bp + @{Text="Open CBS log"})
$rep.Bar.Controls.AddRange(@($rep.Run, $rep.Rec, $rep.Stop, $rep.Logs, $rep.Cbs))
# Docking: Fill first, then bottom bars, then top items (last added = topmost)
$rep.Page.Controls.Add($rep.Out); $rep.Page.Controls.Add($rep.Opt); $rep.Page.Controls.Add($rep.Bar)
$rep.Page.Controls.Add($rep.Prog); $rep.Page.Controls.Add($rep.Info); $rep.Page.Controls.Add($rep.List); $rep.Page.Controls.Add($rep.Status)

# Put the Repair tab just before Updates
$others = @($tabs.TabPages | ForEach-Object { $_ }); $tabs.TabPages.Clear()
foreach ($p in $others) { if ($p -eq $upd.Page) { $tabs.TabPages.Add($rep.Page) }; $tabs.TabPages.Add($p) }
if (-not ($tabs.TabPages.Contains($rep.Page))) { $tabs.TabPages.Add($rep.Page) }

$RepairResultFile = Join-Path $AppDir "repair-results.json"
function Get-RepairResults { try { if (Test-Path $RepairResultFile) { return (Get-Content $RepairResultFile -Raw | ConvertFrom-Json) } } catch {}; return $null }
function Set-RepairResult($id, $text) {
  $r = @{}; $old = Get-RepairResults; if ($old) { foreach ($p in $old.PSObject.Properties) { $r[$p.Name] = $p.Value } }
  $r[$id] = (Get-Date).ToString("MMM d HH:mm") + " - " + $text
  try { New-Item -ItemType Directory $AppDir -Force | Out-Null; $r | ConvertTo-Json | Set-Content $RepairResultFile -Encoding UTF8 } catch {}
}
function Get-WuBackups { @(Get-ChildItem "$env:WINDIR\SoftwareDistribution.bak-*", "$env:WINDIR\System32\catroot2.bak-*" -Directory -ErrorAction Ignore) }

function Update-RepairList {
  $sel = if ($rep.List.SelectedItems.Count) { $rep.List.SelectedItems[0].Tag } else { $null }
  $res = Get-RepairResults; $haveJournal = Test-Path $WuJournal; $haveBackups = (Get-WuBackups).Count -gt 0
  $rep.List.BeginUpdate(); $rep.List.Items.Clear()
  foreach ($id in $RepairTools.Keys) {
    if ($id -eq 'wu-undo' -and -not $haveJournal) { continue }
    if ($id -eq 'wu-purge' -and -not $haveBackups) { continue }
    $t = $RepairTools[$id]
    $it = $rep.List.Items.Add($t.Name); [void]$it.SubItems.Add($t.Time)
    $last = if ($res -and $res.$id) { $res.$id } else { "Not run yet" }; [void]$it.SubItems.Add($last)
    $it.Tag = $id; if ($id -eq $sel) { $it.Selected = $true }
  }
  $rep.List.EndUpdate()
}
$rep.List.Add_SelectedIndexChanged({
  if (-not $rep.List.SelectedItems.Count) { $rep.Run.Enabled = $false; return }
  $t = $RepairTools[$rep.List.SelectedItems[0].Tag]
  $rep.Info.Text = $t.What; $rep.Run.Enabled = -not $script:RepairBusy
})
$rep.List.Add_DoubleClick({ if ($rep.Run.Enabled) { $rep.Run.PerformClick() } })

# ---------------------------------------------------------------- output helpers
function Write-RepairOut([string]$line) { $rep.Out.AppendText($line + "`r`n"); [Windows.Forms.Application]::DoEvents() }
function Set-RepairBusy([bool]$b) {
  $script:RepairBusy = $b; $rep.Rec.Enabled = -not $b; $rep.Stop.Enabled = $b; $rep.List.Enabled = -not $b; $rep.Rp.Enabled = -not $b
  $rep.Run.Enabled = (-not $b) -and $rep.List.SelectedItems.Count; $form.Cursor = if ($b) { 'AppStarting' } else { 'Default' }
}
# SFC writes UTF-16 text when redirected; DISM writes in the console code page
function Read-ToolOutput($file) {
  try {
    $fs = [IO.File]::Open($file, 'Open', 'Read', 'ReadWrite,Delete'); $ms = New-Object IO.MemoryStream; $fs.CopyTo($ms); $fs.Close(); $b = $ms.ToArray()
  } catch { return "" }
  if (-not $b.Length) { return "" }
  $zeros = 0; $n = [math]::Min($b.Length, 400); for ($i = 1; $i -lt $n; $i += 2) { if ($b[$i] -eq 0) { $zeros++ } }
  if ($zeros -gt $n / 4) { return [Text.Encoding]::Unicode.GetString($b) }
  $oem = try { [Text.Encoding]::GetEncoding([Globalization.CultureInfo]::CurrentCulture.TextInfo.OEMCodePage) } catch { [Text.Encoding]::Default }
  return $oem.GetString($b)
}
# Progress lines overwrite themselves with carriage returns; keep only the last state of each line
function Format-ToolOutput([string]$raw) {
  $lines = foreach ($l in ($raw -split "`r?`n")) { $parts = @($l -split "`r" | Where-Object { $_.Trim() }); if ($parts) { $parts[-1].TrimEnd() } }
  (@($lines) | Where-Object { $_ -ne $null }) -join "`r`n"
}

# Runs a console tool hidden, streams its output into the window, returns @{Code; Text}
function Invoke-ConsoleTool([string]$exe, [string]$toolArgs, [string]$title) {
  $ui = if ($script:ToolUI) { $script:ToolUI } else { $rep }   # which tab shows the output
  New-Item -ItemType Directory $RepairLogDir -Force | Out-Null
  $tmp = Join-Path $env:TEMP ("CleanSweep-" + [guid]::NewGuid().ToString("N") + ".txt")
  $psi = New-Object Diagnostics.ProcessStartInfo -Property @{ FileName=(Get-SysExe "cmd.exe"); Arguments="/c `"`"$exe`" $toolArgs > `"$tmp`" 2>&1`""; UseShellExecute=$false; CreateNoWindow=$true }
  $ui.Out.AppendText(">>> $title  ($exe $toolArgs)`r`n")
  $p = [Diagnostics.Process]::Start($psi); $script:RepairProc = $p
  $sw = [Diagnostics.Stopwatch]::StartNew(); $shown = ""; $baseText = $ui.Out.Text
  while (-not $p.HasExited) {
    for ($i = 0; $i -lt 5 -and -not $p.HasExited; $i++) { Start-Sleep -Milliseconds 100; [Windows.Forms.Application]::DoEvents() }
    if ($script:RepairAutoCancelSec -and $sw.Elapsed.TotalSeconds -gt $script:RepairAutoCancelSec) { $script:RepairCancel = $true }
    if ($script:RepairCancel) {
      & taskkill.exe /PID $p.Id /T /F 2>&1 | Out-Null; break
    }
    $txt = Format-ToolOutput (Read-ToolOutput $tmp)
    if ($txt -ne $shown) {
      $shown = $txt; $ui.Out.Text = $baseText + $txt; $ui.Out.SelectionStart = $ui.Out.TextLength; $ui.Out.ScrollToCaret()
      $m = [regex]::Matches($txt, '(\d{1,3})(?:[.,]\d)?\s?%'); if ($m.Count) { $v = [int]$m[$m.Count - 1].Groups[1].Value; if ($v -le 100) { $ui.Prog.Value = $v } }
    }
    $ui.Status.Text = "$title... " + ("{0:mm\:ss}" -f $sw.Elapsed) + $(if ($ui.Prog.Value) { "  ($($ui.Prog.Value)%)" } else { "" })
  }
  $p.WaitForExit(5000) | Out-Null
  $txt = Format-ToolOutput (Read-ToolOutput $tmp); $code = if ($script:RepairCancel) { -1 } else { $p.ExitCode }
  $ui.Out.Text = $baseText + $txt + $(if ($script:RepairCancel) { "`r`n(Cancelled)" } else { "" }) + "`r`n"; $ui.Out.SelectionStart = $ui.Out.TextLength; $ui.Out.ScrollToCaret()
  try { Copy-Item $tmp (Join-Path $RepairLogDir ("{0}-{1}.txt" -f ($title -replace '[^\w]+','-'), (Get-Date -Format "yyyyMMdd-HHmmss"))) -ErrorAction Ignore; Remove-Item $tmp -Force -ErrorAction Ignore } catch {}
  $script:RepairProc = $null
  return @{ Code=$code; Text=$txt; Cancelled=$script:RepairCancel }
}

# Plain-language result for each tool (English text first, exit code as fallback for other languages)
function Get-ToolVerdict($id, $r) {
  if ($r.Cancelled) { return @("Info", "Cancelled") }
  $t = $r.Text; $hex = "0x{0:X8}" -f $r.Code
  switch ($id) {
    'sfc' {
      if ($t -match 'did not find any integrity violations') { return @("OK", "No problems found") }
      if ($t -match 'successfully repaired') { return @("Repaired", "Found and repaired damaged files - restart your PC") }
      if ($t -match 'unable to fix|not able to fix') { return @("Problem", "Some files could not be repaired - run Repair Windows image, restart, then run this again") }
      if ($t -match 'repair pending') { return @("Problem", "A repair is waiting for a restart - restart and run this again") }
      if ($t -match 'could not perform') { return @("Problem", "Windows could not run the check - restart and try again") }
      if ($r.Code -eq 0) { return @("OK", "Finished (see the output for details)") }
    }
    { $_ -like 'dism-*' } {
      if ($r.Code -eq 0) {
        if ($t -match 'No component store corruption detected') { return @("OK", "No damage found") }
        if ($t -match 'is repairable') { return @("Problem", "Damage found - run Repair Windows image") }
        if ($t -match 'restore operation completed successfully') { return @("Repaired", "Windows image is healthy (repaired if needed) - now run System File Checker") }
        return @("OK", "Completed successfully")
      }
      if ($hex -eq '0x800F081F' -or $hex -eq '0x800F0906') { return @("Problem", "Repair files could not be downloaded ($hex) - check your internet connection and Windows Update, then try again") }
      if ($hex -eq '0x800F0954') { return @("Problem", "Blocked by a Windows Update policy ($hex) - common on work-managed PCs") }
      if ($hex -eq '0x800F0806' -or $r.Code -eq 3010) { return @("Problem", "Another update or repair is pending - restart and try again") }
    }
  }
  return @("Problem", "Failed (code $hex) - see the output and log")
}

# ---------------------------------------------------------------- Windows Update repair (with undo journal)
$WuServices = 'wuauserv','bits','cryptsvc','UsoSvc'
$WuDefaultStart = @{ wuauserv='Manual'; bits='Manual'; cryptsvc='Automatic'; UsoSvc='Automatic' }
function Stop-WuServices {
  foreach ($s in $WuServices) {
    $svc = Get-Service $s -ErrorAction Ignore; if (-not $svc -or $svc.Status -eq 'Stopped') { continue }
    Write-RepairOut "Stopping $($svc.DisplayName)..."
    try { Stop-Service $s -Force -ErrorAction Stop } catch { Write-RepairOut "  could not stop $s right away: $($_.Exception.Message)" }
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ((Get-Service $s).Status -ne 'Stopped' -and $sw.Elapsed.TotalSeconds -lt 30) { Start-Sleep -Milliseconds 300; [Windows.Forms.Application]::DoEvents() }
  }
}
function Start-WuServices {
  foreach ($s in 'cryptsvc','bits','wuauserv','UsoSvc') {
    $svc = Get-Service $s -ErrorAction Ignore; if (-not $svc -or $svc.StartType -eq 'Disabled') { continue }
    try { Start-Service $s -ErrorAction Stop; Write-RepairOut "Started $($svc.DisplayName)" } catch { Write-RepairOut "  $s will start when needed ($($_.Exception.Message))" }
  }
}
function Invoke-WuReset {
  $stamp = Get-Date -Format "yyyyMMdd-HHmmss"; $journal = @{ Date=(Get-Date).ToString("s"); Renamed=@(); StartTypes=@{} }
  # 1. Services another "optimizer" may have disabled
  foreach ($s in $WuServices) {
    $svc = Get-Service $s -ErrorAction Ignore
    if ($svc -and $svc.StartType -eq 'Disabled') {
      $journal.StartTypes[$s] = 'Disabled'
      Set-Service $s -StartupType $WuDefaultStart[$s] -ErrorAction Ignore
      Write-RepairOut "$($svc.DisplayName) was disabled - set back to the Windows default ($($WuDefaultStart[$s]))"
    }
  }
  # 2. Stop, set aside the caches, start again
  Stop-WuServices
  foreach ($f in "$env:WINDIR\SoftwareDistribution", "$env:WINDIR\System32\catroot2") {
    if (-not (Test-Path $f)) { continue }
    $to = "$f.bak-$stamp"
    try { Rename-Item -LiteralPath $f -NewName (Split-Path $to -Leaf) -ErrorAction Stop; $journal.Renamed += @{ From=$f; To=$to }; Write-RepairOut "Set aside $f  ->  $(Split-Path $to -Leaf)" }
    catch { Write-RepairOut "Could not set aside $f ($($_.Exception.Message)) - a restart usually releases it" }
  }
  if ($journal.Renamed.Count -or $journal.StartTypes.Count) { $journal | ConvertTo-Json -Depth 4 | Set-Content $WuJournal -Encoding UTF8 }
  Start-WuServices
  Write-RepairOut "Windows will rebuild the update cache on the next check for updates."
  if (-not $TestMode) { Start-Process "ms-settings:windowsupdate-action" -ErrorAction Ignore }
  if ($journal.Renamed.Count -eq 2) { return @("Repaired", "Update components reset - Windows Update is checking again (undo available)") }
  if ($journal.Renamed.Count) { return @("Repaired", "Partly reset ($($journal.Renamed.Count) of 2 folders) - restart and run again if updates still fail") }
  return @("Problem", "The update folders were in use - restart your PC and run this again")
}
function Invoke-WuUndo {
  if (-not (Test-Path $WuJournal)) { return @("Info", "Nothing to undo") }
  $j = Get-Content $WuJournal -Raw | ConvertFrom-Json; $stamp = Get-Date -Format "yyyyMMdd-HHmmss"; $ok = $true
  Stop-WuServices
  foreach ($r in @($j.Renamed)) {
    if (-not (Test-Path $r.To)) { Write-RepairOut "Backup $($r.To) no longer exists"; $ok = $false; continue }
    try {
      if (Test-Path $r.From) { Rename-Item -LiteralPath $r.From -NewName ((Split-Path $r.From -Leaf) + ".rebuilt-$stamp") -ErrorAction Stop }
      Rename-Item -LiteralPath $r.To -NewName (Split-Path $r.From -Leaf) -ErrorAction Stop; Write-RepairOut "Restored $($r.From)"
    } catch { Write-RepairOut "Could not restore $($r.From): $($_.Exception.Message)"; $ok = $false }
  }
  if ($j.StartTypes) { foreach ($p in $j.StartTypes.PSObject.Properties) { Set-Service $p.Name -StartupType $p.Value -ErrorAction Ignore; Write-RepairOut "Service $($p.Name) set back to $($p.Value)" } }
  Start-WuServices
  if ($ok) { Remove-Item $WuJournal -Force -ErrorAction Ignore; return @("OK", "Windows Update repair undone") }
  return @("Problem", "Partly undone - see the output")
}
function Invoke-WuPurge {
  Stop-WuServices; $freed = 0
  foreach ($d in (Get-WuBackups)) {
    $sz = [double]((Get-ChildItem -LiteralPath $d.FullName -Recurse -Force -File -ErrorAction Ignore | Measure-Object Length -Sum).Sum)
    try { Remove-Item -LiteralPath $d.FullName -Recurse -Force -ErrorAction Stop; $freed += $sz; Write-RepairOut "Deleted $($d.Name) ($(Fmt $sz))" } catch { Write-RepairOut "Could not delete $($d.Name): $($_.Exception.Message)" }
  }
  Get-ChildItem "$env:WINDIR\SoftwareDistribution.rebuilt-*", "$env:WINDIR\System32\catroot2.rebuilt-*" -Directory -ErrorAction Ignore | ForEach-Object { Remove-Item -LiteralPath $_.FullName -Recurse -Force -ErrorAction Ignore }
  Remove-Item $WuJournal -Force -ErrorAction Ignore
  Start-WuServices
  return @("OK", "Freed $(Fmt $freed)")
}

# ---------------------------------------------------------------- run
function Invoke-RepairTool($id) {
  $t = $RepairTools[$id]; $rep.Prog.Value = 0
  switch ($id) {
    'dism-check'   { $r = Invoke-ConsoleTool (Get-SysExe "Dism.exe") "/Online /Cleanup-Image /CheckHealth" $t.Name; $v = Get-ToolVerdict $id $r }
    'dism-scan'    { $r = Invoke-ConsoleTool (Get-SysExe "Dism.exe") "/Online /Cleanup-Image /ScanHealth" $t.Name; $v = Get-ToolVerdict $id $r }
    'dism-restore' { $r = Invoke-ConsoleTool (Get-SysExe "Dism.exe") "/Online /Cleanup-Image /RestoreHealth" $t.Name; $v = Get-ToolVerdict $id $r }
    'dism-cleanup' { $r = Invoke-ConsoleTool (Get-SysExe "Dism.exe") "/Online /Cleanup-Image /StartComponentCleanup" $t.Name; $v = Get-ToolVerdict $id $r }
    'sfc'          { $r = Invoke-ConsoleTool (Get-SysExe "sfc.exe") "/scannow" $t.Name; $v = Get-ToolVerdict $id $r }
    'wu-reset'     { Write-RepairOut ">>> $($t.Name)"; $v = Invoke-WuReset }
    'wu-undo'      { Write-RepairOut ">>> $($t.Name)"; $v = Invoke-WuUndo }
    'wu-purge'     { Write-RepairOut ">>> $($t.Name)"; $v = Invoke-WuPurge }
  }
  Write-RepairOut ("Result: " + $v[1]); Write-RepairOut ""
  if ($v[1] -ne 'Cancelled') { Set-RepairResult $id $v[1] }
  $rep.Prog.Value = if ($v[1] -eq 'Cancelled') { 0 } else { 100 }
  return $v
}
# Restore point once per session of repairs, only for tools that change something
function Invoke-RepairPreflight($ids) {
  if (-not (Test-IsAdmin)) { [void][CSMsg]::Show("Repairs need administrator rights. Open CleanSweep from its Desktop shortcut (it asks for permission) and try again.","CleanSweep","OK","Warning"); return $false }
  $changes = @($ids | Where-Object { $RepairTools[$_].Kind -in 'repair','cleanup' })
  if ($changes -and $rep.Rp.Checked) {
    $rep.Status.Text = "Creating a restore point..."; [Windows.Forms.Application]::DoEvents()
    if (New-RestorePoint "CleanSweep - before system repair") { Write-RepairOut "Restore point created." }
    elseif ([CSMsg]::Show("A restore point could not be created (System Restore may be turned off).`n`nThe repair tools use Windows' own files, so this is usually fine. Continue anyway?","CleanSweep","YesNo","Question") -ne 'Yes') { return $false }
    else { Write-RepairOut "No restore point (System Restore unavailable) - continuing." }
  }
  return $true
}
function Start-Repairs($ids, $label) {
  $rep.Out.Clear(); $script:RepairCancel = $false
  if (-not (Invoke-RepairPreflight $ids)) { return }
  Set-RepairBusy $true; $results = @()
  try {
    foreach ($id in $ids) { if ($script:RepairCancel) { break }; $results += ,@($id, (Invoke-RepairTool $id)) }
  } finally { Set-RepairBusy $false; Update-RepairList }
  $last = $results[-1][1]
  $rep.Status.Text = if ($script:RepairCancel) { "Cancelled." } elseif ($results.Count -gt 1) { "$label finished: " + (($results | ForEach-Object { $RepairTools[$_[0]].Name + " - " + $_[1][0] }) -join "; ") } else { $last[1] }
  if ($results | Where-Object { $_[1][1] -match 'restart' }) { [void][CSMsg]::Show("Restart your PC to finish the repair.","CleanSweep","OK","Information") }
}

$rep.Run.Add_Click({ if ($rep.List.SelectedItems.Count) { $id = $rep.List.SelectedItems[0].Tag; Start-Repairs @($id) $RepairTools[$id].Name } })
$rep.Rec.Add_Click({
  $msg = "Recommended repair runs, in Microsoft's suggested order:`n`n1. Repair Windows image (DISM /RestoreHealth)`n2. System File Checker (sfc /scannow)`n`nIt usually takes 20-45 minutes. You can keep using your PC. Start now?"
  if ([CSMsg]::Show($msg,"CleanSweep","YesNo","Question") -eq 'Yes') { Start-Repairs @('dism-restore','sfc') "Recommended repair" }
})
$rep.Stop.Add_Click({ $script:RepairCancel = $true; $rep.Status.Text = "Cancelling..." })
$rep.Logs.Add_Click({ New-Item -ItemType Directory $RepairLogDir -Force | Out-Null; Start-Process explorer.exe $RepairLogDir })
$rep.Cbs.Add_Click({ $f = "$env:WINDIR\Logs\CBS\CBS.log"; if (Test-Path $f) { Start-Process notepad.exe $f } else { [void][CSMsg]::Show("CBS.log was not found.","CleanSweep","OK","Information") } })
$form.Add_FormClosing({ if ($script:RepairBusy -and -not $TestMode) {
  if ([CSMsg]::Show("A repair is still running. Stop it and close CleanSweep?","CleanSweep","YesNo","Warning") -ne 'Yes') { $_.Cancel = $true } else { $script:RepairCancel = $true } } })

if (-not (Test-IsAdmin)) { $rep.Status.Text = "Repairs need administrator rights - open CleanSweep from its Desktop shortcut." }
Update-RepairList
