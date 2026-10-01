# ================================================================ DASHBOARD: health score, recommendations, live stats, history
# Report-first: checks only read the system. Nothing is changed unless you click a recommendation's action,
# and actions only open the right CleanSweep tab or the matching Windows Settings page.

$HistoryFile = Join-Path $AppDir "history.csv"
$script:Findings = @()

# ---------------------------------------------------------------- checks
function New-Finding($area, $status, $finding, $advice = "", $action = "", $actionText = "") {
  [pscustomobject]@{ Area=$area; Status=$status; Finding=$finding; Advice=$advice; Action=$action; ActionText=$actionText }
}
function Get-FolderSize($paths) {
  $sum = 0; foreach ($p in $paths) { if (Test-Path -LiteralPath $p) { $sum += [double]((Get-ChildItem -LiteralPath $p -Recurse -Force -File -ErrorAction Ignore | Measure-Object Length -Sum).Sum) } }; $sum
}

# Each check is independent: if one fails, it reports "could not check" and the rest still run
$HealthChecks = [ordered]@{

  "Storage" = {
    foreach ($d in (Get-CimInstance Win32_LogicalDisk -Filter "DriveType=3" -ErrorAction Stop | Where-Object { $_.Size -gt 0 })) {
      $pct = [math]::Round($d.FreeSpace / $d.Size * 100); $free = Fmt $d.FreeSpace
      $sys = ($d.DeviceID -eq $SysDrive)
      if (($sys -and $d.FreeSpace -lt 10GB) -or $pct -lt 10) {
        New-Finding "Storage" "Problem" "$($d.DeviceID) is almost full: $free free ($pct%)" "Windows slows down and updates can fail when the system drive is nearly full. Clean junk files, then use Find large files on the Drives tab to spot big files you don't need." "tab:drives" "Free up space" }
      elseif ($pct -lt 20) {
        New-Finding "Storage" "Warning" "$($d.DeviceID) is getting full: $free free ($pct%)" "Keeping at least 15-20% free helps performance and leaves room for updates." "tab:junk" "Clean junk files" }
      else { New-Finding "Storage" "OK" "$($d.DeviceID) $free free ($pct%)" }
    }
  }

  "Drive health" = {
    $disks = @(Get-PhysicalDisk -ErrorAction Stop)
    if (-not $disks) { New-Finding "Drive health" "Info" "No drive health data available"; return }
    foreach ($pd in $disks) {
      $mt = if ($pd.MediaType -in 'SSD','HDD') { "$($pd.MediaType), " } else { "" }
      $name = "$($pd.FriendlyName) ($mt$([math]::Round($pd.Size / 1GB)) GB)"
      $rc = $null; try { $rc = $pd | Get-StorageReliabilityCounter -ErrorAction Stop } catch {}
      $bits = @()
      if ($rc) {
        if ($null -ne $rc.Wear -and $pd.MediaType -eq 'SSD') { $bits += "$($rc.Wear)% worn" }
        if ($rc.Temperature) { $bits += "$($rc.Temperature) C" }
        if ($rc.PowerOnHours) { $bits += "{0:N0} hours on" -f $rc.PowerOnHours }
      }
      $info = if ($bits) { " - " + ($bits -join ", ") } else { "" }
      if ($pd.HealthStatus -ne 'Healthy') {
        New-Finding "Drive health" "Problem" "$name reports $($pd.HealthStatus)$info" "The drive itself is reporting a problem. Back up your important files now, then check the maker's support tool or plan a replacement." "settings:ms-settings:backup" "Open backup settings" }
      elseif ($rc -and $rc.ReadErrorsUncorrected -gt 0) {
        New-Finding "Drive health" "Problem" "$name has $($rc.ReadErrorsUncorrected) unreadable-data errors$info" "Uncorrected read errors often come before a drive fails. Back up your files now." "settings:ms-settings:backup" "Open backup settings" }
      elseif ($rc -and $rc.Wear -ge 90) {
        New-Finding "Drive health" "Problem" "$name is near the end of its rated life$info" "The SSD has used most of its rated write endurance. Back up and plan a replacement." "settings:ms-settings:backup" "Open backup settings" }
      elseif ($rc -and $rc.Wear -ge 70) {
        New-Finding "Drive health" "Warning" "$name is wearing out$info" "The SSD has used over 70% of its rated life. Make sure backups are up to date." "settings:ms-settings:backup" "Open backup settings" }
      elseif ($rc -and $rc.Temperature -gt 70) {
        New-Finding "Drive health" "Warning" "$name is running hot$info" "Above 70 C the drive slows itself down. Keep the laptop's air vents clear and avoid soft surfaces under it." }
      else { New-Finding "Drive health" "OK" "$name is healthy$info" }
    }
  }

  "Memory" = {
    $os = Get-CimInstance Win32_OperatingSystem -ErrorAction Stop
    $total = [double]$os.TotalVisibleMemorySize * 1KB; $free = [double]$os.FreePhysicalMemory * 1KB
    $pct = [math]::Round(($total - $free) / $total * 100)
    if ($pct -ge 90) { New-Finding "Memory" "Warning" "Memory is $pct% in use right now ($(Fmt $total) installed)" "Close apps or browser tabs you aren't using. Task Manager shows what is using the most memory. (CleanSweep does not force-clear RAM - that only makes Windows slower.)" "run:taskmgr.exe" "Open Task Manager" }
    elseif ($total -lt 7.5GB) { New-Finding "Memory" "Info" "$(Fmt $total) installed, $pct% in use" "8 GB or more makes multitasking much smoother on Windows 11, if your laptop can be upgraded." }
    else { New-Finding "Memory" "OK" "$pct% in use of $(Fmt $total)" }
  }

  "Startup apps" = {
    $n = 0
    $approved = @{}
    foreach ($k in 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run','HKLM:\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run','HKLM:\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run32','HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder') {
      $it = Get-Item $k -ErrorAction Ignore; if ($it) { foreach ($v in $it.GetValueNames()) { $b = $it.GetValue($v); if ($b -is [byte[]] -and $b.Length) { $approved[$v] = (($b[0] -band 1) -eq 0) } } }
    }
    foreach ($k in 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run','HKLM:\Software\Microsoft\Windows\CurrentVersion\Run','HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run') {
      $it = Get-Item $k -ErrorAction Ignore; if ($it) { foreach ($v in $it.GetValueNames()) { if ($v -and ($approved[$v] -ne $false)) { $n++ } } }
    }
    foreach ($f in [Environment]::GetFolderPath('Startup'), [Environment]::GetFolderPath('CommonStartup')) {
      Get-ChildItem -LiteralPath $f -File -ErrorAction Ignore | Where-Object Name -ne 'desktop.ini' | ForEach-Object { if ($approved[$_.Name] -ne $false) { $n++ } }
    }
    if ($n -gt 15) { New-Finding "Startup apps" "Warning" "$n apps start with Windows" "Many startup apps slow down sign-in. Turn off the ones you don't need right away (you can still open them normally)." "settings:ms-settings:startupapps" "Open Startup apps" }
    else { New-Finding "Startup apps" "OK" "$n apps start with Windows" }
  }

  "Security" = {
    $mp = $null; try { $mp = Get-MpComputerStatus -ErrorAction Stop } catch {}
    $thirdParty = @(Get-CimInstance -Namespace root\SecurityCenter2 -ClassName AntiVirusProduct -ErrorAction Ignore | Where-Object { $_.displayName -notmatch 'Windows Defender|Microsoft Defender' })
    if ($thirdParty) { New-Finding "Security" "OK" "Antivirus: $($thirdParty[0].displayName)" }
    elseif ($mp) {
      if (-not $mp.RealTimeProtectionEnabled) { New-Finding "Security" "Problem" "Microsoft Defender real-time protection is off" "Turn real-time protection back on unless you use another antivirus." "settings:windowsdefender:" "Open Windows Security" }
      elseif ($mp.AntivirusSignatureAge -gt 7) { New-Finding "Security" "Warning" "Virus definitions are $($mp.AntivirusSignatureAge) days old" "Run Windows Update or check for protection updates in Windows Security." "settings:windowsdefender:" "Open Windows Security" }
      else { New-Finding "Security" "OK" "Microsoft Defender is on and up to date" }
    } else { New-Finding "Security" "Warning" "No antivirus status found" "Check Windows Security to make sure you are protected." "settings:windowsdefender:" "Open Windows Security" }
    $off = @(Get-NetFirewallProfile -ErrorAction Ignore | Where-Object { -not $_.Enabled } | ForEach-Object Name)
    if ($off) { New-Finding "Security" "Problem" "Firewall is off for: $($off -join ', ')" "Turn the firewall back on unless another security app manages it." "settings:windowsdefender:" "Open Windows Security" }
    else { New-Finding "Security" "OK" "Firewall is on" }
    $lua = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System' -ErrorAction Ignore).EnableLUA
    if ($lua -eq 0) { New-Finding "Security" "Problem" "User Account Control is turned off" "UAC stops apps from making system changes without asking. Turn it back on in Control Panel > User Accounts." "run:UserAccountControlSettings.exe" "Open UAC settings" }
  }

  "Windows Update" = {
    $last = $null; try { $last = (New-Object -ComObject Microsoft.Update.AutoUpdate).Results.LastInstallationSuccessDate } catch {}
    if ($last -and $last.Year -gt 2000) {
      $days = [int]((Get-Date) - $last).TotalDays
      if ($days -gt 45) { New-Finding "Windows Update" "Warning" "Updates last installed $days days ago" "Security fixes come out monthly. Check Windows Update; if updates keep failing, use Repair Windows Update on the Repair tab." "settings:ms-settings:windowsupdate" "Open Windows Update" }
      else { New-Finding "Windows Update" "OK" "Updates installed $days days ago" }
    } else { New-Finding "Windows Update" "Info" "Last update date not available" "" "settings:ms-settings:windowsupdate" "Open Windows Update" }
    $pending = (Test-Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending') -or (Test-Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired')
    if ($pending) { New-Finding "Windows Update" "Warning" "A restart is needed to finish installing updates" "Restart when convenient so updates can complete." }
  }

  "Uptime" = {
    $boot = (Get-CimInstance Win32_OperatingSystem -ErrorAction Stop).LastBootUpTime
    $d = [int]((Get-Date) - $boot).TotalDays
    if ($d -ge 14) { New-Finding "Uptime" "Warning" "Not restarted for $d days" "A restart clears memory leaks and finishes pending updates. (Shut down with Fast Startup doesn't count as a restart.)" }
    else { New-Finding "Uptime" "OK" "Restarted $d day(s) ago" }
  }

  "Junk files" = {
    $sz = Get-FolderSize @($env:TEMP, "$env:WINDIR\Temp", "$env:WINDIR\SoftwareDistribution\Download")
    if ($sz -gt 2GB) { New-Finding "Junk files" "Warning" "About $(Fmt $sz) of temporary files" "These are safe to remove with the Junk Files tab." "tab:junk" "Clean junk files" }
    else { New-Finding "Junk files" "OK" "About $(Fmt $sz) of temporary files" }
  }

  "Battery" = {
    if (-not (Get-CimInstance Win32_Battery -ErrorAction Ignore)) { return }   # desktops: no finding
    $full = (Get-CimInstance -Namespace root\wmi -ClassName BatteryFullChargedCapacity -ErrorAction Ignore | Select-Object -First 1).FullChargedCapacity
    $design = (Get-CimInstance -Namespace root\wmi -ClassName BatteryStaticData -ErrorAction Ignore | Select-Object -First 1).DesignedCapacity
    $cycles = (Get-CimInstance -Namespace root\wmi -ClassName BatteryCycleCount -ErrorAction Ignore | Select-Object -First 1).CycleCount
    if ($full -and $design) {
      $h = [math]::Round($full / $design * 100); $c = if ($cycles) { ", $cycles charge cycles" } else { "" }
      if ($h -lt 60) { New-Finding "Battery" "Warning" "Battery holds $h% of its original charge$c" "The battery is well worn. A replacement will restore battery life; the detailed report shows the trend." "battery" "Open battery report" }
      else { New-Finding "Battery" "OK" "Battery holds $h% of its original charge$c" "" "battery" "Open battery report" }
    } else { New-Finding "Battery" "Info" "Battery capacity not reported" "" "battery" "Open battery report" }
  }

  "Devices" = {
    $bad = @(Get-CimInstance Win32_PnPEntity -ErrorAction Stop | Where-Object { $_.ConfigManagerErrorCode -notin 0, 22, 45 })
    if ($bad) { New-Finding "Devices" "Warning" "$($bad.Count) device(s) not working: $(($bad | Select-Object -First 3 | ForEach-Object { if ($_.Name) { $_.Name } elseif ($_.Description) { $_.Description } else { $_.PNPDeviceID } }) -join '; ')" "Usually a missing or broken driver. Device Manager shows the error; Windows Update > Optional updates may have the driver." "run:devmgmt.msc" "Open Device Manager" }
    else { New-Finding "Devices" "OK" "All devices are working" }
  }

  "Stability" = {
    $since7 = (Get-Date).AddDays(-7); $since30 = (Get-Date).AddDays(-30)
    $bsod = @(Get-WinEvent -FilterHashtable @{LogName='System'; ProviderName='Microsoft-Windows-WER-SystemErrorReporting'; Id=1001; StartTime=$since30} -ErrorAction Ignore).Count
    $power = @(Get-WinEvent -FilterHashtable @{LogName='System'; ProviderName='Microsoft-Windows-Kernel-Power'; Id=41; StartTime=$since30} -ErrorAction Ignore).Count
    $apps = @(Get-WinEvent -FilterHashtable @{LogName='Application'; ProviderName='Application Error'; Id=1000; StartTime=$since7} -ErrorAction Ignore)
    if ($bsod) { New-Finding "Stability" "Problem" "$bsod blue-screen crash(es) in the last 30 days" "Repeated blue screens usually point to a driver or hardware problem. Note the stop code in Reliability Monitor and update the related driver. If Windows files are damaged, the Recommended repair on the Repair tab can fix them." "tab:repair" "Open Repair tools" }
    elseif ($power) { New-Finding "Stability" "Warning" "$power unexpected shutdown(s) in the last 30 days" "The PC lost power or froze. If you didn't hold the power button, check Reliability Monitor." "run:perfmon.exe /rel" "Open Reliability Monitor" }
    else { New-Finding "Stability" "OK" "No crashes or unexpected shutdowns in 30 days" }
    if ($apps.Count -ge 5) {
      $top = $apps | ForEach-Object { if ($_.Message -match 'Faulting application name:\s*([^,\r\n]+)') { $matches[1] } } | Group-Object | Sort-Object Count -Descending | Select-Object -First 2
      New-Finding "Stability" "Warning" "$($apps.Count) app crashes this week (mostly $(($top | ForEach-Object { "$($_.Name) x$($_.Count)" }) -join ', '))" "Update or reinstall the app that keeps crashing." "run:perfmon.exe /rel" "Open Reliability Monitor" }
  }

  "Internet" = {
    $ms = Get-PingMs 1.1.1.1; if ($null -eq $ms) { $ms = Get-PingMs 1.1.1.1 -Tcp }
    if ($null -eq $ms) { New-Finding "Internet" "Problem" "No internet connection" "Check Wi-Fi or the cable, then use the Network Optimizer tab." "tab:net" "Open Network Optimizer" }
    elseif ($ms -gt 100) { New-Finding "Internet" "Warning" "Slow response: $ms ms" "Run a connection test in the Network Optimizer tab." "tab:net" "Open Network Optimizer" }
    else { New-Finding "Internet" "OK" "Connected ($ms ms)" }
  }
}

# 100 minus 15 per problem and 5 per warning (never below 0)
function Get-HealthScore($rows) {
  $p = @($rows | Where-Object Status -eq 'Problem').Count; $w = @($rows | Where-Object Status -eq 'Warning').Count
  [math]::Max(0, 100 - 15 * $p - 5 * $w)
}
function Get-Grade($s) { if ($s -ge 90) { "Excellent" } elseif ($s -ge 75) { "Good" } elseif ($s -ge 50) { "Fair" } else { "Needs attention" } }
function Get-GradeColor($s) { if ($s -ge 90) { 'ForestGreen' } elseif ($s -ge 75) { 'SeaGreen' } elseif ($s -ge 50) { 'DarkOrange' } else { 'Firebrick' } }

function Invoke-HealthCheck {
  $rows = New-Object System.Collections.Generic.List[object]
  foreach ($name in $HealthChecks.Keys) {
    $dash.Status.Text = "Checking $($name.ToLower())..."; [Windows.Forms.Application]::DoEvents()
    try { foreach ($r in @(& $HealthChecks[$name])) { if ($r) { $rows.Add($r) } } }
    catch { $rows.Add((New-Finding $name "Info" "Could not check ($($_.Exception.Message))")) }
  }
  return $rows
}

# ---------------------------------------------------------------- history
function Save-History($score, $rows) {
  try {
    New-Item -ItemType Directory -Path $AppDir -Force | Out-Null
    $c = Get-CimInstance Win32_LogicalDisk -Filter "DeviceID='$SysDrive'" -ErrorAction Ignore
    [pscustomobject]@{ Date=(Get-Date).ToString("s"); Score=$score
      Problems=@($rows | Where-Object Status -eq 'Problem').Count; Warnings=@($rows | Where-Object Status -eq 'Warning').Count
      SystemFreeGB=[math]::Round($c.FreeSpace / 1GB, 1) } | Export-Csv -LiteralPath $HistoryFile -Append -NoTypeInformation -Encoding UTF8
  } catch {}
}
function Get-History { if (Test-Path $HistoryFile) { @(Import-Csv -LiteralPath $HistoryFile -ErrorAction Ignore) } else { @() } }

# ---------------------------------------------------------------- UI
$dash = @{}
$dash.Page = New-Object Windows.Forms.TabPage -Property @{Text="Dashboard"; Padding='10,10,10,10'}

# Top: score on the left, live stats and charts on the right
$dash.Top = New-Object Windows.Forms.TableLayoutPanel -Property @{Dock='Top'; Height=150; ColumnCount=2; RowCount=1}
[void]$dash.Top.ColumnStyles.Add((New-Object Windows.Forms.ColumnStyle 'Absolute', 210))
[void]$dash.Top.ColumnStyles.Add((New-Object Windows.Forms.ColumnStyle 'Percent', 100))
$scoreBox = New-Object Windows.Forms.Panel -Property @{Dock='Fill'}
$dash.Score = New-Object Windows.Forms.Label -Property @{Dock='Top'; Height=74; Text="--"; TextAlign='MiddleCenter'; Font=New-Object Drawing.Font("Segoe UI",36,[Drawing.FontStyle]::Bold); ForeColor='DimGray'}
$dash.Grade = New-Object Windows.Forms.Label -Property @{Dock='Top'; Height=28; Text="Health score"; TextAlign='MiddleCenter'; Font=New-Object Drawing.Font("Segoe UI",12,[Drawing.FontStyle]::Bold)}
$dash.Trend = New-Object Windows.Forms.Label -Property @{Dock='Fill'; Text=""; TextAlign='TopCenter'; ForeColor='DimGray'}
$scoreBox.Controls.AddRange(@($dash.Trend, $dash.Grade, $dash.Score))

$right = New-Object Windows.Forms.TableLayoutPanel -Property @{Dock='Fill'; ColumnCount=3; RowCount=2}
foreach ($i in 1..3) { [void]$right.ColumnStyles.Add((New-Object Windows.Forms.ColumnStyle 'Percent', 33.3)) }
[void]$right.RowStyles.Add((New-Object Windows.Forms.RowStyle 'Absolute', 24)); [void]$right.RowStyles.Add((New-Object Windows.Forms.RowStyle 'Percent', 100))
$dash.CpuL = New-Object Windows.Forms.Label -Property @{Dock='Fill'; Text="CPU"; TextAlign='MiddleLeft'}
$dash.RamL = New-Object Windows.Forms.Label -Property @{Dock='Fill'; Text="Memory"; TextAlign='MiddleLeft'}
$dash.HisL = New-Object Windows.Forms.Label -Property @{Dock='Fill'; Text="Score history"; TextAlign='MiddleLeft'}
$right.Controls.Add($dash.CpuL, 0, 0); $right.Controls.Add($dash.RamL, 1, 0); $right.Controls.Add($dash.HisL, 2, 0)

# Small line charts (falls back to text only if the chart library is unavailable)
$script:HaveCharts = $true
try { Add-Type -AssemblyName System.Windows.Forms.DataVisualization -ErrorAction Stop } catch { $script:HaveCharts = $false }
function New-MiniChart($color, $max) {
  if (-not $script:HaveCharts) { return (New-Object Windows.Forms.Label -Property @{Dock='Fill'}) }
  $c = New-Object System.Windows.Forms.DataVisualization.Charting.Chart -Property @{Dock='Fill'; BackColor='Transparent'}
  $a = New-Object System.Windows.Forms.DataVisualization.Charting.ChartArea; $a.BackColor = 'White'
  $a.AxisY.Minimum = 0; $a.AxisY.Maximum = $max; $a.AxisX.LabelStyle.Enabled = $false; $a.AxisX.MajorGrid.Enabled = $false
  $a.AxisY.MajorGrid.LineColor = 'Gainsboro'; $a.AxisY.LabelStyle.Font = New-Object Drawing.Font("Segoe UI",7); $a.AxisX.MajorTickMark.Enabled = $false
  $a.Position.Auto = $true; [void]$c.ChartAreas.Add($a)
  $s = New-Object System.Windows.Forms.DataVisualization.Charting.Series; $s.ChartType = 'Line'; $s.BorderWidth = 2; $s.Color = $color
  [void]$c.Series.Add($s); $c
}
$dash.CpuC = New-MiniChart 'SteelBlue' 100; $dash.RamC = New-MiniChart 'MediumPurple' 100; $dash.HisC = New-MiniChart 'SeaGreen' 100
$right.Controls.Add($dash.CpuC, 0, 1); $right.Controls.Add($dash.RamC, 1, 1); $right.Controls.Add($dash.HisC, 2, 1)
$dash.Top.Controls.Add($scoreBox, 0, 0); $dash.Top.Controls.Add($right, 1, 0)

$dash.Status = New-Object Windows.Forms.Label -Property @{Dock='Top'; Height=30; TextAlign='MiddleLeft'; Text="Click Run health check."; Font=New-Object Drawing.Font("Segoe UI",11,[Drawing.FontStyle]::Bold)}
$dash.List = New-Object Windows.Forms.ListView -Property @{View='Details'; FullRowSelect=$true; Dock='Fill'; HideSelection=$false}
foreach ($c in @(@("Status",85), @("Area",120), @("Finding",380), @("Suggested action",170))) { [void]$dash.List.Columns.Add($c[0], $c[1]) }
$dash.Advice = New-Object Windows.Forms.Label -Property @{Dock='Bottom'; Height=52; Padding='2,6,2,2'; Text="Select a finding to see what to do."; ForeColor='DimGray'}
$dash.Bar = New-Object Windows.Forms.FlowLayoutPanel -Property @{Dock='Bottom'; AutoSize=$true; AutoSizeMode='GrowAndShrink'; WrapContents=$true; Padding='0,6,0,0'}
$bp = @{AutoSize=$true; MinimumSize='120,36'; Margin='0,0,8,0'}
$dash.Run = New-Object Windows.Forms.Button -Property ($bp + @{Text="Run health check"})
$dash.Do  = New-Object Windows.Forms.Button -Property ($bp + @{Text="Do suggested action"; Enabled=$false})
$dash.Rep = New-Object Windows.Forms.Button -Property ($bp + @{Text="Save report..."; Enabled=$false})
$dash.Bar.Controls.AddRange(@($dash.Run, $dash.Do, $dash.Rep))
$dash.Page.Controls.Add($dash.List); $dash.Page.Controls.Add($dash.Advice); $dash.Page.Controls.Add($dash.Bar); $dash.Page.Controls.Add($dash.Status); $dash.Page.Controls.Add($dash.Top)
# TabPages.Insert is ignored before the control has a window handle, so rebuild the order instead
$others = @($tabs.TabPages | ForEach-Object { $_ }); $tabs.TabPages.Clear()
$tabs.TabPages.Add($dash.Page); foreach ($p in $others) { $tabs.TabPages.Add($p) }
$tabs.SelectedIndex = 0

$StatusColor = @{ Problem='Firebrick'; Warning='DarkOrange'; OK='ForestGreen'; Info='DimGray' }
$StatusOrder = @{ Problem=0; Warning=1; Info=2; OK=3 }

function Show-Findings($rows) {
  $dash.List.BeginUpdate(); $dash.List.Items.Clear()
  foreach ($r in ($rows | Sort-Object { $StatusOrder[$_.Status] }, Area)) {
    $it = $dash.List.Items.Add($r.Status); [void]$it.SubItems.Add($r.Area); [void]$it.SubItems.Add($r.Finding); [void]$it.SubItems.Add($r.ActionText)
    $it.UseItemStyleForSubItems = $false; $it.ForeColor = [Drawing.Color]::FromName($StatusColor[$r.Status]); $it.Font = New-Object Drawing.Font("Segoe UI",10,[Drawing.FontStyle]::Bold)
    $it.Tag = $r
  }
  $dash.List.EndUpdate()
}
function Show-Score($score) {
  $dash.Score.Text = "$score"; $dash.Score.ForeColor = [Drawing.Color]::FromName((Get-GradeColor $score)); $dash.Grade.Text = Get-Grade $score
}
function Show-History {
  $h = Get-History
  if ($script:HaveCharts) { $s = $dash.HisC.Series[0]; $s.Points.Clear(); foreach ($r in ($h | Select-Object -Last 30)) { [void]$s.Points.AddY([int]$r.Score) }; $s.MarkerStyle = 'Circle'; $s.MarkerSize = 4 }
  if ($h.Count -ge 2) {
    $prev = $h[-2]; $now = $h[-1]; $diff = [int]$now.Score - [int]$prev.Score
    $when = try { ([datetime]$prev.Date).ToString("MMM d") } catch { "last time" }
    $dash.Trend.Text = "{0} since {1} ({2})" -f $(if ($diff -gt 0) { "Up $diff" } elseif ($diff -lt 0) { "Down $(-$diff)" } else { "No change" }), $when, $prev.Score
  } elseif ($h.Count -eq 1) { $dash.Trend.Text = "First check - history starts now" }
  $dash.HisL.Text = "Score history ($($h.Count) checks)"
}

$dash.Run.Add_Click({
  $dash.Run.Enabled = $false; $dash.Do.Enabled = $false; $form.Cursor = 'WaitCursor'
  $rows = Invoke-HealthCheck; $script:Findings = $rows
  $score = Get-HealthScore $rows; Show-Score $score; Show-Findings $rows
  Save-History $score $rows; Show-History
  $p = @($rows | Where-Object Status -eq 'Problem').Count; $w = @($rows | Where-Object Status -eq 'Warning').Count
  $dash.Status.Text = if ($p + $w) { "$p problem(s) and $w recommendation(s). Select one to see what to do." } else { "Everything looks good. Nothing needs doing." }
  $dash.Run.Enabled = $true; $dash.Rep.Enabled = $true; $form.Cursor = 'Default'
})

$dash.List.Add_SelectedIndexChanged({
  if (-not $dash.List.SelectedItems.Count) { $dash.Do.Enabled = $false; return }
  $r = $dash.List.SelectedItems[0].Tag
  $dash.Advice.Text = if ($r.Advice) { $r.Advice } elseif ($r.Status -eq 'OK') { "No action needed." } else { "" }
  $dash.Do.Text = if ($r.ActionText) { $r.ActionText } else { "Do suggested action" }
  $dash.Do.Enabled = [bool]$r.Action
})

# Actions only navigate or open Windows' own tools - CleanSweep never changes settings from here
function Invoke-FindingAction($action) {
  switch -Regex ($action) {
    '^tab:junk$'   { $tabs.SelectedTab = $junk.Page }
    '^tab:net$'    { $tabs.SelectedTab = $wifi.Page }
    '^tab:repair$' { $tabs.SelectedTab = $rep.Page }
    '^tab:drives$' { $tabs.SelectedTab = $drv.Page; foreach ($i in $drv.List.Items) { if ($i.Tag.Letter -eq $SysDrive) { $i.Checked = $true } } }
    '^settings:(.+)$' { Start-Process $matches[1] }
    '^run:(\S+)\s*(.*)$' { if ($matches[2]) { Start-Process $matches[1] -ArgumentList $matches[2] } else { Start-Process $matches[1] } }
    '^battery$' {
      $f = Join-Path $env:TEMP "CleanSweep battery report.html"
      & powercfg.exe /batteryreport /output "$f" | Out-Null
      if (Test-Path $f) { Start-Process $f } else { [void][CSMsg]::Show("Windows could not create a battery report.","CleanSweep","OK","Warning") }
    }
  }
}
$dash.Do.Add_Click({ if ($dash.List.SelectedItems.Count) { Invoke-FindingAction $dash.List.SelectedItems[0].Tag.Action } })
$dash.List.Add_DoubleClick({ if ($dash.List.SelectedItems.Count -and $dash.List.SelectedItems[0].Tag.Action) { Invoke-FindingAction $dash.List.SelectedItems[0].Tag.Action } })

$dash.Rep.Add_Click({
  $sd = New-Object Windows.Forms.SaveFileDialog -Property @{Filter="Text file (*.txt)|*.txt"; FileName="CleanSweep health report.txt"}
  if ($sd.ShowDialog() -ne 'OK') { return }
  $score = Get-HealthScore $script:Findings
  $txt = "CleanSweep health report - " + (Get-Date).ToString("yyyy-MM-dd HH:mm") + "`r`nComputer: $env:COMPUTERNAME`r`nHealth score: $score ($(Get-Grade $score))`r`n`r`n" +
    (($script:Findings | Sort-Object { $StatusOrder[$_.Status] } | ForEach-Object { "[$($_.Status)] $($_.Area): $($_.Finding)" + $(if ($_.Advice -and $_.Status -ne 'OK') { "`r`n    What to do: $($_.Advice)" } else { "" }) }) -join "`r`n")
  Set-Content -LiteralPath $sd.FileName -Value $txt -Encoding UTF8
})

# Live CPU / memory (every 2 seconds while the Dashboard is showing)
function Update-Live {
  try {
    $cpu = [int](Get-CimInstance Win32_PerfFormattedData_PerfOS_Processor -Filter "Name='_Total'" -ErrorAction Stop).PercentProcessorTime
    $os = Get-CimInstance Win32_OperatingSystem -ErrorAction Stop
    $ram = [int](([double]$os.TotalVisibleMemorySize - $os.FreePhysicalMemory) / $os.TotalVisibleMemorySize * 100)
    $dash.CpuL.Text = "CPU $cpu%"; $dash.RamL.Text = "Memory $ram% of $(Fmt ([double]$os.TotalVisibleMemorySize * 1KB))"
    if ($script:HaveCharts) {
      foreach ($pair in @(@($dash.CpuC, $cpu), @($dash.RamC, $ram))) {
        $pts = $pair[0].Series[0].Points; [void]$pts.AddY($pair[1]); while ($pts.Count -gt 60) { $pts.RemoveAt(0) }
      }
    }
  } catch {}
}
$liveTimer = New-Object Windows.Forms.Timer -Property @{Interval=2000}
$liveTimer.Add_Tick({ if ($tabs.SelectedTab -eq $dash.Page -and $form.WindowState -ne 'Minimized') { Update-Live } })
$liveTimer.Start()
Update-Live; Show-History

# First check runs automatically when the window opens (read-only)
$form.Add_Shown({ if (-not $TestMode) { $dash.Run.PerformClick() } })
