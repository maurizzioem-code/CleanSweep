# Automated test run of CleanSweep on a Windows VM (GitHub Actions).
# Loads the app without blocking, drives every tab, checks layout, takes screenshots and records errors.
param([string]$Out = "$PSScriptRoot\..\test-output")
$ErrorActionPreference = "Continue"
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
New-Item -ItemType Directory $Out -Force | Out-Null
$Out = (Resolve-Path $Out).Path
$env:CLEANSWEEP_TEST = "1"
$env:CLEANSWEEP_TRACE = Join-Path $Out "trace.txt"
$results = New-Object System.Collections.Generic.List[object]
$log = Join-Path $Out "log.txt"
function Note($t) { Write-Host $t; Add-Content -LiteralPath $log -Value $t }

function Step($name, [scriptblock]$body) {
  Write-Host "START $name"; [IO.File]::AppendAllText($env:CLEANSWEEP_TRACE, "=== START $name`r`n")
  $before = $Error.Count; $sw = [Diagnostics.Stopwatch]::StartNew(); $ex = $null
  try { & $body } catch { $ex = $_ }
  $sw.Stop()
  $errs = @(); if ($Error.Count -gt $before) { $errs = @($Error[0..($Error.Count - $before - 1)] | ForEach-Object { "$_ (line $($_.InvocationInfo.ScriptLineNumber): $($_.InvocationInfo.Line.Trim()))" }) }
  $errs = @($errs | Where-Object { $_ -notmatch 'is denied|being used by another process|because it does not exist|^CANCELLED|No matching MSFT_|^Not supported|^Invalid namespace|^Invalid class|No MSFT_|not supported on this operating system' })   # expected: files in use are skipped
  if ($ex) { $errs = @("THROWN: $ex (line $($ex.InvocationInfo.ScriptLineNumber))") + $errs }
  $status = if ($errs) { "ISSUES" } else { "PASS" }
  $results.Add([pscustomobject]@{ Step=$name; Status=$status; Seconds=[math]::Round($sw.Elapsed.TotalSeconds,1); Errors=($errs | Select-Object -Unique) })
  Note "[$status] $name ($([math]::Round($sw.Elapsed.TotalSeconds,1))s)"; foreach ($e in ($errs | Select-Object -Unique)) { Note "    $e" }
}
function Shot($name) {
  [Windows.Forms.Application]::DoEvents(); Start-Sleep -Milliseconds 300; [Windows.Forms.Application]::DoEvents()
  $bmp = New-Object Drawing.Bitmap $form.Width, $form.Height
  # Real screen capture (shows native dark controls exactly as a user sees them); fall back to DrawToBitmap
  try { $g = [Drawing.Graphics]::FromImage($bmp); $g.CopyFromScreen($form.Location, [Drawing.Point]::Empty, $form.Size); $g.Dispose() }
  catch { $form.DrawToBitmap($bmp, (New-Object Drawing.Rectangle 0, 0, $form.Width, $form.Height)) }
  $bmp.Save((Join-Path $Out "$name.png"), [Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
}
# Every button/checkbox/combobox on the visible tab must be visible and fully inside the window
function Check-Layout($tabName) {
  $client = $form.ClientRectangle; $bad = @()
  $walk = { param($c) foreach ($k in $c.Controls) {
      $scrolls = $false; $pp = $k.Parent; while ($pp) { if ($pp -is [Windows.Forms.ScrollableControl] -and $pp.AutoScroll) { $scrolls = $true }; $pp = $pp.Parent }
      if (($k -is [Windows.Forms.ButtonBase] -or $k -is [Windows.Forms.ComboBox]) -and -not $scrolls) {
        $r = $form.RectangleToClient($k.RectangleToScreen($k.ClientRectangle))
        if (-not $k.Visible) { $script:bad += "'$($k.Text)' is hidden" }
        elseif (-not $client.Contains($r)) { $script:bad += "'$($k.Text)' is outside the window at $r (window $client)" }
        elseif ($r.Width -lt 20 -or $r.Height -lt 15) { $script:bad += "'$($k.Text)' is too small ($r)" }
      }
      & $walk $k } }
  $script:bad = @(); & $walk $tabs.SelectedTab
  if ($script:bad) { foreach ($b in $script:bad) { Write-Error "Layout [$tabName]: $b" } }
}
function Show-Tab($page) { $tabs.SelectedTab = $page; [Windows.Forms.Application]::DoEvents() }

Note "Windows: $((Get-CimInstance Win32_OperatingSystem).Caption) $((Get-CimInstance Win32_OperatingSystem).Version)"
Note "PowerShell: $($PSVersionTable.PSVersion)   DPI scale: $([Windows.Forms.Screen]::PrimaryScreen.Bounds)"

# ---------------------------------------------------------------- load
# Load the app in THIS scope (dot-source) so its functions and controls can be driven
$before = $Error.Count; $sw = [Diagnostics.Stopwatch]::StartNew()
. "$PSScriptRoot\..\app\CleanSweep.ps1" *> "$Out\startup.txt"
$startOut = Get-Content "$Out\startup.txt" -Raw
$sw.Stop()
$loadErrs = @(); if ($Error.Count -gt $before) { $loadErrs = @($Error[0..($Error.Count - $before - 1)] | ForEach-Object { "$_ (line $($_.InvocationInfo.ScriptLineNumber))" }) }
$loadErrs = @($loadErrs | Where-Object { $_ -notmatch '^Not supported|^Invalid namespace|^Invalid class|No MSFT_|No matching MSFT_' })   # optional sensors absent on this PC
if ($startOut -match 'STARTUP ERROR') { $loadErrs += $startOut.Trim() }
if (-not $form) { $loadErrs += "Main window was not created" }
$results.Add([pscustomobject]@{ Step="App loads without errors"; Status=$(if ($loadErrs) { "ISSUES" } else { "PASS" }); Seconds=[math]::Round($sw.Elapsed.TotalSeconds,1); Errors=$loadErrs })
Note "[$(if ($loadErrs) { 'ISSUES' } else { 'PASS' })] App loads ($([math]::Round($sw.Elapsed.TotalSeconds,1))s)"; foreach ($e in $loadErrs) { Note "    $e" }
if (-not $form) { $results | ConvertTo-Json -Depth 4 | Set-Content "$Out\results.json"; exit 1 }
$form.StartPosition = 'Manual'; $form.Location = '0,0'
$form.TopMost = $true; $form.Show(); $form.Activate(); [Windows.Forms.Application]::DoEvents()
Note "Window: $($form.Size)  Title: $($form.Text)  Tabs: $(($tabs.TabPages | ForEach-Object Text) -join ', ')"

# ---------------------------------------------------------------- layout at default and minimum size
foreach ($size in @($form.Size, $form.MinimumSize)) {
  $form.Size = $size; [Windows.Forms.Application]::DoEvents()
  foreach ($p in $tabs.TabPages) {
    Step "Layout: $($p.Text) at $($size.Width)x$($size.Height)" { Show-Tab $p; Check-Layout $p.Text }
    Shot ("layout-{0}x{1}-{2}" -f $size.Width, $size.Height, ($p.Text -replace '[^\w]', ''))
  }
}
$form.Size = '1200,800'

# ---------------------------------------------------------------- dashboard
Show-Tab $dash.Page
Step "Dashboard: live CPU and memory" { Update-Live; Update-Live; Note "    $($dash.CpuL.Text) | $($dash.RamL.Text) | charts: $script:HaveCharts"; if ($dash.CpuL.Text -notmatch '\d+%') { Write-Error "Live CPU not shown" } }
Step "Dashboard: health check" {
  $dash.Run.PerformClick()
  Note "    Score: $($dash.Score.Text) ($($dash.Grade.Text))  $($dash.Status.Text)"
  foreach ($i in $dash.List.Items) { $r = $i.Tag; Note "      [$($r.Status)] $($r.Area): $($r.Finding)$(if ($r.ActionText) { "  -> $($r.ActionText)" })" }
  if ($dash.List.Items.Count -lt 8) { Write-Error "Too few findings ($($dash.List.Items.Count))" }
  $sc = 0; if (-not [int]::TryParse($dash.Score.Text, [ref]$sc) -or $sc -lt 0 -or $sc -gt 100) { Write-Error "Bad score '$($dash.Score.Text)'" }
  $cnc = @(@($dash.List.Items) | Where-Object { $_.Tag.Finding -like 'Could not check*' }); foreach ($c in $cnc) { Note "    (could not check: $($c.Tag.Area))" }
}
Shot "dashboard"
Step "Dashboard: second check records history" { $dash.Run.PerformClick(); $h = Get-History; Note "    history entries: $($h.Count)  trend: $($dash.Trend.Text)"; if ($h.Count -lt 2) { Write-Error "History not saved" } }
Step "Dashboard: select finding shows advice" {
  $it = @($dash.List.Items) | Where-Object { $_.Tag.Action -like 'tab:*' } | Select-Object -First 1
  if (-not $it) { $it = $dash.List.Items[0] }
  if (-not $it) { Write-Error 'No findings to select'; return }
  $it.Selected = $true; [Windows.Forms.Application]::DoEvents(); Note "    $($it.Tag.Area): button '$($dash.Do.Text)' enabled=$($dash.Do.Enabled) advice='$($dash.Advice.Text)'"
  if ($it.Tag.Action -like 'tab:*') { $dash.Do.PerformClick(); Note "    navigated to: $($tabs.SelectedTab.Text)"; if ($tabs.SelectedTab -eq $dash.Page) { Write-Error "Action did not navigate" } }
}
Step "Dashboard: hardware monitor cards" {
  Start-Sleep -Seconds 4; for ($i = 0; $i -lt 4; $i++) { Update-Live; [Windows.Forms.Application]::DoEvents(); Start-Sleep -Milliseconds 1600 }
  foreach ($k in 'cpu','ram','gpu','disk','net','bat') { Note ("    {0}: {1} | {2} | {3} samples" -f $HW[$k].Title.Text, $HW[$k].Value.Text, ($HW[$k].Sub.Text -replace "`n", ' / '), $Spark[$k].Data.Count) }
  Note "    background sampler running: $($HwSync.Run)  samples: $($HwSync.Seq)"
  if ($HW.cpu.Value.Text -eq '--' -or $HW.ram.Value.Text -eq '--') { Write-Error "Hardware cards empty" }
  if (-not $HwSync.Seq) { Write-Error "Background sampler produced no data" }
}
Step "Dashboard: one-click restore point tile" { Invoke-QuickAction 'restore'; Note "    $($dash.Tiles.restore.Sub.Text)" }
Step "Dashboard: one-click network check tile" { Invoke-QuickAction 'network'; Note "    tab: $($tabs.SelectedTab.Text)  $($wifi.Status.Text)"; if ($tabs.SelectedTab -ne $wifi.Page) { Write-Error "Did not open Network" } }
Show-Tab $dash.Page
Step "Dashboard: battery report action" { if (Get-CimInstance Win32_Battery) { Invoke-FindingAction 'battery' } else { Note "    no battery on this VM - skipped" } }
Show-Tab $dash.Page; Shot "dashboard-selected"

# ---------------------------------------------------------------- junk files
Show-Tab $junk.Page
Step "Junk: drive picker lists drives" {
  Note ("    drives: " + (($script:DriveChecks | ForEach-Object Text) -join ' | '))
  if (-not $script:DriveChecks) { throw "No drives listed" }
}
Step "Junk: scan system drive" { $junk.Scan.PerformClick(); Note "    $($junk.Status.Text)"; foreach ($i in $junk.List.Items) { Note "      $($i.Text): $($i.SubItems[1].Text)" } }
Shot "junk-after-scan"
Step "Junk: scan all drives (incl. other drives)" {
  foreach ($c in $script:DriveChecks) { $c.Checked = $true }
  $junk.Scan.PerformClick(); Note "    $($junk.Status.Text)"; foreach ($i in $junk.List.Items) { Note "      $($i.Text): $($i.SubItems[1].Text)" }
}
Shot "junk-all-drives"
Step "Junk: clean" { $junk.Clean.PerformClick(); Note "    $($junk.Status.Text)" }
Shot "junk-after-clean"
Step "Junk: cancel button stops a scan" {
  foreach ($c in $script:DriveChecks) { $c.Checked = $true }
  $script:cancelTimer = New-Object Windows.Forms.Timer; $script:cancelTimer.Interval = 150
  $script:cancelTimer.Add_Tick({ $script:cancelTimer.Stop(); if ($junk.Stop.Enabled) { $junk.Stop.PerformClick() } })
  $script:cancelTimer.Start()
  $junk.Scan.PerformClick(); $script:cancelTimer.Stop(); Note "    $($junk.Status.Text)"
}
foreach ($c in $script:DriveChecks) { $c.Checked = ($c.Tag -eq $SysDrive) }

# ---------------------------------------------------------------- registry
Show-Tab $reg.Page
Step "Registry: scan" { $reg.Scan.PerformClick(); Note "    $($reg.Status.Text)"; foreach ($i in ($reg.List.Items | Select-Object -First 15)) { Note "      $($i.Text) | $($i.SubItems[1].Text) | $($i.SubItems[2].Text)" } }
Shot "registry-after-scan"
Step "Registry: create restore point button" { $mkRp.PerformClick(); Note "    $($reg.Status.Text)" }
Step "Registry: clean (with backup)" {
  # add a known-bad test entry so cleaning always has something to do
  New-Item "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" -Force -ErrorAction SilentlyContinue | Out-Null   # fresh VM profiles have no Run key
  New-ItemProperty "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" -Name "CleanSweepTest" -Value '"C:\NoSuchFolder\missing.exe"' -Force | Out-Null
  $reg.Scan.PerformClick(); Note "    $($reg.Status.Text)"
  $found = @($reg.List.Items | Where-Object { $_.SubItems[1].Text -like "*CleanSweepTest*" })
  if (-not $found) { Write-Error "Test startup entry pointing to a missing exe was not detected" }
  foreach ($i in $reg.List.Items) { $i.Checked = ($i.SubItems[1].Text -like "*CleanSweepTest*") }
  $reg.Clean.PerformClick(); Note "    $($reg.Status.Text)"
  if ((Get-ItemProperty "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" -Name CleanSweepTest -ErrorAction Ignore)) { Write-Error "Test entry was not removed" }
  $bk = Get-ChildItem $backupDir -Filter *.reg -ErrorAction SilentlyContinue | Sort-Object LastWriteTime | Select-Object -Last 1
  if (-not $bk) { Write-Error "No registry backup file was written" } else { Note "    backup: $($bk.Name) ($($bk.Length) bytes)"; if (-not (Select-String -LiteralPath $bk.FullName -Pattern CleanSweepTest -Encoding Unicode -Quiet)) { Write-Error "Backup does not contain the removed entry" } }
}

# ---------------------------------------------------------------- broken shortcuts
Show-Tab $sc.Page
Step "Shortcuts: detects and removes a broken shortcut" {
  $lnk = Join-Path ([Environment]::GetFolderPath('Desktop')) "CleanSweep Test Broken.lnk"
  $s = (New-Object -ComObject WScript.Shell).CreateShortcut($lnk); $s.TargetPath = "C:\NoSuchFolder\gone.exe"; $s.Save()
  $sc.Scan.PerformClick(); Note "    $($sc.Status.Text)"
  $hit = @($sc.List.Items | Where-Object { $_.Text -eq "CleanSweep Test Broken" })
  if (-not $hit) { Write-Error "Broken test shortcut was not detected" }
  foreach ($i in $sc.List.Items) { $i.Checked = ($i.Text -eq "CleanSweep Test Broken") }
  $sc.Clean.PerformClick(); Note "    $($sc.Status.Text)"
  if (Test-Path $lnk) { Write-Error "Broken test shortcut was not removed" }
}
Shot "shortcuts-after"

# ---------------------------------------------------------------- network
Show-Tab $wifi.Page
Step "Network: adapters" { Note "    Wi-Fi: $((Get-WifiAdapter).InterfaceDescription)   Ethernet: $((Get-EthAdapter).InterfaceDescription)   Selected: $script:NetKind"; Get-NetAdapter | ForEach-Object { Note "      $($_.Name) | $($_.InterfaceDescription) | medium $($_.NdisPhysicalMedium) | $($_.Status)" } }
Step "Network: options list is filled" { $n = $wifi.List.Items.Count; Note "    $n options for $script:NetKind"; if ($n -lt 4) { Write-Error "Network options list is empty or incomplete ($n items)" } }
Step "Network: switch to Ethernet" { $connBox.SelectedItem = "Ethernet"; Note "    $($wifiInfo.Text -replace "`n", ' / ')"; Note ("    options: " + (($wifi.List.Items | ForEach-Object Text) -join ', ')) }
Shot "network-ethernet"
Step "Network: test connection" { $wifi.Scan.PerformClick(); Note "    $($wifi.Status.Text)" }
Step "Network: optimize (safe items only on the VM)" {
  foreach ($i in $wifi.List.Items) { $i.Checked = $i.Text -in @("Flush DNS cache", "Clear ARP cache", "Reset TCP auto-tuning to normal") }
  $dnsBox.SelectedIndex = 0
  $wifi.Clean.PerformClick(); Note "    $($wifi.Status.Text)"
}
Step "Network: Ethernet diagnostics" {
  $rows = Invoke-EthDiagnostics
  foreach ($r in $rows) { Note "      [$($r.Status)] $($r.Area): $($r.Result)" }
  if (-not $rows) { Write-Error "Diagnostics returned nothing" }
}
Step "Network: switch to Wi-Fi" { $connBox.SelectedItem = "Wi-Fi"; Note "    $($wifiInfo.Text -replace "`n", ' / ')" }
Shot "network-wifi"

# ---------------------------------------------------------------- drives
Show-Tab $drv.Page
Step "Drives: list volumes with checkboxes" {
  foreach ($i in $drv.List.Items) { Note ("      " + (@($i.Text) + @($i.SubItems | Select-Object -Skip 1 | ForEach-Object Text) -join ' | ') + $(if ($i.Tag.IsSystemPart) { "  [not selectable]" })) }
  if (-not (@($drv.List.Items) | Where-Object { $_.Tag.Letter -eq $SysDrive })) { Write-Error "System drive missing" }
}
Step "Drives: hidden partitions can't be ticked" {
  $h = @($drv.List.Items) | Where-Object { $_.Tag.IsSystemPart } | Select-Object -First 1
  if ($h) { $h.Checked = $true; [Windows.Forms.Application]::DoEvents(); Note "    $($h.Text) checked after click: $($h.Checked)"; if ($h.Checked) { Write-Error "Hidden partition could be ticked" } } else { Note "    no hidden partitions on this VM" }
}
Step "Drives: tick system drive" {
  $c = @($drv.List.Items) | Where-Object { $_.Tag.Letter -eq $SysDrive }; $c.Checked = $true; [Windows.Forms.Application]::DoEvents()
  Note "    checked: $((Get-CheckedDrives | ForEach-Object Letter) -join ',')  buttons enabled: $($drv.Check.Enabled)"; if (-not (Get-CheckedDrives)) { Write-Error "Tick did not register" }
}
Shot "drives"
Step "Drives: check for errors (chkdsk /scan)" {
  # The runner's C: holds millions of files; check the small data drive instead when there is one
  $other = @($drv.List.Items) | Where-Object { $_.Tag.Letter -and $_.Tag.Letter -ne $SysDrive } | Select-Object -First 1
  if ($other) { foreach ($i in $drv.List.Items) { $i.Checked = ($i -eq $other) } }
  $script:RepairAutoCancelSec = 600; $drv.Check.PerformClick(); $script:RepairAutoCancelSec = 0
  Note "    $($drv.Status.Text)"; Note ("    output: " + (($drv.Out.Text -split "`r`n" | Where-Object { $_ } | Select-Object -Last 4) -join ' / '))
  if ($drv.Status.Text -match 'failed|Cancelled') { Write-Error $drv.Status.Text }
}
Step "Drives: optimize" {
  foreach ($i in $drv.List.Items) { $i.Checked = ($i.Tag.Letter -eq $SysDrive) }
  $script:RepairAutoCancelSec = 600; $drv.Opt.PerformClick(); $script:RepairAutoCancelSec = 0
  Note "    $($drv.Status.Text)"; Note ("    output: " + (($drv.Out.Text -split "`r`n" | Where-Object { $_ } | Select-Object -Last 4) -join ' / '))
  if ($drv.Status.Text -match 'failed|Cancelled') { Write-Error $drv.Status.Text }
}
Step "Drives: find large files" {
  # Use the small data drive when there is one (the runner's C: has millions of files)
  $other = @($drv.List.Items) | Where-Object { $_.Tag.Letter -and $_.Tag.Letter -ne $SysDrive -and $_.Tag.Media -notmatch 'USB|Removable' } | Select-Object -First 1
  $root = if ($other) { $other.Tag.Letter + "\CleanSweepTest" } else { $env:TEMP }
  if ($other) { foreach ($i in $drv.List.Items) { $i.Checked = ($i -eq $other) } }
  New-Item -ItemType Directory $root -Force | Out-Null
  $big = "$root\CleanSweepBigTest.bin"; $fs = [IO.File]::Create($big); $fs.SetLength(150MB); $fs.Close()
  $drv.Large.PerformClick(); Note "    $($drv.Status.Text)"
  foreach ($x in ($script:LargeFiles | Sort-Object Size -Descending | Select-Object -First 5)) { Note "      $(Fmt $x.Size)  $($x.Path)" }
  if (-not ($script:LargeFiles | Where-Object Name -eq 'CleanSweepBigTest.bin')) { Write-Error "Test file not found" }
  $lv = $script:LargeForm.Controls | Where-Object { $_ -is [Windows.Forms.ListView] }
  $it = @($lv.Items) | Where-Object { $_.Tag.Name -eq 'CleanSweepBigTest.bin' } | Select-Object -First 1; $it.Checked = $true
  ($script:LargeForm.Controls | Where-Object { $_ -is [Windows.Forms.FlowLayoutPanel] }).Controls[1].PerformClick()
  [Windows.Forms.Application]::DoEvents(); $script:LargeForm.Close()
  Note "    after Recycle Bin: test file exists = $(Test-Path $big)"; if (Test-Path $big) { Write-Error "File not moved"; Remove-Item $big -Force }
}
Step "Drives: Clean junk on ticked drives" { $drv.Junk.PerformClick(); Note "    tab: $($tabs.SelectedTab.Text)  junk drives: $((Get-SelectedDrives) -join ',')  $($junk.Status.Text)" }
Show-Tab $drv.Page; Shot "drives-after"

# ---------------------------------------------------------------- repair
Show-Tab $rep.Page
Step "Repair: tool list" { Note ("    " + ((@($rep.List.Items) | ForEach-Object Text) -join ', ') + "   admin: $(Test-IsAdmin)"); if ($rep.List.Items.Count -lt 5) { Write-Error "Tool list incomplete" } }
Step "Repair: quick Windows health check (DISM /CheckHealth)" {
  Start-Repairs @('dism-check') "Check"; Note "    $($rep.Status.Text)"
  Note ("    output: " + (($rep.Out.Text -split "`r`n" | Where-Object { $_ } | Select-Object -Last 4) -join ' / '))
  if ($rep.Status.Text -match 'Failed') { Write-Error "DISM CheckHealth failed: $($rep.Status.Text)" }
}
Shot "repair-dism"
Step "Repair: cancel stops a running scan" {
  $script:RepairAutoCancelSec = 15; Start-Repairs @('dism-scan') "Scan"; $script:RepairAutoCancelSec = 0
  Note "    $($rep.Status.Text)  busy=$script:RepairBusy  dism still running: $([bool](Get-Process dism -ErrorAction Ignore))"
  if ($rep.Status.Text -ne 'Cancelled.') { Write-Error "Cancel did not work: $($rep.Status.Text)" }
}
Step "Repair: System File Checker (runs 45 s, then cancelled)" {
  $script:RepairAutoCancelSec = 45; Start-Repairs @('sfc') "SFC"; $script:RepairAutoCancelSec = 0
  Note "    $($rep.Status.Text)  progress $($rep.Prog.Value)%"
  Note ("    output: " + (($rep.Out.Text -split "`r`n" | Where-Object { $_ } | Select-Object -Last 5) -join ' / '))
  if ($rep.Status.Text -ne 'Cancelled.' -or (Get-Process sfc -ErrorAction Ignore)) { Write-Error "SFC was not stopped: $($rep.Status.Text)" }
}
Shot "repair-sfc"
Step "Repair: Windows Update repair" {
  Set-Service wuauserv -StartupType Disabled   # simulate a tool that disabled updates
  Start-Repairs @('wu-reset') "WU"; Note "    $($rep.Status.Text)"
  $bk = Get-WuBackups; Note "    backups: $(($bk | ForEach-Object Name) -join ', ')   wuauserv start type: $((Get-Service wuauserv).StartType)"
  if ($bk.Count -lt 1) { Write-Error "No backup folders created" }
  if ((Get-Service wuauserv).StartType -eq 'Disabled') { Write-Error "wuauserv still disabled" }
  if (-not (Test-Path "$env:WINDIR\SoftwareDistribution")) { Note "    (SoftwareDistribution not recreated yet - Windows makes it on next scan)" }
  Note ("    tools now: " + ((@($rep.List.Items) | ForEach-Object Text) -join ', '))
}
Step "Repair: undo Windows Update repair" {
  Start-Repairs @('wu-undo') "Undo"; Note "    $($rep.Status.Text)   wuauserv start type: $((Get-Service wuauserv).StartType)"
  if (-not (Test-Path "$env:WINDIR\SoftwareDistribution")) { Write-Error "SoftwareDistribution not restored" }
  if ((Get-Service wuauserv).StartType -ne 'Disabled') { Write-Error "Start type not restored" }
  Set-Service wuauserv -StartupType Manual
}
Step "Repair: delete backups" {
  Start-Repairs @('wu-reset') "WU"; Start-Repairs @('wu-purge') "Purge"; Note "    $($rep.Status.Text)"
  if ((Get-WuBackups).Count) { Write-Error "Backups remain" }
}
Shot "repair-wu"

# ---------------------------------------------------------------- updates
Show-Tab $upd.Page
Step "Updates: check" { Check-Update $false; Note "    $($upd.Status.Text)" }
Shot "updates"

# ---------------------------------------------------------------- message boxes that would have appeared
Note "`nMessage boxes shown during the run:"; foreach ($m in [CSMsg]::Log) { Note "  $m" }
$form.Close()
$results | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $Out "results.json")
$fail = @($results | Where-Object Status -ne "PASS").Count
Note "`n$($results.Count) steps, $fail with issues"
