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

# ---------------------------------------------------------------- UI (Windows 11 style dark dashboard)
$dash = @{}
$dash.Page = New-Object Windows.Forms.TabPage -Property @{Text="Dashboard"}
$scroll = New-Object Windows.Forms.Panel -Property @{Dock='Fill'; AutoScroll=$true; Padding='0,0,8,0'}
$dash.Page.Controls.Add($scroll)
$StatusColor = @{ Problem=$Theme.Bad; Warning=$Theme.Warn; OK=$Theme.Ok; Info=$Theme.Info }
$StatusOrder = @{ Problem=0; Warning=1; Info=2; OK=3 }
function New-Lbl($text, $font, $color, $dock = 'Top', $h = 0) {
  $l = New-Object Windows.Forms.Label -Property @{Text=$text; Font=$font; ForeColor=$color; Dock=$dock; AutoEllipsis=$true; BackColor=[Drawing.Color]::Transparent}
  if ($h) { $l.Height = $h } else { $l.AutoSize = $false; $l.Height = [int]($font.GetHeight() + 6) }
  $l
}
function Add-Rows($parent, $rows) { for ($i = $rows.Count - 1; $i -ge 0; $i--) { $parent.Controls.Add($rows[$i]) } }   # Dock=Top stacks in reverse
function Spacer($h = 12) { New-Object Windows.Forms.Panel -Property @{Dock='Top'; Height=$h} }

# ---- header
$hello = if ((Get-Date).Hour -lt 12) { "Good morning" } elseif ((Get-Date).Hour -lt 18) { "Good afternoon" } else { "Good evening" }
$osInfo = try { $cv = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion' -ErrorAction Stop; $cap = (Get-CimInstance Win32_OperatingSystem -ErrorAction Stop).Caption -replace '^Microsoft ', ''; "$cap $($cv.DisplayVersion)" } catch { "Windows" }
$model = try { $cs = Get-CimInstance Win32_ComputerSystem -ErrorAction Stop; ("$($cs.Manufacturer) $($cs.Model)" -replace 'System manufacturer System Product Name', 'PC').Trim() } catch { "" }
$hdr = New-Object Windows.Forms.Panel -Property @{Dock='Top'; Height=64}
$hdrT = New-Lbl $hello (DisplayFont 20 'Bold') $Theme.Text 'Top' 38
$hdrS = New-Lbl ("$env:COMPUTERNAME  {0}  $model  {0}  $osInfo" -f [char]0xB7) (UiFont 9.5) $Theme.Sub 'Top' 22
Add-Rows $hdr @($hdrT, $hdrS)

# ---- row 1: health score card + one-click actions card
$row1 = New-Object Windows.Forms.TableLayoutPanel -Property @{Dock='Top'; Height=250; ColumnCount=2; RowCount=1; Margin='0,0,0,0'; Padding='0,0,0,0'}
[void]$row1.ColumnStyles.Add((New-Object Windows.Forms.ColumnStyle 'Absolute', 380)); [void]$row1.ColumnStyles.Add((New-Object Windows.Forms.ColumnStyle 'Percent', 100))
[void]$row1.RowStyles.Add((New-Object Windows.Forms.RowStyle 'Percent', 100))

$health = New-Card
$dash.Score = New-Object Windows.Forms.Label -Property @{Text="--"}   # value holder (the gauge paints it)
$dash.Gauge = New-Object Windows.Forms.Panel -Property @{Dock='Left'; Width=170}
Set-DoubleBuffered $dash.Gauge
$GaugeFonts = @{ Num=(DisplayFont 34 'Bold'); Small=(UiFont 9) }
$dash.Gauge.Add_Paint({ param($s, $e)
  $g = $e.Graphics; $g.SmoothingMode = 'AntiAlias'; $g.TextRenderingHint = 'AntiAliasGridFit'
  $sz = [math]::Min($s.Width, $s.Height) - 24; $r = New-Object Drawing.RectangleF 10, (($s.Height - $sz) / 2), $sz, $sz
  $pen = New-Object Drawing.Pen (C '#3A3A3A'), 12; $pen.StartCap = 'Round'; $pen.EndCap = 'Round'; $g.DrawArc($pen, $r, 135, 270); $pen.Dispose()
  $v = 0; $has = [int]::TryParse($dash.Score.Text, [ref]$v)
  if ($has -and $v -gt 0) { $col = Get-GradeColor $v; $pen = New-Object Drawing.Pen $col, 12; $pen.StartCap = 'Round'; $pen.EndCap = 'Round'; $g.DrawArc($pen, $r, 135, [float](270 * $v / 100)); $pen.Dispose() }
  $txt = if ($has) { "$v" } else { "--" }
  $fmt = New-Object Drawing.StringFormat; $fmt.Alignment = 'Center'; $fmt.LineAlignment = 'Center'
  $b = New-Object Drawing.SolidBrush $Theme.Text; $g.DrawString($txt, $GaugeFonts.Num, $b, (New-Object Drawing.RectangleF $r.X, ($r.Y - 6), $r.Width, $r.Height), $fmt); $b.Dispose()
  $b = New-Object Drawing.SolidBrush $Theme.Sub; $g.DrawString("HEALTH SCORE", $GaugeFonts.Small, $b, (New-Object Drawing.RectangleF $r.X, ($r.Y + $r.Height * 0.30), $r.Width, $r.Height), $fmt); $b.Dispose()
})
$hInfo = New-Object Windows.Forms.Panel -Property @{Dock='Fill'; Padding='12,18,0,0'}
$dash.Grade  = New-Lbl "Not checked yet" (DisplayFont 16 'Bold') $Theme.Text 'Top' 34
$dash.Trend  = New-Lbl "" (UiFont 9.5) $Theme.Sub 'Top' 40; $dash.Trend.AutoEllipsis = $false
$dash.Status = New-Lbl "Run a health check to see your score." (UiFont 9.5) $Theme.Sub 'Top' 62
$dash.Status.AutoEllipsis = $false
$hBtns = New-Object Windows.Forms.FlowLayoutPanel -Property @{Dock='Bottom'; Height=44; WrapContents=$false}
$dash.Run = New-Object Windows.Forms.Button -Property @{Text="Run health check"; AutoSize=$true; MinimumSize='150,34'; Margin='0,0,8,0'}
$dash.Rep = New-Object Windows.Forms.Button -Property @{Text="Save report"; AutoSize=$true; MinimumSize='110,34'; Dock='Right'; Enabled=$false}
Set-Primary $dash.Run
$hBtns.Controls.Add($dash.Run)
$hInfo.Controls.Add($hBtns); Add-Rows $hInfo @($dash.Grade, $dash.Trend, $dash.Status)
$health.Controls.Add($hInfo); $health.Controls.Add($dash.Gauge)

# One-click action tiles
$acts = New-Card
$actsT = New-Lbl "One-click actions" (UiFont 11 'Bold') $Theme.Text 'Top' 28
$grid = New-Object Windows.Forms.TableLayoutPanel -Property @{Dock='Fill'; ColumnCount=3; RowCount=2; Padding='0,4,0,0'}
$dash.Tiles = @{}; $script:TileOf = @{}
function New-Tile($key, $glyph, $title, $sub) {
  $t = New-Object Windows.Forms.Panel -Property @{Dock='Fill'; BackColor=$Theme.Tile; Margin='0,0,8,8'; Cursor='Hand'; Padding='12,10,8,6'}
  Set-Rounded $t 6
  $ic = New-Lbl ([string]$glyph) (IconFont 16) $Theme.Accent 'Left' 0; $ic.Width = 34; $ic.TextAlign = 'MiddleLeft'
  $txt = New-Object Windows.Forms.Panel -Property @{Dock='Fill'; BackColor=[Drawing.Color]::Transparent}
  $tt = New-Lbl $title (UiFont 10 'Bold') $Theme.Text 'Top' 22
  $ts = New-Lbl $sub (UiFont 8.5) $Theme.Sub 'Fill' 0
  $txt.Controls.Add($ts); $txt.Controls.Add($tt)
  $t.Controls.Add($txt); $t.Controls.Add($ic)
  foreach ($c in @($t, $ic, $txt, $tt, $ts)) {
    $c.Cursor = 'Hand'; $script:TileOf[$c] = @{ Key=$key; Tile=$t }
    $c.Add_Click({ param($s, $e) Invoke-QuickAction $script:TileOf[$s].Key })
    $c.Add_MouseEnter({ param($s, $e) $script:TileOf[$s].Tile.BackColor = $Theme.TileHover })
    $c.Add_MouseLeave({ param($s, $e) $t = $script:TileOf[$s].Tile; if (-not $t.ClientRectangle.Contains($t.PointToClient([Windows.Forms.Cursor]::Position))) { $t.BackColor = $Theme.Tile } })
  }
  $dash.Tiles[$key] = @{ Panel=$t; Sub=$ts; Default=$sub }
  $t
}
[void](New-Tile 'clean'   $Glyph.Clean   "Quick clean"     "Scan and remove junk on $SysDrive")
[void](New-Tile 'space'   $Glyph.Space   "Free up space"   "Find large files on $SysDrive")
[void](New-Tile 'repair'  $Glyph.Repair  "Repair Windows"  "DISM + System File Checker")
[void](New-Tile 'optimize' $Glyph.Speed  "Optimize drives" "TRIM SSDs, defragment HDDs")
[void](New-Tile 'network' $Glyph.Net     "Network check"   "Test speed, latency and DNS")
[void](New-Tile 'restore' $Glyph.Restore "Restore point"   "Create a safety snapshot")
$acts.Controls.Add($grid); $acts.Controls.Add($actsT)
$script:TileOrder = @('clean','space','repair','optimize','network','restore'); $script:TileCols = 0
function Update-TileLayout {
  $cols = if ($acts.Width -lt 560) { 2 } else { 3 }
  if ($cols -eq $script:TileCols) { return }; $script:TileCols = $cols; $rows = [math]::Ceiling(6 / $cols)
  $grid.SuspendLayout(); $grid.Controls.Clear(); $grid.ColumnStyles.Clear(); $grid.RowStyles.Clear(); $grid.ColumnCount = $cols; $grid.RowCount = $rows
  for ($i = 0; $i -lt $cols; $i++) { [void]$grid.ColumnStyles.Add((New-Object Windows.Forms.ColumnStyle 'Percent', (100 / $cols))) }
  for ($i = 0; $i -lt $rows; $i++) { [void]$grid.RowStyles.Add((New-Object Windows.Forms.RowStyle 'Percent', (100 / $rows))) }
  for ($i = 0; $i -lt 6; $i++) { $grid.Controls.Add($dash.Tiles[$script:TileOrder[$i]].Panel, ($i % $cols), [math]::Floor($i / $cols)) }
  $grid.ResumeLayout()
  $row1.Height = if ($cols -eq 2) { 330 } else { 250 }
}
$acts.Add_Resize({ Update-TileLayout }); Update-TileLayout
$row1.Controls.Add($health, 0, 0); $row1.Controls.Add($acts, 1, 0)

# ---- row 2: hardware monitoring cards (3 x 2)
$hwT = New-Lbl "Hardware monitor" (UiFont 11 'Bold') $Theme.Text 'Top' 30
$row2 = New-Object Windows.Forms.TableLayoutPanel -Property @{Dock='Top'; Height=356; ColumnCount=3; RowCount=2}
foreach ($i in 1..3) { [void]$row2.ColumnStyles.Add((New-Object Windows.Forms.ColumnStyle 'Percent', 33.33)) }
foreach ($i in 1..2) { [void]$row2.RowStyles.Add((New-Object Windows.Forms.RowStyle 'Percent', 50)) }
$Spark = @{}
function New-Spark($key, $color, [double]$max) {
  $p = New-Object Windows.Forms.Panel -Property @{Dock='Fill'; BackColor=[Drawing.Color]::Transparent}
  Set-DoubleBuffered $p
  $Spark[$key] = @{ Data=(New-Object System.Collections.Generic.List[double]); Max=$max; Color=$color; Panel=$p }; $p.Tag = $key
  $p.Add_Paint({ param($s, $e) Paint-Spark $s $e })
  $p
}
function Paint-Spark($s, $e) {
    $sp = $Spark[$s.Tag]; $d = $sp.Data; $g = $e.Graphics; $g.SmoothingMode = 'AntiAlias'
    $w = $s.Width; $h = $s.Height - 2; if ($w -lt 10 -or $h -lt 10) { return }
    $gp = New-Object Drawing.Pen (C '#363636'), 1; $g.DrawLine($gp, 0, $h, $w, $h); $g.DrawLine($gp, 0, [int]($h / 2), $w, [int]($h / 2)); $gp.Dispose()
    if ($d.Count -lt 2) { return }
    $max = if ($sp.Max -gt 0) { $sp.Max } else { [math]::Max(1, ($d | Measure-Object -Maximum).Maximum * 1.2) }
    $n = 60; $step = $w / ($n - 1); $off = $n - $d.Count
    $pts = New-Object 'System.Collections.Generic.List[Drawing.PointF]'
    for ($i = 0; $i -lt $d.Count; $i++) { $pts.Add((New-Object Drawing.PointF (($off + $i) * $step), ($h - [math]::Min($h, $d[$i] / $max * $h)))) }
    $area = New-Object Drawing.Drawing2D.GraphicsPath; $area.AddLines($pts.ToArray())
    $area.AddLine($pts[$pts.Count - 1].X, $h, $pts[0].X, $h); $area.CloseFigure()
    $fill = New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(50, $sp.Color)); $g.FillPath($fill, $area); $fill.Dispose()
    $pen = New-Object Drawing.Pen $sp.Color, 2; $g.DrawLines($pen, $pts.ToArray()); $pen.Dispose()
}
$HW = @{}
function New-HwCard($key, $glyph, $title, $color, [double]$max) {
  $c = New-Card 'Fill' '16,12,16,10'
  $top = New-Object Windows.Forms.Panel -Property @{Dock='Top'; Height=24; BackColor=[Drawing.Color]::Transparent}
  $ic = New-Lbl ([string]$glyph) (IconFont 11) $color 'Left' 0; $ic.Width = 24; $ic.TextAlign = 'MiddleLeft'
  $tl = New-Lbl $title (UiFont 9.5 'Bold') $Theme.Sub 'Fill' 0; $tl.TextAlign = 'MiddleLeft'
  $top.Controls.Add($tl); $top.Controls.Add($ic)
  $val = New-Lbl "--" (DisplayFont 20 'Bold') $Theme.Text 'Top' 40
  $sub = New-Lbl "" (UiFont 8.5) $Theme.Sub 'Top' 36; $sub.AutoEllipsis = $true
  $sp = New-Spark $key $color $max
  $c.Controls.Add($sp); Add-Rows $c @($top, $val, $sub)
  $HW[$key] = @{ Value=$val; Sub=$sub; Title=$tl }
  $c
}
$row2.Controls.Add((New-HwCard 'cpu'  $Glyph.Cpu     "Processor" (C '#60CDFF') 100), 0, 0)
$row2.Controls.Add((New-HwCard 'ram'  $Glyph.Memory  "Memory"    (C '#C3A6FF') 100), 1, 0)
$row2.Controls.Add((New-HwCard 'gpu'  $Glyph.Gpu     "Graphics"  (C '#FF9EC4') 100), 2, 0)
$row2.Controls.Add((New-HwCard 'disk' $Glyph.Disk    "Disk"      (C '#FFC66D') 100), 0, 1)
$row2.Controls.Add((New-HwCard 'net'  $Glyph.Net     "Network"   (C '#6CCB5F') 0), 1, 1)
$row2.Controls.Add((New-HwCard 'bat'  $Glyph.Battery "Battery and temperatures" (C '#9FE6A0') 100), 2, 1)
$dash.CpuL = $HW.cpu.Value; $dash.RamL = $HW.ram.Value

# ---- row 3: recommendations
$recT = New-Lbl "Recommendations" (UiFont 11 'Bold') $Theme.Text 'Top' 30
$rec = New-Card 'Top' '4,4,4,10'; $rec.Height = 340; $rec.Margin = '0,0,0,0'
$dash.List = New-Object Windows.Forms.ListView -Property @{View='Details'; FullRowSelect=$true; Dock='Fill'; HideSelection=$false; MultiSelect=$false}
foreach ($c in @(@("Status",90), @("Area",130), @("Finding",440), @("Suggested action",190))) { [void]$dash.List.Columns.Add($c[0], $c[1]) }
$recBottom = New-Object Windows.Forms.Panel -Property @{Dock='Bottom'; Height=50; Padding='12,6,12,0'}
$dash.Do = New-Object Windows.Forms.Button -Property @{Text="Do suggested action"; AutoSize=$true; MinimumSize='170,34'; Dock='Right'; Enabled=$false}
Set-Primary $dash.Do
$dash.Advice = New-Lbl "Select a finding to see what to do." (UiFont 9.5) $Theme.Sub 'Fill' 0; $dash.Advice.AutoEllipsis = $false
$recGap = New-Object Windows.Forms.Panel -Property @{Dock='Right'; Width=8}
$recBottom.Controls.Add($dash.Advice); $recBottom.Controls.Add($dash.Do); $recBottom.Controls.Add($recGap); $recBottom.Controls.Add($dash.Rep)
$rec.Controls.Add($dash.List); $rec.Controls.Add($recBottom)
$dash.HisL = New-Lbl "" (UiFont 9) $Theme.Sub 'Top' 20

Add-Rows $scroll @($hdr, $row1, (Spacer 8), $hwT, $row2, (Spacer 8), $recT, $rec, (Spacer 12))
$others = @($tabs.TabPages | ForEach-Object { $_ }); $tabs.TabPages.Clear()
$tabs.TabPages.Add($dash.Page); foreach ($p in $others) { $tabs.TabPages.Add($p) }
$tabs.SelectedIndex = 0

# ---------------------------------------------------------------- findings / score display
function Show-Findings($rows) {
  $dash.List.BeginUpdate(); $dash.List.Items.Clear()
  foreach ($r in ($rows | Sort-Object { $StatusOrder[$_.Status] }, Area)) {
    $it = $dash.List.Items.Add($r.Status); [void]$it.SubItems.Add($r.Area); [void]$it.SubItems.Add($r.Finding); [void]$it.SubItems.Add($r.ActionText)
    $it.UseItemStyleForSubItems = $false; $it.ForeColor = $StatusColor[$r.Status]; $it.Font = UiFont 10 'Bold'
    if ($r.ActionText) { $it.SubItems[3].ForeColor = $Theme.Accent }
    $it.Tag = $r
  }
  $dash.List.EndUpdate()
}
function Show-Score($score) { $dash.Score.Text = "$score"; $dash.Grade.Text = Get-Grade $score; $dash.Grade.ForeColor = Get-GradeColor $score; $dash.Gauge.Invalidate() }
function Get-GradeColor($s) { if ($s -ge 90) { $Theme.Ok } elseif ($s -ge 75) { C '#9BDB4D' } elseif ($s -ge 50) { $Theme.Warn } else { $Theme.Bad } }
function Show-History {
  $h = Get-History
  if ($h.Count -ge 2) {
    $prev = $h[-2]; $now = $h[-1]; $diff = [int]$now.Score - [int]$prev.Score
    $when = try { ([datetime]$prev.Date).ToString("MMM d") } catch { "last time" }
    $dash.Trend.Text = "{0} since {1}  $([char]0xB7)  {2} checks recorded" -f $(if ($diff -gt 0) { "Up $diff" } elseif ($diff -lt 0) { "Down $(-$diff)" } else { "No change" }), $when, $h.Count
  } elseif ($h.Count -eq 1) { $dash.Trend.Text = "First check - history starts now" }
  $dash.HisL.Text = "Score history ($($h.Count) checks)"
}

$dash.Run.Add_Click({
  $dash.Run.Enabled = $false; $dash.Do.Enabled = $false; $form.Cursor = 'WaitCursor'
  $rows = Invoke-HealthCheck; $script:Findings = $rows
  $score = Get-HealthScore $rows; Show-Score $score; Show-Findings $rows
  Save-History $score $rows; Show-History
  $p = @($rows | Where-Object Status -eq 'Problem').Count; $w = @($rows | Where-Object Status -eq 'Warning').Count
  $dash.Status.Text = if ($p + $w) { "$p problem(s) and $w recommendation(s) - see Recommendations below." } else { "Everything looks good. Nothing needs doing." }
  $dash.Run.Enabled = $true; $dash.Rep.Enabled = $true; $form.Cursor = 'Default'
})
$dash.List.Add_SelectedIndexChanged({
  if (-not $dash.List.SelectedItems.Count) { $dash.Do.Enabled = $false; return }
  $r = $dash.List.SelectedItems[0].Tag
  $dash.Advice.Text = if ($r.Advice) { $r.Advice } elseif ($r.Status -eq 'OK') { "No action needed." } else { "" }
  $dash.Do.Text = if ($r.ActionText) { $r.ActionText } else { "Do suggested action" }
  $dash.Do.Enabled = [bool]$r.Action
})

# Recommendation actions only navigate or open Windows' own tools
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
  $txt = "CleanSweep health report - " + (Get-Date).ToString("yyyy-MM-dd HH:mm") + "`r`nComputer: $env:COMPUTERNAME ($model, $osInfo)`r`nHealth score: $score ($(Get-Grade $score))`r`n`r`n" +
    (($script:Findings | Sort-Object { $StatusOrder[$_.Status] } | ForEach-Object { "[$($_.Status)] $($_.Area): $($_.Finding)" + $(if ($_.Advice -and $_.Status -ne 'OK') { "`r`n    What to do: $($_.Advice)" } else { "" }) }) -join "`r`n")
  Set-Content -LiteralPath $sd.FileName -Value $txt -Encoding UTF8
})

# ---------------------------------------------------------------- one-click actions
# Each action shows what it does on the matching page; anything that changes the PC still asks first
function Invoke-QuickAction($key) {
  if ($script:RepairBusy) { [void][CSMsg]::Show("Another task is still running. Wait for it to finish or cancel it first.","CleanSweep","OK","Information"); return }
  $tile = $dash.Tiles[$key]
  switch ($key) {
    'clean' {
      Load-Drives; foreach ($cb in $script:DriveChecks) { $cb.Checked = ($cb.Tag -eq $SysDrive) }
      $tabs.SelectedTab = $junk.Page; [Windows.Forms.Application]::DoEvents(); $junk.Scan.PerformClick()
      if ($junk.Clean.Enabled) { $junk.Clean.PerformClick() }
    }
    'space' { Invoke-FindingAction 'tab:drives'; [Windows.Forms.Application]::DoEvents(); foreach ($i in $drv.List.Items) { $i.Checked = ($i.Tag.Letter -eq $SysDrive) }; Update-DriveButtons; $drv.Large.PerformClick() }
    'repair' { $tabs.SelectedTab = $rep.Page; [Windows.Forms.Application]::DoEvents(); $rep.Rec.PerformClick() }
    'optimize' {
      $tabs.SelectedTab = $drv.Page; [Windows.Forms.Application]::DoEvents()
      foreach ($i in $drv.List.Items) { $i.Checked = [bool]($i.Tag.Letter -and $i.Tag.Media -notmatch 'USB|Removable') }; Update-DriveButtons
      if ([CSMsg]::Show("Optimize $((Get-CheckedDrives | ForEach-Object Letter) -join ', ')?`n`nWindows trims SSDs and defragments hard drives - the same thing its weekly maintenance does.","CleanSweep","YesNo","Question") -eq 'Yes') { $drv.Opt.PerformClick() }
    }
    'network' { $tabs.SelectedTab = $wifi.Page; [Windows.Forms.Application]::DoEvents(); $wifi.Scan.PerformClick() }
    'restore' {
      $tile.Sub.Text = "Creating restore point..."; $form.Cursor = 'WaitCursor'; [Windows.Forms.Application]::DoEvents()
      $ok = New-RestorePoint "CleanSweep - manual restore point"; $form.Cursor = 'Default'
      $tile.Sub.Text = if ($ok) { "Created " + (Get-Date).ToString("MMM d, h:mm tt") } else { "Not available (System Restore is off)" }
      if (-not $ok) { [void][CSMsg]::Show("A restore point could not be created. System Restore may be turned off - turn it on in Control Panel > System > System Protection.","CleanSweep","OK","Information") }
    }
  }
}

# ---------------------------------------------------------------- hardware monitor (background sampler)
# Static details read once
$HwStatic = @{}
try { $cpuW = Get-CimInstance Win32_Processor -ErrorAction Stop | Select-Object -First 1
  $HwStatic.Cpu = ($cpuW.Name -replace '\(R\)|\(TM\)|CPU|Processor|\s+@.*$', '' -replace '\s+', ' ').Trim(); $HwStatic.Cores = "$($cpuW.NumberOfCores) cores, $($cpuW.NumberOfLogicalProcessors) threads"; $HwStatic.MaxMhz = $cpuW.MaxClockSpeed } catch {}
try { $gpuW = @(Get-CimInstance Win32_VideoController -ErrorAction Stop | Where-Object { $_.Name -notmatch 'Basic Display|Remote|Hyper-V|Mirror' })
  $HwStatic.Gpu = if ($gpuW) { $gpuW[0].Name } else { "Basic display adapter" }; $HwStatic.GpuDrv = if ($gpuW) { "Driver $($gpuW[0].DriverVersion)" } else { "" } } catch {}
try { $mem = @(Get-CimInstance Win32_PhysicalMemory -ErrorAction Stop); $spd = ($mem | Measure-Object Speed -Maximum).Maximum; $HwStatic.RamInfo = "$($mem.Count) module(s)$(if ($spd) { ", $spd MT/s" })" } catch {}

$SampleBlock = {
  param([int]$tick)
  $r = @{}
  try { $r.Cpu = [int](Get-CimInstance Win32_PerfFormattedData_PerfOS_Processor -Filter "Name='_Total'" -ErrorAction Stop).PercentProcessorTime } catch {}
  try { $r.CpuPerf = [int](Get-CimInstance Win32_PerfFormattedData_Counters_ProcessorInformation -Filter "Name='_Total'" -ErrorAction Stop).PercentProcessorPerformance } catch {}
  try { $os = Get-CimInstance Win32_OperatingSystem -ErrorAction Stop; $r.RamTotal = [double]$os.TotalVisibleMemorySize * 1KB; $r.RamFree = [double]$os.FreePhysicalMemory * 1KB
        $r.Commit = ([double]$os.TotalVirtualMemorySize - $os.FreeVirtualMemory) * 1KB; $r.Boot = $os.LastBootUpTime } catch {}
  try { $d = Get-CimInstance Win32_PerfFormattedData_PerfDisk_PhysicalDisk -Filter "Name='_Total'" -ErrorAction Stop
        $r.DiskBusy = [int][math]::Max(0, [math]::Min(100, 100 - $d.PercentIdleTime)); $r.DiskRead = [double]$d.DiskReadBytesPersec; $r.DiskWrite = [double]$d.DiskWriteBytesPersec } catch {}
  try { $c = Get-CimInstance Win32_LogicalDisk -Filter "DeviceID='$env:SystemDrive'" -ErrorAction Stop; $r.SysFree = [double]$c.FreeSpace; $r.SysSize = [double]$c.Size } catch {}
  try { $n = @(Get-CimInstance Win32_PerfFormattedData_Tcpip_NetworkInterface -ErrorAction Stop | Where-Object { $_.Name -notmatch 'isatap|Teredo|Loopback|vEthernet' })
        $r.NetDown = [double](($n | Measure-Object BytesReceivedPersec -Sum).Sum); $r.NetUp = [double](($n | Measure-Object BytesSentPersec -Sum).Sum) } catch {}
  try { $gp = @(Get-CimInstance Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine -ErrorAction Stop | Where-Object { $_.Name -like '*engtype_3D*' })
        if ($gp) { $r.Gpu = [int][math]::Min(100, ($gp | Measure-Object UtilizationPercentage -Sum).Sum) } } catch {}
  try { $gm = @(Get-CimInstance Win32_PerfFormattedData_GPUPerformanceCounters_GPUAdapterMemory -ErrorAction Stop); if ($gm) { $r.GpuMem = [double](($gm | Measure-Object DedicatedUsage -Sum).Sum) } } catch {}
  try { $b = Get-CimInstance Win32_Battery -ErrorAction Stop | Select-Object -First 1
        if ($b) { $r.Bat = [int]$b.EstimatedChargeRemaining; $r.BatAC = ($b.BatteryStatus -in 2,6,7,8,9); $r.BatMin = if ($b.EstimatedRunTime -and $b.EstimatedRunTime -lt 71582788) { [int]$b.EstimatedRunTime } else { $null } } } catch {}
  # Temperatures every ~30 s (drive sensors are slow to read)
  if ($tick % 20 -eq 0) {
    $temps = @()
    try { $lhm = @(Get-CimInstance -Namespace root\LibreHardwareMonitor -ClassName Sensor -ErrorAction Stop | Where-Object { $_.SensorType -eq 'Temperature' -and $_.Name -match 'CPU Package|Core \(Tctl|GPU Core' })
          foreach ($s in $lhm) { $temps += "{0} {1:N0} $([char]0xB0)C" -f $(if ($s.Name -match 'GPU') { 'GPU' } else { 'CPU' }), $s.Value } } catch {}
    if (-not ($temps -match '^CPU')) {
      try { $tz = @(Get-CimInstance -Namespace root\wmi -ClassName MSAcpi_ThermalZoneTemperature -ErrorAction Stop | ForEach-Object { $_.CurrentTemperature / 10 - 273.15 } | Where-Object { $_ -gt 15 -and $_ -lt 110 })
            if ($tz) { $temps += "CPU zone {0:N0} $([char]0xB0)C" -f ($tz | Measure-Object -Maximum).Maximum } } catch {}
    }
    try { foreach ($pd in (Get-PhysicalDisk -ErrorAction Stop)) { $rc = $pd | Get-StorageReliabilityCounter -ErrorAction Ignore; if ($rc.Temperature) { $temps += "Drive {0} {1} $([char]0xB0)C" -f $pd.DeviceId, $rc.Temperature } } } catch {}
    $r.Temps = $temps
  }
  $r
}

$script:HwData = @{}
function Apply-Sample($s) {
  foreach ($k in $s.Keys) { $script:HwData[$k] = $s[$k] }
  $d = $script:HwData
  function Push($key, $v) { $sp = $Spark[$key]; if ($null -eq $v) { return }; $sp.Data.Add([double]$v); while ($sp.Data.Count -gt 60) { $sp.Data.RemoveAt(0) }; $sp.Panel.Invalidate() }
  if ($null -ne $d.Cpu) {
    $HW.cpu.Value.Text = "$($d.Cpu)%"
    $ghz = if ($d.CpuPerf -and $HwStatic.MaxMhz) { "  $([char]0xB7)  {0:N2} GHz" -f ($HwStatic.MaxMhz * $d.CpuPerf / 100 / 1000) } else { "" }
    $HW.cpu.Sub.Text = "$($HwStatic.Cpu)`n$($HwStatic.Cores)$ghz"; Push 'cpu' $d.Cpu
  }
  if ($d.RamTotal) {
    $used = $d.RamTotal - $d.RamFree; $pct = [int]($used / $d.RamTotal * 100)
    $HW.ram.Value.Text = "$pct%"; $HW.ram.Sub.Text = ("$(Fmt $used) of $(Fmt $d.RamTotal) in use`nCommitted $(Fmt $d.Commit)" + $(if ($HwStatic.RamInfo) { "  " + [char]0xB7 + "  " + $HwStatic.RamInfo } else { "" })); Push 'ram' $pct
  }
  if ($null -ne $d.Gpu) { $HW.gpu.Value.Text = "$($d.Gpu)%"; Push 'gpu' $d.Gpu } else { $HW.gpu.Value.Text = "n/a" }
  $HW.gpu.Sub.Text = "$($HwStatic.Gpu)`n$(if ($d.GpuMem) { 'Video memory ' + (Fmt $d.GpuMem) + '  ' + [char]0xB7 + '  ' })$($HwStatic.GpuDrv)"
  if ($null -ne $d.DiskBusy) {
    $HW.disk.Value.Text = "$($d.DiskBusy)%"
    $HW.disk.Sub.Text = "Read $(Fmt $d.DiskRead)/s  $([char]0xB7)  Write $(Fmt $d.DiskWrite)/s`n$SysDrive $(Fmt $d.SysFree) free of $(Fmt $d.SysSize)"; Push 'disk' $d.DiskBusy
  }
  if ($null -ne $d.NetDown) {
    $HW.net.Value.Text = "{0:N1} Mbps" -f ($d.NetDown * 8 / 1MB)
    $HW.net.Sub.Text = "Download {0:N1} Mbps  $([char]0xB7)  Upload {1:N1} Mbps`nLive traffic on all adapters" -f ($d.NetDown * 8 / 1MB), ($d.NetUp * 8 / 1MB); Push 'net' ($d.NetDown * 8 / 1MB)
  }
  $tempTxt = if ($d.Temps) { ($d.Temps | Select-Object -First 3) -join '  $([char]0xB7)  ' } else { "Temperatures: not reported by this PC" }
  if ($null -ne $d.Bat) {
    $HW.bat.Title.Text = "Battery and temperatures"; $HW.bat.Value.Text = "$($d.Bat)%"
    $state = if ($d.BatAC) { "Plugged in" } elseif ($d.BatMin) { "{0}h {1:00}m left" -f [int][math]::Floor($d.BatMin / 60), ($d.BatMin % 60) } else { "On battery" }
    $HW.bat.Sub.Text = "$state`n$tempTxt"; Push 'bat' $d.Bat
  } else {
    $HW.bat.Title.Text = "System and temperatures"
    if ($d.Boot) { $up = (Get-Date) - $d.Boot; $HW.bat.Value.Text = "{0}d {1}h" -f $up.Days, $up.Hours }
    $HW.bat.Sub.Text = "Uptime (desktop - no battery)`n$tempTxt"
  }
}

# Background sampler: reads counters off the UI thread so the window never stutters
$HwSync = [hashtable]::Synchronized(@{ Run=$true; Active=$true; Data=$null; Seq=0 })
try {
  $rs = [runspacefactory]::CreateRunspace(); $rs.ApartmentState = 'MTA'; $rs.Open(); $rs.SessionStateProxy.SetVariable('HwSync', $HwSync)
  $ps = [powershell]::Create(); $ps.Runspace = $rs
  [void]$ps.AddScript({ param($code) $sb = [scriptblock]::Create($code); $t = 0
    while ($HwSync.Run) { if ($HwSync.Active) { try { $HwSync.Data = & $sb $t; $HwSync.Seq++ } catch {}; $t++ }; Start-Sleep -Milliseconds 1500 } }).AddArgument($SampleBlock.ToString())
  $script:HwHandle = $ps.BeginInvoke(); $script:HwPs = $ps
} catch { $HwSync.Run = $false }
$script:HwSeen = -1
function Update-Live {
  if ($HwSync.Seq -ne $script:HwSeen -and $HwSync.Data) { $script:HwSeen = $HwSync.Seq; Apply-Sample $HwSync.Data }
  elseif (-not $script:HwData.Count -or -not $HwSync.Run) { Apply-Sample (& $SampleBlock 0) }   # first paint / no background thread
}
$liveTimer = New-Object Windows.Forms.Timer -Property @{Interval=1000}
$liveTimer.Add_Tick({ $HwSync.Active = ($tabs.SelectedTab -eq $dash.Page -and $form.WindowState -ne 'Minimized'); if ($HwSync.Active) { Update-Live } })
$liveTimer.Start()
$form.Add_FormClosed({ $HwSync.Run = $false; $liveTimer.Stop() })
$script:HaveCharts = $true
Update-Live; Show-History

# First check runs automatically when the window opens (read-only)
$form.Add_Shown({ if (-not $TestMode) { $dash.Run.PerformClick() } })
