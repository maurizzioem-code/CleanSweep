# Automated test run of CleanSweep on a Windows VM (GitHub Actions).
# Loads the app without blocking, drives every tab, checks layout, takes screenshots and records errors.
param([string]$Out = "$PSScriptRoot\..\test-output")
$ErrorActionPreference = "Continue"
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
New-Item -ItemType Directory $Out -Force | Out-Null
$Out = (Resolve-Path $Out).Path
$env:CLEANSWEEP_TEST = "1"
$results = New-Object System.Collections.Generic.List[object]
$log = Join-Path $Out "log.txt"
function Note($t) { Write-Host $t; Add-Content -LiteralPath $log -Value $t }

function Step($name, [scriptblock]$body) {
  $before = $Error.Count; $sw = [Diagnostics.Stopwatch]::StartNew(); $ex = $null
  try { & $body } catch { $ex = $_ }
  $sw.Stop()
  $errs = @(); if ($Error.Count -gt $before) { $errs = @($Error[0..($Error.Count - $before - 1)] | ForEach-Object { "$_ (line $($_.InvocationInfo.ScriptLineNumber): $($_.InvocationInfo.Line.Trim()))" }) }
  if ($ex) { $errs = @("THROWN: $ex (line $($ex.InvocationInfo.ScriptLineNumber))") + $errs }
  $status = if ($errs) { "ISSUES" } else { "PASS" }
  $results.Add([pscustomobject]@{ Step=$name; Status=$status; Seconds=[math]::Round($sw.Elapsed.TotalSeconds,1); Errors=($errs | Select-Object -Unique) })
  Note "[$status] $name ($([math]::Round($sw.Elapsed.TotalSeconds,1))s)"; foreach ($e in ($errs | Select-Object -Unique)) { Note "    $e" }
}
function Shot($name) {
  [Windows.Forms.Application]::DoEvents(); Start-Sleep -Milliseconds 300; [Windows.Forms.Application]::DoEvents()
  $bmp = New-Object Drawing.Bitmap $form.Width, $form.Height
  $form.DrawToBitmap($bmp, (New-Object Drawing.Rectangle 0, 0, $form.Width, $form.Height))
  $bmp.Save((Join-Path $Out "$name.png"), [Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
}
# Every button/checkbox/combobox on the visible tab must be visible and fully inside the window
function Check-Layout($tabName) {
  $client = $form.ClientRectangle; $bad = @()
  $walk = { param($c) foreach ($k in $c.Controls) {
      if ($k -is [Windows.Forms.ButtonBase] -or $k -is [Windows.Forms.ComboBox]) {
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
$startOut = . "$PSScriptRoot\..\app\CleanSweep.ps1" *>&1 | Out-String
$sw.Stop()
$loadErrs = @(); if ($Error.Count -gt $before) { $loadErrs = @($Error[0..($Error.Count - $before - 1)] | ForEach-Object { "$_ (line $($_.InvocationInfo.ScriptLineNumber))" }) }
if ($startOut -match 'STARTUP ERROR') { $loadErrs += $startOut.Trim() }
if (-not $form) { $loadErrs += "Main window was not created" }
$results.Add([pscustomobject]@{ Step="App loads without errors"; Status=$(if ($loadErrs) { "ISSUES" } else { "PASS" }); Seconds=[math]::Round($sw.Elapsed.TotalSeconds,1); Errors=$loadErrs })
Note "[$(if ($loadErrs) { 'ISSUES' } else { 'PASS' })] App loads ($([math]::Round($sw.Elapsed.TotalSeconds,1))s)"; foreach ($e in $loadErrs) { Note "    $e" }
if (-not $form) { throw "Main window was not created" }
}
if (-not $form) { $results | ConvertTo-Json -Depth 4 | Set-Content "$Out\results.json"; exit 1 }
$form.StartPosition = 'Manual'; $form.Location = '0,0'
$form.Show(); [Windows.Forms.Application]::DoEvents()
Note "Window: $($form.Size)  Title: $($form.Text)  Tabs: $(($tabs.TabPages | ForEach-Object Text) -join ', ')"

# ---------------------------------------------------------------- layout at default and minimum size
foreach ($size in @($form.Size, $form.MinimumSize)) {
  $form.Size = $size; [Windows.Forms.Application]::DoEvents()
  foreach ($p in $tabs.TabPages) {
    Step "Layout: $($p.Text) at $($size.Width)x$($size.Height)" { Show-Tab $p; Check-Layout $p.Text }
    Shot ("layout-{0}x{1}-{2}" -f $size.Width, $size.Height, ($p.Text -replace '[^\w]', ''))
  }
}
$form.Size = '820,620'

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
  $t = New-Object Windows.Forms.Timer; $t.Interval = 300; $t.Add_Tick({ $junk.Stop.PerformClick(); $t.Stop() }.GetNewClosure()); $t.Start()
  $junk.Scan.PerformClick(); Note "    $($junk.Status.Text)"
}
foreach ($c in $script:DriveChecks) { $c.Checked = ($c.Tag -eq $SysDrive) }

# ---------------------------------------------------------------- registry
Show-Tab $reg.Page
Step "Registry: scan" { $reg.Scan.PerformClick(); Note "    $($reg.Status.Text)"; foreach ($i in ($reg.List.Items | Select-Object -First 15)) { Note "      $($i.Text) | $($i.SubItems[1].Text) | $($i.SubItems[2].Text)" } }
Shot "registry-after-scan"
Step "Registry: create restore point button" { $mkRp.PerformClick(); Note "    $($reg.Status.Text)" }
Step "Registry: clean (with backup)" {
  # add a known-bad test entry so cleaning always has something to do
  New-ItemProperty "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" -Name "CleanSweepTest" -Value '"C:\NoSuchFolder\missing.exe"' -Force | Out-Null
  $reg.Scan.PerformClick()
  $found = @($reg.List.Items | Where-Object { $_.SubItems[1].Text -like "*CleanSweepTest*" })
  if (-not $found) { Write-Error "Test startup entry pointing to a missing exe was not detected" }
  foreach ($i in $reg.List.Items) { $i.Checked = ($i.SubItems[1].Text -like "*CleanSweepTest*") }
  $reg.Clean.PerformClick(); Note "    $($reg.Status.Text)"
  if ((Get-ItemProperty "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" -Name CleanSweepTest -ErrorAction SilentlyContinue)) { Write-Error "Test entry was not removed" }
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
