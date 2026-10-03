# ================================================================ TEMP FILES: temp file cleaner with a review list for files that can't be deleted
# Safety: only known temp folders; never follows junctions/symlinks out of them; skips files newer than the chosen age.
# Files that can't be deleted are never forced: CleanSweep shows why (in use, protected) and which app holds them,
# and the user chooses Retry, Delete at next restart, Ignore once, or Always ignore.

if (-not ("CSLocks" -as [type])) {
Add-Type -TypeDefinition @"
using System; using System.Text; using System.Collections.Generic; using System.Runtime.InteropServices;
public static class CSLocks {
  [StructLayout(LayoutKind.Sequential)] struct RM_UNIQUE_PROCESS { public int dwProcessId; public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime; }
  [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct RM_PROCESS_INFO {
    public RM_UNIQUE_PROCESS Process;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string strAppName;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string strServiceShortName;
    public int ApplicationType; public uint AppStatus; public uint TSSessionId; [MarshalAs(UnmanagedType.Bool)] public bool bRestartable; }
  [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)] static extern int RmStartSession(out uint h, int flags, StringBuilder key);
  [DllImport("rstrtmgr.dll")] static extern int RmEndSession(uint h);
  [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)] static extern int RmRegisterResources(uint h, uint nFiles, string[] files, uint nApps, RM_UNIQUE_PROCESS[] apps, uint nSvc, string[] svcs);
  [DllImport("rstrtmgr.dll")] static extern int RmGetList(uint h, out uint needed, ref uint n, [In, Out] RM_PROCESS_INFO[] info, ref uint reasons);
  [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool MoveFileEx(string from, string to, int flags);
  // Which apps have this file open (Windows Restart Manager - read-only query)
  public static string Who(string path) {
    uint h; var key = new StringBuilder(64);
    if (RmStartSession(out h, 0, key) != 0) return "";
    try {
      if (RmRegisterResources(h, 1, new string[] { path }, 0, null, 0, null) != 0) return "";
      uint needed = 0, n = 0, reasons = 0;
      int r = RmGetList(h, out needed, ref n, null, ref reasons);
      if (r != 234 || needed == 0) return "";
      var info = new RM_PROCESS_INFO[needed]; n = needed;
      if (RmGetList(h, out needed, ref n, info, ref reasons) != 0) return "";
      var names = new List<string>();
      for (int i = 0; i < n; i++) {
        string s = string.IsNullOrEmpty(info[i].strAppName) ? "Process" : info[i].strAppName;
        s += " (PID " + info[i].Process.dwProcessId + ")"; if (!names.Contains(s)) names.Add(s);
      }
      return string.Join(", ", names.ToArray());
    } finally { RmEndSession(h); }
  }
  // Ask Windows to delete the file during the next restart, before apps can lock it (needs admin)
  public static bool DeleteAtRestart(string path) { return MoveFileEx(path, null, 4); }
}
"@
}

$tcl = @{}
$tcl.Page   = New-Object Windows.Forms.TabPage -Property @{Text="Temp Files"; Padding='10,10,10,10'}
$tcl.Status = New-Object Windows.Forms.Label -Property @{Dock='Top'; Height=36; TextAlign='MiddleLeft'; Text="Click Scan to find temporary files."; Font=New-Object Drawing.Font("Segoe UI",12,[Drawing.FontStyle]::Bold)}
$tcl.Opts   = New-Object Windows.Forms.FlowLayoutPanel -Property @{Dock='Top'; AutoSize=$true; AutoSizeMode='GrowAndShrink'; WrapContents=$true; Padding='0,0,0,6'}
$oL = @{AutoSize=$true; Margin='0,7,6,0'}
$tcl.AgeL   = New-Object Windows.Forms.Label -Property ($oL + @{Text="Delete files older than"})
$tcl.Age    = New-Object Windows.Forms.ComboBox -Property @{DropDownStyle='DropDownList'; Width=110; Margin='0,3,18,0'}
$TempAges = [ordered]@{ "1 hour" = 1/24; "1 day" = 1; "7 days" = 7; "30 days" = 30 }
foreach ($k in $TempAges.Keys) { [void]$tcl.Age.Items.Add($k) }
$tcl.ModeL  = New-Object Windows.Forms.Label -Property ($oL + @{Text="If a file can't be deleted"})
$tcl.Mode   = New-Object Windows.Forms.ComboBox -Property @{DropDownStyle='DropDownList'; Width=260; Margin='0,3,18,0'}
$TempModes = [ordered]@{ Ask = "List it so I can decide"; Skip = "Skip it"; Restart = "Delete it when the PC restarts" }
foreach ($v in $TempModes.Values) { [void]$tcl.Mode.Items.Add($v) }
$tcl.IgnBtn = New-Object Windows.Forms.Button -Property @{AutoSize=$true; MinimumSize='130,32'; Margin='0,0,0,0'; Text="Ignore list"}
$tcl.Opts.Controls.AddRange(@($tcl.AgeL, $tcl.Age, $tcl.ModeL, $tcl.Mode, $tcl.IgnBtn))

$tcl.List = New-Object Windows.Forms.ListView -Property @{View='Details'; CheckBoxes=$true; FullRowSelect=$true; Dock='Top'; Height=170; HideSelection=$false}
foreach ($c in @(@("Location",210), @("Files",80), @("Size",90), @("Skipped",160), @("Folder",380))) { [void]$tcl.List.Columns.Add($c[0], $c[1]) }
$bp = @{AutoSize=$true; MinimumSize='110,36'; Margin='0,0,8,6'}
$tcl.Bar   = New-Object Windows.Forms.FlowLayoutPanel -Property @{Dock='Top'; AutoSize=$true; AutoSizeMode='GrowAndShrink'; WrapContents=$true; Padding='0,8,0,0'}
$tcl.Scan  = New-Object Windows.Forms.Button -Property ($bp + @{Text="Scan"})
$tcl.Clean = New-Object Windows.Forms.Button -Property ($bp + @{Text="Clean"; Enabled=$false})
$tcl.Stop  = New-Object Windows.Forms.Button -Property ($bp + @{Text="Cancel"; Enabled=$false})
$tcl.Bar.Controls.AddRange(@($tcl.Scan, $tcl.Clean, $tcl.Stop))
$tcl.Prog  = New-Object CSProgress -Property @{Dock='Top'; Height=10; Minimum=0; Maximum=100}
$tcl.PHdr  = New-Object Windows.Forms.Label -Property @{Dock='Top'; Height=44; TextAlign='BottomLeft'; Font=New-Object Drawing.Font("Segoe UI",10,[Drawing.FontStyle]::Bold)
  Text="Files that could not be deleted"}
$tcl.PHelp = New-Object Windows.Forms.Label -Property @{Dock='Top'; Height=22; ForeColor='DimGray'; AutoEllipsis=$true
  Text="Nothing is forced. Close the app shown under ""Used by"" and click Retry, or choose another action for the ticked files."}
$tcl.Probs = New-Object Windows.Forms.ListView -Property @{View='Details'; CheckBoxes=$true; FullRowSelect=$true; Dock='Fill'; HideSelection=$false}
foreach ($c in @(@("File",230), @("Size",80), @("Reason",200), @("Used by",190), @("Folder",360))) { [void]$tcl.Probs.Columns.Add($c[0], $c[1]) }
$tcl.PBar   = New-Object Windows.Forms.FlowLayoutPanel -Property @{Dock='Bottom'; AutoSize=$true; AutoSizeMode='GrowAndShrink'; WrapContents=$true; Padding='0,6,0,0'}
$tcl.Retry  = New-Object Windows.Forms.Button -Property ($bp + @{Text="Retry"})
$tcl.Reboot = New-Object Windows.Forms.Button -Property ($bp + @{Text="Delete at next restart"})
$tcl.Skip   = New-Object Windows.Forms.Button -Property ($bp + @{Text="Ignore this time"})
$tcl.Always = New-Object Windows.Forms.Button -Property ($bp + @{Text="Always ignore  " + [char]0x25BE})
$tcl.Open   = New-Object Windows.Forms.Button -Property ($bp + @{Text="Open location"})
$tcl.PAll   = New-Object Windows.Forms.Button -Property ($bp + @{Text="Tick all"})
$tcl.PBar.Controls.AddRange(@($tcl.Retry, $tcl.Reboot, $tcl.Skip, $tcl.Always, $tcl.Open, $tcl.PAll))
# Dock order: the last added docks first
foreach ($c in @($tcl.Probs, $tcl.PBar, $tcl.PHelp, $tcl.PHdr, $tcl.Prog, $tcl.Bar, $tcl.List, $tcl.Opts, $tcl.Status)) { $tcl.Page.Controls.Add($c) }

# "Always ignore" choices
$tcl.Menu = New-Object Windows.Forms.ContextMenuStrip -Property @{ShowImageMargin=$false; BackColor=$Theme.Card; ForeColor=$Theme.Text; RenderMode='System'}
$miFile = $tcl.Menu.Items.Add("This file"); $miFolder = $tcl.Menu.Items.Add("Everything in this folder"); $miExt = $tcl.Menu.Items.Add("All files of this type here")
$tcl.Always.Add_Click({ $tcl.Menu.Show($tcl.Always, 0, $tcl.Always.Height) })

# Put Temp Files right under Junk Files
$others = @($tabs.TabPages | ForEach-Object { $_ }); $tabs.TabPages.Clear()
foreach ($p in $others) { $tabs.TabPages.Add($p); if ($p -eq $junk.Page) { $tabs.TabPages.Add($tcl.Page) } }

# ---------------------------------------------------------------- settings
if (-not $settings.TempAge) { $settings.TempAge = "1 day" }
if (-not $settings.TempMode) { $settings.TempMode = "Ask" }
$tcl.Age.SelectedItem = [string]$settings.TempAge; if ($tcl.Age.SelectedIndex -lt 0) { $tcl.Age.SelectedIndex = 1 }
$tcl.Mode.SelectedIndex = [math]::Max(0, @($TempModes.Keys).IndexOf([string]$settings.TempMode))
$tcl.Age.Add_SelectedIndexChanged({ $settings.TempAge = [string]$tcl.Age.SelectedItem; Save-Settings; $tcl.Clean.Enabled = $false; $tcl.Status.Text = "Age changed - click Scan again." })
$tcl.Mode.Add_SelectedIndexChanged({ $settings.TempMode = @($TempModes.Keys)[$tcl.Mode.SelectedIndex]; Save-Settings })
function Get-TempIgnore { @($settings.TempIgnore | Where-Object { $_ }) }
function Set-TempIgnore($list) { $settings.TempIgnore = @($list | Select-Object -Unique); $global:CSIgnore = $settings.TempIgnore; Save-Settings; Update-IgnoreButton }
function Update-IgnoreButton { $tcl.IgnBtn.Text = "Ignore list ($(@(Get-TempIgnore).Count))" }
$global:CSIgnore = Get-TempIgnore; Update-IgnoreButton

# ---------------------------------------------------------------- locations
function Get-TempLocations {
  $me = try { (Get-Item -LiteralPath $env:TEMP -Force).FullName.TrimEnd('\') } catch { $env:TEMP }
  $rows = @(
    @{ Name = "Your temp folder"; Paths = @($me); On = $true }
    @{ Name = "Windows temp folder"; Paths = @("$env:WINDIR\Temp"); On = $true }
  )
  $mine = [Environment]::GetFolderPath('UserProfile')
  $otherT = @(Get-ChildItem -LiteralPath (Split-Path $mine) -Directory -Force -ErrorAction Ignore |
    Where-Object { $_.FullName -ne $mine -and -not ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) } |
    ForEach-Object { Join-Path $_.FullName "AppData\Local\Temp" } | Where-Object { (Test-Path -LiteralPath $_) -and $_ -ne $me })
  if ($otherT) { $rows += @{ Name = "Other users' temp folders"; Paths = $otherT; On = $true } }
  $rows += @{ Name = "Internet temporary files"; Paths = @("$env:LOCALAPPDATA\Microsoft\Windows\INetCache"); On = $true }
  $rows += @{ Name = "DirectX shader cache"; Paths = @("$env:LOCALAPPDATA\D3DSCache"); On = $false; Note = "games rebuild it (may stutter briefly)" }
  $rows | Where-Object { @($_.Paths | Where-Object { Test-Path -LiteralPath $_ }).Count }
}
function Load-TempList {
  $tcl.List.BeginUpdate(); $tcl.List.Items.Clear()
  foreach ($r in Get-TempLocations) {
    $i = $tcl.List.Items.Add($r.Name); $i.Checked = $r.On; $i.Tag = $r
    [void]$i.SubItems.Add("-"); [void]$i.SubItems.Add("-"); [void]$i.SubItems.Add($(if ($r.Note) { $r.Note } else { "" })); [void]$i.SubItems.Add(($r.Paths | Select-Object -First 2) -join "; ")
  }
  $tcl.List.EndUpdate()
}

# Walks a temp folder itself: never follows junctions or symbolic links (they could point outside the temp folder)
function Get-TempFilesIn([string]$root, [datetime]$cut, $stat) {
  $stack = New-Object System.Collections.Stack; $stack.Push($root); $n = 0
  while ($stack.Count) {
    $dir = $stack.Pop()
    if ((++$n % 100) -eq 0) { $tcl.Status.Text = "Scanning $dir"; Check-Cancel }
    $entries = try { ([IO.DirectoryInfo]$dir).GetFileSystemInfos() } catch { $stat.Unreadable++; @() }
    foreach ($e in $entries) {
      if ($e.Attributes -band [IO.FileAttributes]::Directory) {
        if ($e.Attributes -band [IO.FileAttributes]::ReparsePoint) { $stat.Links++; continue }
        $stack.Push($e.FullName); continue
      }
      if (Test-CSIgnored $e.FullName) { $stat.Ignored++; continue }
      if ($e.LastWriteTime -gt $cut) { $stat.Recent++; continue }
      $e
    }
  }
}

function Set-TempBusy([bool]$b) {
  $script:Cancel = $false; $tcl.Scan.Enabled = -not $b; $tcl.Stop.Enabled = $b; $tcl.List.Enabled = -not $b; $tcl.Opts.Enabled = -not $b; $tcl.PBar.Enabled = -not $b
  if ($b) { $tcl.Clean.Enabled = $false }
  $form.Cursor = if ($b) { 'WaitCursor' } else { 'Default' }
}
$tcl.Stop.Add_Click({ $script:Cancel = $true })

function Invoke-TempScan {
  $days = $TempAges[[string]$tcl.Age.SelectedItem]; $cut = (Get-Date).AddDays(-$days)
  Set-TempBusy $true; $total = 0; $count = 0
  try {
    foreach ($i in $tcl.List.Items) {
      $r = $i.Tag; $stat = @{ Recent = 0; Ignored = 0; Links = 0; Unreadable = 0 }
      if (-not $i.Checked) { $i.SubItems[1].Text = "-"; $i.SubItems[2].Text = "-"; $r.Files = @(); continue }
      $tcl.Status.Text = "Scanning $($r.Name)..."; Check-Cancel
      $files = @(foreach ($p in $r.Paths) { Get-TempFilesIn $p $cut $stat })
      $sz = [long]0; foreach ($f in $files) { $sz += $f.Length }
      $r.Files = $files; $r.Size = $sz; $total += $sz; $count += $files.Count
      $i.SubItems[1].Text = "{0:N0}" -f $files.Count; $i.SubItems[2].Text = Fmt $sz
      $sk = @(); if ($stat.Recent) { $sk += "$($stat.Recent) recent" }; if ($stat.Ignored) { $sk += "$($stat.Ignored) ignored" }; if ($stat.Links) { $sk += "$($stat.Links) links" }
      $i.SubItems[3].Text = if ($sk) { $sk -join ", " } elseif ($r.Note) { $r.Note } else { "" }
    }
    $tcl.Status.Text = "Found $(Fmt $total) in {0:N0} temp files older than $($tcl.Age.SelectedItem)." -f $count
    Set-TempBusy $false; $tcl.Clean.Enabled = ($count -gt 0)
  } catch { Set-TempBusy $false; $tcl.Status.Text = "Scan cancelled." }
}
$tcl.Scan.Add_Click({ Invoke-TempScan })

# Delete one file. Read-only files and very long paths are handled; anything else is reported, never forced.
function Remove-TempFile([string]$p) {
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
function Get-FailInfo($e, [string]$path) {
  $code = $e.HResult -band 0xFFFF
  if ($e -is [IO.IOException] -and $code -in 32, 33) {
    $who = try { [CSLocks]::Who($path) } catch { "" }
    return @{ Reason = "In use"; Who = $(if ($who) { $who } else { "Unknown app" }) }
  }
  if ($e -is [UnauthorizedAccessException]) { return @{ Reason = "Access denied (protected by Windows or another account)"; Who = "" } }
  if ($e -is [IO.PathTooLongException]) { return @{ Reason = "Path too long"; Who = "" } }
  @{ Reason = $e.Message; Who = "" }
}
function Add-TempProblem($f, $info) {
  $it = $tcl.Probs.Items.Add($f.Name); $it.Tag = @{ Path = $f.FullName; Size = $f.Length; Name = $f.Name; Dir = $f.DirectoryName }
  [void]$it.SubItems.Add((Fmt $f.Length)); [void]$it.SubItems.Add($info.Reason); [void]$it.SubItems.Add($info.Who); [void]$it.SubItems.Add($f.DirectoryName)
  if ($info.Reason -eq 'In use') { $it.ForeColor = $Theme.Warn }
}
function Update-ProbHeader {
  $n = $tcl.Probs.Items.Count
  $tcl.PHdr.Text = if ($n) { "Files that could not be deleted ($n)" } else { "Files that could not be deleted" }
  foreach ($b in @($tcl.Retry, $tcl.Reboot, $tcl.Skip, $tcl.Always, $tcl.Open, $tcl.PAll)) { $b.Enabled = ($n -gt 0) }
}
# Remove now-empty folders inside a temp root (never the root, never links)
function Remove-EmptyTempDirs([string]$root, [datetime]$cut) {
  $dirs = New-Object System.Collections.Generic.List[string]; $stack = New-Object System.Collections.Stack; $stack.Push($root)
  while ($stack.Count) { $d = $stack.Pop(); try { foreach ($s in ([IO.DirectoryInfo]$d).GetDirectories()) { if (-not ($s.Attributes -band [IO.FileAttributes]::ReparsePoint)) { $dirs.Add($s.FullName); $stack.Push($s.FullName) } } } catch {} }
  foreach ($d in ($dirs | Sort-Object Length -Descending)) {
    try { $di = [IO.DirectoryInfo]$d; if ($di.LastWriteTime -lt $cut -and -not $di.EnumerateFileSystemInfos().GetEnumerator().MoveNext()) { $di.Delete() } } catch {}
  }
}

function Invoke-TempClean {
  $mode = @($TempModes.Keys)[$tcl.Mode.SelectedIndex]
  $sel = @($tcl.List.Items | Where-Object { $_.Checked -and $_.Tag.Files })
  if (-not $sel) { return }
  $n = ($sel | ForEach-Object { $_.Tag.Files.Count } | Measure-Object -Sum).Sum
  $q = "Delete {0:N0} temp files from: $(($sel | ForEach-Object Text) -join ', ')?" -f $n
  if ([CSMsg]::Show($q, "Confirm", "YesNo", "Warning") -ne "Yes") { return }
  Set-TempBusy $true; $freed = [long]0; $done = 0; $failed = 0; $atBoot = 0; $k = 0
  $cut = (Get-Date).AddDays(-$TempAges[[string]$tcl.Age.SelectedItem])
  $tcl.Probs.BeginUpdate(); $tcl.Probs.Items.Clear()
  try {
    foreach ($i in $sel) {
      $tcl.Status.Text = "Cleaning $($i.Text)..."
      foreach ($f in $i.Tag.Files) {
        if ((++$k % 50) -eq 0) { $tcl.Prog.Value = [int](100 * $k / $n); Check-Cancel }
        $err = Remove-TempFile $f.FullName
        if (-not $err) { $freed += $f.Length; $done++; continue }
        $failed++
        if ($mode -eq 'Skip') { continue }
        if ($mode -eq 'Restart' -and [CSLocks]::DeleteAtRestart($f.FullName)) { $atBoot++; continue }
        Add-TempProblem $f (Get-FailInfo $err $f.FullName)
      }
      foreach ($p in $i.Tag.Paths) { Remove-EmptyTempDirs $p $cut }
      $i.Tag.Files = @(); $i.SubItems[1].Text = "Cleaned"
    }
    $msg = "Freed $(Fmt $freed) ({0:N0} files)." -f $done
  } catch { $msg = "Cleaning stopped. Freed $(Fmt $freed) before cancelling." }
  $tcl.Probs.EndUpdate(); $tcl.Prog.Value = 0; Set-TempBusy $false; Update-ProbHeader
  if ($failed) {
    $msg += switch ($mode) {
      'Skip'    { " Skipped $failed that could not be deleted." }
      'Restart' { " $atBoot will be deleted at the next restart" + $(if ($failed - $atBoot) { "; $($failed - $atBoot) are listed below." } else { "." }) }
      default   { " $failed could not be deleted - see the list below." }
    }
  }
  $tcl.Status.Text = $msg
}
$tcl.Clean.Add_Click({ Invoke-TempClean })

# ---------------------------------------------------------------- actions for files that could not be deleted
function Get-TickedProblems { $t = @($tcl.Probs.CheckedItems | ForEach-Object { $_ }); if (-not $t -and $tcl.Probs.SelectedItems.Count) { $t = @($tcl.Probs.SelectedItems[0]) }; $t }
function Need-Ticked { $t = Get-TickedProblems; if (-not $t) { [void][CSMsg]::Show("Tick the files first (or click Tick all).","CleanSweep","OK","Information") }; $t }
$tcl.PAll.Add_Click({ foreach ($it in $tcl.Probs.Items) { $it.Checked = $true } })
function Invoke-TempRetry {
  $items = Need-Ticked; if (-not $items) { return }
  $ok = 0; $freed = [long]0
  foreach ($it in $items) {
    $p = $it.Tag.Path
    if (-not (Test-Path -LiteralPath $p)) { $tcl.Probs.Items.Remove($it); $ok++; continue }
    $err = Remove-TempFile $p
    if (-not $err) { $freed += $it.Tag.Size; $tcl.Probs.Items.Remove($it); $ok++ }
    else { $info = Get-FailInfo $err $p; $it.SubItems[2].Text = $info.Reason; $it.SubItems[3].Text = $info.Who }
  }
  Update-ProbHeader
  $left = $items.Count - $ok
  $tcl.Status.Text = "Retry: deleted $ok file(s), freed $(Fmt $freed)." + $(if ($left) { " $left still can't be deleted - close the app shown, or choose another action." } else { "" })
}
$tcl.Retry.Add_Click({ Invoke-TempRetry })
function Invoke-TempAtRestart {
  $items = Need-Ticked; if (-not $items) { return }
  $ok = 0
  foreach ($it in $items) { if ([CSLocks]::DeleteAtRestart($it.Tag.Path)) { $tcl.Probs.Items.Remove($it); $ok++ } else { $it.SubItems[2].Text = "Windows refused to schedule this file" } }
  Update-ProbHeader; $tcl.Status.Text = "$ok file(s) will be deleted the next time you restart the PC."
}
$tcl.Reboot.Add_Click({ Invoke-TempAtRestart })
$tcl.Skip.Add_Click({ $items = Need-Ticked; foreach ($it in $items) { $tcl.Probs.Items.Remove($it) }; Update-ProbHeader; if ($items) { $tcl.Status.Text = "Left $($items.Count) file(s) alone this time." } })
function Add-TempIgnore([string]$kind) {
  $items = Need-Ticked; if (-not $items) { return }
  $new = foreach ($it in $items) {
    switch ($kind) {
      'file'   { "file:" + $it.Tag.Path }
      'folder' { "folder:" + $it.Tag.Dir.TrimEnd('\') + "\" }
      'ext'    { $x = [IO.Path]::GetExtension($it.Tag.Name); if ($x) { "ext:" + $it.Tag.Dir.TrimEnd('\') + "\*" + $x } else { "file:" + $it.Tag.Path } }
    }
  }
  Set-TempIgnore (@(Get-TempIgnore) + @($new))
  # Drop every listed file the new rules cover
  foreach ($it in @($tcl.Probs.Items | ForEach-Object { $_ })) { if (Test-CSIgnored $it.Tag.Path) { $tcl.Probs.Items.Remove($it) } }
  Update-ProbHeader; $tcl.Status.Text = "Added $(@($new | Select-Object -Unique).Count) rule(s) to the ignore list. CleanSweep (and automatic cleanup) will leave these files alone."
}
$miFile.Add_Click({ Add-TempIgnore 'file' }); $miFolder.Add_Click({ Add-TempIgnore 'folder' }); $miExt.Add_Click({ Add-TempIgnore 'ext' })
$tcl.Open.Add_Click({ $t = Get-TickedProblems | Select-Object -First 1; if ($t) { Start-Process explorer.exe -ArgumentList "/select,`"$($t.Tag.Path)`"" } })
$tcl.Probs.Add_DoubleClick({ if ($tcl.Probs.SelectedItems.Count) { Start-Process explorer.exe -ArgumentList "/select,`"$($tcl.Probs.SelectedItems[0].Tag.Path)`"" } })

# Ignore list window
function Format-IgnoreRule([string]$r) {
  switch -Regex ($r) { '^file:(.+)$' { "File:  $($matches[1])" } '^folder:(.+)$' { "Folder:  $($matches[1])" } '^ext:(.+)$' { "Type:  $($matches[1])" } default { $r } }
}
function Show-IgnoreList {
  $f = New-Object Windows.Forms.Form -Property @{Text="Ignore list"; Size='720,420'; StartPosition='CenterParent'; Font=$form.Font; Icon=$form.Icon; MinimizeBox=$false; ShowInTaskbar=$false}
  $hdr = New-Object Windows.Forms.Label -Property @{Dock='Top'; Height=44; Padding='12,10,12,0'; Text="CleanSweep never deletes these files, in the Temp Files page or during automatic cleanup."}
  $lb = New-Object Windows.Forms.ListBox -Property @{Dock='Fill'; SelectionMode='MultiExtended'; BorderStyle='None'; IntegralHeight=$false; BackColor=$Theme.Input; ForeColor=$Theme.Text}
  $rules = @(Get-TempIgnore); foreach ($r in $rules) { [void]$lb.Items.Add((Format-IgnoreRule $r)) }
  $bar = New-Object Windows.Forms.FlowLayoutPanel -Property @{Dock='Bottom'; Height=56; FlowDirection='RightToLeft'; Padding='12,10,12,10'; BackColor=$Theme.Side}
  $close = New-Object Windows.Forms.Button -Property @{Text="Close"; AutoSize=$true; MinimumSize='100,34'; DialogResult='OK'; Margin='8,0,0,0'}
  $rm = New-Object Windows.Forms.Button -Property @{Text="Remove selected"; AutoSize=$true; MinimumSize='140,34'; Margin='8,0,0,0'}
  $bar.Controls.AddRange(@($close, $rm)); $f.Controls.Add($lb); $f.Controls.Add($hdr); $f.Controls.Add($bar); $f.AcceptButton = $close
  $script:IgnUI = @{ Form = $f; List = $lb; Rules = $rules }
  $rm.Add_Click({ Remove-IgnoreSelected })
  Use-DarkDialog $f; $hdr.ForeColor = $Theme.Sub
  if ($TestMode) { $f.Show($form); [Windows.Forms.Application]::DoEvents() } else { [void]$f.ShowDialog($form); $f.Dispose() }
}
function Remove-IgnoreSelected {
  $u = $script:IgnUI; $idx = @($u.List.SelectedIndices | ForEach-Object { $_ })
  $keep = for ($n = 0; $n -lt $u.Rules.Count; $n++) { if ($idx -notcontains $n) { $u.Rules[$n] } }
  $u.Rules = @($keep); Set-TempIgnore $u.Rules
  $u.List.Items.Clear(); foreach ($r in $u.Rules) { [void]$u.List.Items.Add((Format-IgnoreRule $r)) }
}
$tcl.IgnBtn.Add_Click({ Show-IgnoreList })

Load-TempList; Update-ProbHeader
