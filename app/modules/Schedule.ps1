# CleanSweep - automatic cleanup schedule (Dashboard card + settings window)
# Uses Windows Task Scheduler, so cleanup runs even when CleanSweep is closed. Only junk categories the user
# ticks are cleaned; nothing else on the PC is changed. Turning it off removes the scheduled task completely.

$AcTaskPath = "\CleanSweep\"; $AcTaskName = "Automatic cleanup"
$AppRoot = Split-Path $PSScriptRoot
$AcHistoryFile = Join-Path $AppDir "autoclean-history.csv"
$AcDays = @('Monday','Tuesday','Wednesday','Thursday','Friday','Saturday','Sunday')
$AcFreqs = [ordered]@{ Daily = "Every day"; Weekly = "Every week"; Monthly = "Every 4 weeks" }
# Safe to clean unattended; the rest are offered but unticked by default
$AcDefaultCats = @("User Temp Files", "Windows Temp Files", "Windows Update Cache", "Crash Dumps & Error Reports", "Delivery Optimization")
$AcCatNotes = @{
  "Prefetch Files"  = "not recommended: Windows uses these to start apps faster"
  "Thumbnail Cache" = "folders with pictures open slowly until rebuilt"
  "Chrome Cache"    = "websites load a little slower at first"
  "Edge Cache"      = "websites load a little slower at first"
  "Firefox Cache"   = "websites load a little slower at first"
}

function Get-AutoCleanConfig {
  $c = @{ Enabled = $false; Freq = "Weekly"; Day = "Sunday"; Minutes = 19 * 60; Cats = $AcDefaultCats; RecycleDays = 0
          ACOnly = $true; Idle = $false; CatchUp = $true; Notify = $true }
  if ($settings.AutoClean) { foreach ($p in $settings.AutoClean.PSObject.Properties) { if ($c.ContainsKey($p.Name)) { $c[$p.Name] = $p.Value } } }
  if ($settings.AutoClean -is [hashtable]) { foreach ($k in $settings.AutoClean.Keys) { if ($c.ContainsKey($k)) { $c[$k] = $settings.AutoClean[$k] } } }
  $c.Cats = @($c.Cats | Where-Object { $targets.Contains($_) }); $c.Minutes = [int]$c.Minutes; $c.RecycleDays = [int]$c.RecycleDays
  $c
}
function Save-AutoCleanConfig($cfg) { $settings.AutoClean = [pscustomobject]$cfg; Save-Settings }
function Get-AutoCleanTask { Get-ScheduledTask -TaskPath $AcTaskPath -TaskName $AcTaskName -ErrorAction Ignore }
function Format-AcTime([int]$min) { [datetime]::Today.AddMinutes($min).ToString("h:mm tt") }
function Get-AcDescription($cfg) {
  $at = Format-AcTime $cfg.Minutes
  switch ($cfg.Freq) { 'Daily' { "every day at $at" } 'Weekly' { "every $($cfg.Day) at $at" } default { "every 4 weeks on $($cfg.Day) at $at" } }
}

# Create or update the scheduled task (needs admin rights - CleanSweep already runs elevated)
function Set-AutoCleanSchedule($cfg) {
  $at = [datetime]::Today.AddMinutes($cfg.Minutes)
  $tr = switch ($cfg.Freq) {
    'Daily'   { New-ScheduledTaskTrigger -Daily -At $at }
    'Weekly'  { New-ScheduledTaskTrigger -Weekly -DaysOfWeek $cfg.Day -At $at }
    default   { New-ScheduledTaskTrigger -Weekly -WeeksInterval 4 -DaysOfWeek $cfg.Day -At $at }
  }
  $vbs = Join-Path $AppRoot "AutoClean.vbs"
  $act = New-ScheduledTaskAction -Execute "$env:SystemRoot\System32\wscript.exe" -Argument "//B //Nologo `"$vbs`" Scheduled" -WorkingDirectory $AppRoot
  $set = New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Hours 1) -MultipleInstances IgnoreNew
  $set.StartWhenAvailable = [bool]$cfg.CatchUp
  $set.DisallowStartIfOnBatteries = [bool]$cfg.ACOnly; $set.StopIfGoingOnBatteries = [bool]$cfg.ACOnly
  if ($cfg.Idle) { $set.RunOnlyIfIdle = $true; $set.IdleSettings.IdleDuration = "PT10M"; $set.IdleSettings.WaitTimeout = "PT2H"; $set.IdleSettings.StopOnIdleEnd = $false }
  $who = [Security.Principal.WindowsIdentity]::GetCurrent().Name
  $pr = New-ScheduledTaskPrincipal -UserId $who -LogonType Interactive -RunLevel Highest
  Register-ScheduledTask -TaskPath $AcTaskPath -TaskName $AcTaskName -Action $act -Trigger $tr -Settings $set -Principal $pr -Force -ErrorAction Stop `
    -Description "CleanSweep: removes the junk files chosen in CleanSweep (Dashboard > Automatic cleanup). Turn off in CleanSweep to remove this task." | Out-Null
  $cfg.Enabled = $true; Save-AutoCleanConfig $cfg
}
function Remove-AutoCleanSchedule {
  if (Get-AutoCleanTask) { Unregister-ScheduledTask -TaskPath $AcTaskPath -TaskName $AcTaskName -Confirm:$false -ErrorAction Stop }
  $cfg = Get-AutoCleanConfig; $cfg.Enabled = $false; Save-AutoCleanConfig $cfg
}
function Get-AutoCleanHistory { if (Test-Path $AcHistoryFile) { @(Import-Csv -LiteralPath $AcHistoryFile -ErrorAction Ignore) } else { @() } }

# ---------------------------------------------------------------- Dashboard card
$ac = @{}
$ac.Card = New-Card 'Top' '16,12,16,12'; $ac.Card.Height = 100; $ac.Card.Margin = '0,0,0,0'
$ac.Icon = New-Lbl ([string][char]0xE787) (IconFont 20) $Theme.Accent 'Left' 0; $ac.Icon.Width = 44; $ac.Icon.TextAlign = 'MiddleLeft'
$ac.Text = New-Object Windows.Forms.Panel -Property @{Dock='Fill'; BackColor=[Drawing.Color]::Transparent}
$ac.Title  = New-Lbl "Automatic cleanup" (UiFont 11 'Bold') $Theme.Text 'Top' 26
$ac.Status = New-Lbl "Off" (UiFont 9.5) $Theme.Sub 'Top' 22
$ac.Last   = New-Lbl "" (UiFont 9.5) $Theme.Sub 'Top' 22
Add-Rows $ac.Text @($ac.Title, $ac.Status, $ac.Last)
$ac.Btns = New-Object Windows.Forms.FlowLayoutPanel -Property @{Dock='Right'; AutoSize=$true; AutoSizeMode='GrowAndShrink'; WrapContents=$false; Padding='0,18,0,0'; BackColor=[Drawing.Color]::Transparent}
$ac.Run = New-Object Windows.Forms.Button -Property @{Text="Run now"; AutoSize=$true; MinimumSize='100,34'; Margin='0,0,8,0'}
$ac.Set = New-Object Windows.Forms.Button -Property @{Text="Set up schedule"; AutoSize=$true; MinimumSize='140,34'; Margin='0,0,0,0'}
Set-Primary $ac.Set
$ac.Btns.Controls.AddRange(@($ac.Run, $ac.Set))
$ac.Card.Controls.Add($ac.Text); $ac.Card.Controls.Add($ac.Icon); $ac.Card.Controls.Add($ac.Btns)
# Place it under the health score / one-click actions row
# (Dock=Top lays out the highest child index first, so a lower index than $row1 means "below it")
$ac.Host = New-Object Windows.Forms.Panel -Property @{Dock='Top'; Height=$ac.Card.Height; Padding='0,0,12,0'}   # same right edge as the cards above
$ac.Card.Dock = 'Fill'; $ac.Host.Controls.Add($ac.Card)
$scroll.Controls.Add($ac.Host); $scroll.Controls.SetChildIndex($ac.Host, $scroll.Controls.GetChildIndex($row1))
$acGap = Spacer 4; $scroll.Controls.Add($acGap); $scroll.Controls.SetChildIndex($acGap, $scroll.Controls.GetChildIndex($ac.Host))

# Health check entry (Info only - it never lowers the score)
$HealthChecks["Automatic cleanup"] = {
  $t = Get-AutoCleanTask
  if ($t -and $t.State -ne 'Disabled') { New-Finding "Automatic cleanup" "OK" ("On - " + (Get-AcDescription (Get-AutoCleanConfig))) }
  else { New-Finding "Automatic cleanup" "Info" "Off - junk builds up until you clean it" "Schedule a weekly cleanup of temp files and update leftovers. It runs in the background and skips anything recent or in use." "schedule" "Set up schedule" }
}

function Update-AutoCleanCard {
  $cfg = Get-AutoCleanConfig; $task = Get-AutoCleanTask
  if ($task -and $task.State -ne 'Disabled') {
    $next = try { (Get-ScheduledTaskInfo -InputObject $task -ErrorAction Stop).NextRunTime } catch { $null }
    $ac.Status.Text = "On - " + (Get-AcDescription $cfg) + $(if ($next) { "  " + [char]0xB7 + "  next run " + $next.ToString("ddd MMM d, h:mm tt") } else { "" })
    $ac.Status.ForeColor = $Theme.Text; $ac.Icon.ForeColor = $Theme.Ok; $ac.Set.Text = "Change schedule"
  } else {
    $ac.Status.Text = "Off - CleanSweep only cleans when you ask it to."
    $ac.Status.ForeColor = $Theme.Sub; $ac.Icon.ForeColor = $Theme.Accent; $ac.Set.Text = "Set up schedule"
  }
  $h = Get-AutoCleanHistory | Select-Object -Last 1
  $ac.Last.Text = if ($h) {
    $when = try { ([datetime]$h.Time).ToString("MMM d, h:mm tt") } catch { $h.Time }
    $how = if ($h.Trigger -eq 'Scheduled') { "Last automatic run" } else { "Last run" }
    "$how $when - freed $(Fmt ([long]$h.Freed)) ($($h.Files) files)"
  } else { "Removes temp files and update leftovers in the background, even when CleanSweep is closed." }
}

# Run the cleanup now with the saved choices (same code the schedule uses)
function Start-AutoCleanNow {
  if ($script:AcProc -and -not $script:AcProc.HasExited) { return }
  if (-not $settings.AutoClean) { Save-AutoCleanConfig (Get-AutoCleanConfig) }
  $ps = "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe"
  $script:AcProc = Start-Process $ps -WindowStyle Hidden -PassThru -ArgumentList "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File `"$(Join-Path $AppRoot 'AutoClean.ps1')`" -Trigger Manual"
  $ac.Run.Enabled = $false; $ac.Run.Text = "Cleaning..."; $ac.Last.Text = "Cleaning junk files in the background..."
  if ($TestMode) { [void]$script:AcProc.WaitForExit(600000); Complete-AutoCleanRun; return }
  $script:AcTimer.Start()
}
function Complete-AutoCleanRun { $script:AcTimer.Stop(); $ac.Run.Enabled = $true; $ac.Run.Text = "Run now"; Update-AutoCleanCard }
$script:AcTimer = New-Object Windows.Forms.Timer -Property @{Interval=1000}
$script:AcTimer.Add_Tick({ if ($script:AcProc -and $script:AcProc.HasExited) { Complete-AutoCleanRun } })
$ac.Run.Add_Click({ Start-AutoCleanNow })
$ac.Set.Add_Click({ Show-AutoCleanDialog })

# ---------------------------------------------------------------- settings window
function Show-AutoCleanDialog {
  $cfg = Get-AutoCleanConfig; $task = Get-AutoCleanTask
  $f = New-Object Windows.Forms.Form -Property @{Text="Automatic cleanup"; Size='600,700'; MinimumSize='560,560'; StartPosition='CenterParent'; Font=$form.Font; Icon=$form.Icon; MaximizeBox=$false; MinimizeBox=$false; ShowInTaskbar=$false}
  $body = New-Object Windows.Forms.FlowLayoutPanel -Property @{Dock='Fill'; FlowDirection='TopDown'; WrapContents=$false; AutoScroll=$true; Padding='20,16,20,8'}
  $lbl = { param($t, $font, $col, $top = 0) New-Object Windows.Forms.Label -Property @{Text=$t; Font=$font; ForeColor=$col; AutoSize=$true; MaximumSize='520,0'; Margin="0,$top,0,4"} }
  $body.Controls.Add((& $lbl "Automatic cleanup" (DisplayFont 15 'Bold') $Theme.Text))
  $body.Controls.Add((& $lbl "CleanSweep removes the junk you tick below on a schedule, even when the app is closed. Files changed in the last 24 hours and files in use are always skipped. Nothing else on your PC is changed." (UiFont 9.5) $Theme.Sub))
  $on = New-Object Windows.Forms.CheckBox -Property @{Text="Clean junk files automatically"; AutoSize=$true; Margin='0,10,0,6'; Font=(UiFont 10 'Bold'); Checked=([bool]$task -or -not $settings.AutoClean)}
  $body.Controls.Add($on)

  $when = New-Object Windows.Forms.FlowLayoutPanel -Property @{AutoSize=$true; WrapContents=$false; Margin='22,0,0,4'}
  $mk = { param($items, $w) $c = New-Object Windows.Forms.ComboBox -Property @{DropDownStyle='DropDownList'; Width=$w; Margin='0,0,10,0'}; foreach ($i in $items) { [void]$c.Items.Add($i) }; $c }
  $freq = & $mk @($AcFreqs.Values) 130; $freq.SelectedIndex = [math]::Max(0, @($AcFreqs.Keys).IndexOf($cfg.Freq))
  $onL = New-Object Windows.Forms.Label -Property @{Text="on"; AutoSize=$true; Margin='0,4,6,0'; ForeColor=$Theme.Sub}
  $day = & $mk $AcDays 120; $day.SelectedIndex = [math]::Max(0, $AcDays.IndexOf([string]$cfg.Day))
  $atL = New-Object Windows.Forms.Label -Property @{Text="at"; AutoSize=$true; Margin='0,4,6,0'; ForeColor=$Theme.Sub}
  $times = foreach ($m in (0..47 | ForEach-Object { $_ * 30 })) { Format-AcTime $m }
  $time = & $mk $times 110; $time.SelectedIndex = [math]::Min(47, [int]($cfg.Minutes / 30))
  $when.Controls.AddRange(@($freq, $onL, $day, $atL, $time))
  $body.Controls.Add($when)
  $sync = { $d = $freq.SelectedIndex -ne 0; $day.Visible = $d; $onL.Visible = $d }.GetNewClosure()
  $freq.Add_SelectedIndexChanged($sync); & $sync

  $body.Controls.Add((& $lbl "What to clean" (UiFont 10.5 'Bold') $Theme.Text 12))
  $catBoxes = @()
  foreach ($k in $targets.Keys) {
    $t = if ($AcCatNotes[$k]) { "$k  ($($AcCatNotes[$k]))" } else { $k }
    $cb = New-Object Windows.Forms.CheckBox -Property @{Text=$t; Tag=$k; AutoSize=$true; Margin='22,2,0,2'; Checked=($cfg.Cats -contains $k)}
    $body.Controls.Add($cb); $catBoxes += $cb
  }
  $rb = New-Object Windows.Forms.FlowLayoutPanel -Property @{AutoSize=$true; WrapContents=$false; Margin='22,2,0,2'}
  $rcb = New-Object Windows.Forms.CheckBox -Property @{Text="Recycle Bin items deleted more than"; AutoSize=$true; Margin='0,3,6,0'; Checked=($cfg.RecycleDays -gt 0)}
  $rdays = & $mk @('7','14','30','60','90') 64; $rdays.SelectedItem = [string]$(if ($cfg.RecycleDays -gt 0) { $cfg.RecycleDays } else { 30 }); if ($rdays.SelectedIndex -lt 0) { $rdays.SelectedIndex = 2 }
  $rl = New-Object Windows.Forms.Label -Property @{Text="days ago"; AutoSize=$true; Margin='4,5,0,0'; ForeColor=$Theme.Text}
  $rb.Controls.AddRange(@($rcb, $rdays, $rl)); $body.Controls.Add($rb)

  $body.Controls.Add((& $lbl "When to run" (UiFont 10.5 'Bold') $Theme.Text 12))
  $ac1 = New-Object Windows.Forms.CheckBox -Property @{Text="Only when plugged in (laptops)"; AutoSize=$true; Margin='22,2,0,2'; Checked=[bool]$cfg.ACOnly}
  $ac2 = New-Object Windows.Forms.CheckBox -Property @{Text="Only when the PC has been idle for 10 minutes"; AutoSize=$true; Margin='22,2,0,2'; Checked=[bool]$cfg.Idle}
  $ac3 = New-Object Windows.Forms.CheckBox -Property @{Text="If the PC was off at that time, run as soon as possible"; AutoSize=$true; Margin='22,2,0,2'; Checked=[bool]$cfg.CatchUp}
  $ac4 = New-Object Windows.Forms.CheckBox -Property @{Text="Show a notification with the result"; AutoSize=$true; Margin='22,2,0,2'; Checked=[bool]$cfg.Notify}
  $body.Controls.AddRange(@($ac1, $ac2, $ac3, $ac4))
  $tip = & $lbl "" (UiFont 9) $Theme.Warn 8; $body.Controls.Add($tip)

  $bar = New-Object Windows.Forms.FlowLayoutPanel -Property @{Dock='Bottom'; Height=58; FlowDirection='RightToLeft'; Padding='16,10,16,10'; BackColor=$Theme.Side}
  $save = New-Object Windows.Forms.Button -Property @{Text="Save"; AutoSize=$true; MinimumSize='110,34'; Margin='8,0,0,0'}
  $cancel = New-Object Windows.Forms.Button -Property @{Text="Cancel"; AutoSize=$true; MinimumSize='100,34'; Margin='8,0,0,0'; DialogResult='Cancel'}
  Set-Primary $save; $bar.Controls.AddRange(@($save, $cancel)); $f.CancelButton = $cancel
  $f.Controls.Add($body); $f.Controls.Add($bar)
  $toggle = { $en = $on.Checked; foreach ($c in @($when, $rb, $ac1, $ac2, $ac3, $ac4) + $catBoxes) { $c.Enabled = $en } }.GetNewClosure()
  $on.Add_CheckedChanged($toggle); & $toggle

  $script:AcUI = @{ Form=$f; On=$on; Freq=$freq; Day=$day; Time=$time; Cats=$catBoxes; Recycle=$rcb; RecycleDays=$rdays; ACOnly=$ac1; Idle=$ac2; CatchUp=$ac3; Notify=$ac4; Tip=$tip; Save=$save }
  $save.Add_Click({ Save-AutoCleanDialog })
  Use-DarkDialog $f
  if ($TestMode) { $f.Show($form); [Windows.Forms.Application]::DoEvents() } else { [void]$f.ShowDialog($form); $f.Dispose() }
}
function Save-AutoCleanDialog {
  $u = $script:AcUI
  $cfg = Get-AutoCleanConfig
  $cfg.Freq = @($AcFreqs.Keys)[$u.Freq.SelectedIndex]; $cfg.Day = [string]$u.Day.SelectedItem; $cfg.Minutes = $u.Time.SelectedIndex * 30
  $cfg.Cats = @($u.Cats | Where-Object Checked | ForEach-Object Tag)
  $cfg.RecycleDays = if ($u.Recycle.Checked) { [int]$u.RecycleDays.SelectedItem } else { 0 }
  $cfg.ACOnly = $u.ACOnly.Checked; $cfg.Idle = $u.Idle.Checked; $cfg.CatchUp = $u.CatchUp.Checked; $cfg.Notify = $u.Notify.Checked
  try {
    if ($u.On.Checked) {
      if (-not $cfg.Cats -and -not $cfg.RecycleDays) { $u.Tip.Text = "Tick at least one thing to clean."; return }
      Set-AutoCleanSchedule $cfg
    } else { Save-AutoCleanConfig $cfg; Remove-AutoCleanSchedule }
  } catch { $u.Tip.Text = "Windows could not save the schedule: $($_.Exception.Message)"; return }
  Update-AutoCleanCard
  $u.Form.DialogResult = 'OK'; $u.Form.Close()
}

Update-AutoCleanCard
