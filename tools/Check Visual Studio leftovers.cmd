@echo off
rem CleanSweep - read-only check of the two Visual Studio "Application path" registry entries.
rem It only reads; nothing on your PC is changed. Results are saved to your Desktop.
powershell -NoProfile -ExecutionPolicy Bypass -Command "$s = (Get-Content -Raw -LiteralPath '%~f0') -split ('#' + 'PS' + '#'); Invoke-Expression $s[1]"
pause
exit /b
#PS#
$out = New-Object System.Collections.Generic.List[string]
function W($t) { $out.Add([string]$t); Write-Host $t }
function Ex($p) { if ([string]::IsNullOrWhiteSpace($p)) { return '(empty)' }; $q = [Environment]::ExpandEnvironmentVariables($p.Trim().Trim('"')); if (Test-Path -LiteralPath $q) { 'EXISTS' } else { 'MISSING' } }

W "CleanSweep - Visual Studio leftovers check ($(Get-Date -Format 'yyyy-MM-dd HH:mm'))"
W "Read-only: nothing was changed."
W ""
W "== 1. The two Application path entries =="
$hklm = [Microsoft.Win32.RegistryKey]::OpenBaseKey('LocalMachine', 'Registry64')
foreach ($k in 'SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\devenv.exe', 'SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\App Paths\devenv.exe') {
    W "HKLM\$k"
    $key = $hklm.OpenSubKey($k)
    if ($key -eq $null) { W "   (not there any more)"; continue }
    foreach ($n in $key.GetValueNames()) {
        $v = [string]$key.GetValue($n, $null, 'DoNotExpandEnvironmentNames')
        $label = if ($n -eq '') { '(Default)' } else { $n }
        $state = if ($n -eq '' -or $n -eq 'Path') { '  [' + (Ex $v) + ']' } else { '' }
        W "   $label = $v$state"
    }
    $key.Close()
}
W ""
W "== 2. Visual Studio installs the Visual Studio Installer knows about =="
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (Test-Path -LiteralPath $vswhere) {
    $j = & $vswhere -all -prerelease -products * -format json 2>$null | Out-String
    $list = @(); try { $list = @($j | ConvertFrom-Json) } catch { }
    if ($list.Count -eq 0) { W "   None - the Visual Studio Installer has no Visual Studio installed." }
    foreach ($i in $list) {
        W "   $($i.displayName)  version $($i.installationVersion)"
        W "      folder: $($i.installationPath)  [$(Ex $i.installationPath)]"
        W "      devenv: $($i.productPath)  [$(Ex $i.productPath)]"
        W "      complete: $($i.isComplete)   launchable: $($i.isLaunchable)"
    }
} else { W "   The Visual Studio Installer is not installed (no vswhere.exe)." }
W ""
W "== 3. Visual Studio in Settings > Apps (uninstall entries) =="
$found = 0
foreach ($root in 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall', 'SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall') {
    $u = $hklm.OpenSubKey($root); if ($u -eq $null) { continue }
    foreach ($s in $u.GetSubKeyNames()) {
        $sk = $u.OpenSubKey($s); if ($sk -eq $null) { continue }
        $dn = [string]$sk.GetValue('DisplayName')
        if ($dn -match 'Visual Studio' -and $dn -notmatch 'Code|Redistributable|Tools for Office|Runtime') {
            $loc = [string]$sk.GetValue('InstallLocation')
            W "   $dn  $([string]$sk.GetValue('DisplayVersion'))"
            if ($loc) { W "      folder: $loc  [$(Ex $loc)]" }
            $found++
        }
        $sk.Close()
    }
    $u.Close()
}
if ($found -eq 0) { W "   None." }
W ""
W "== 4. devenv.exe files on this PC (usual Visual Studio folders) =="
$roots = @()
foreach ($d in (Get-PSDrive -PSProvider FileSystem | Where-Object { $_.Root -match '^[A-Z]:\\$' })) {
    foreach ($pf in 'Program Files\Microsoft Visual Studio', 'Program Files (x86)\Microsoft Visual Studio', 'Microsoft Visual Studio') {
        $p = Join-Path $d.Root $pf; if (Test-Path -LiteralPath $p) { $roots += $p }
    }
}
$hits = 0
foreach ($r in $roots) {
    W "   Folder: $r"
    Get-ChildItem -LiteralPath $r -Directory -ErrorAction SilentlyContinue | ForEach-Object { W "      $($_.Name)" }
    Get-ChildItem -LiteralPath $r -Filter devenv.exe -Recurse -Depth 5 -ErrorAction SilentlyContinue | ForEach-Object { W "      FOUND: $($_.FullName)  ($([math]::Round($_.Length/1MB,1)) MB, $($_.LastWriteTime.ToString('yyyy-MM-dd')))"; $hits++ }
}
if ($roots.Count -eq 0) { W "   No Microsoft Visual Studio folders on any drive." }
elseif ($hits -eq 0) { W "   No devenv.exe found." }
W ""
W "== 5. Leftover installer records =="
$inst = 'C:\ProgramData\Microsoft\VisualStudio\Packages\_Instances'
if (Test-Path -LiteralPath $inst) { Get-ChildItem -LiteralPath $inst -Directory | ForEach-Object { W "   Instance record: $($_.Name)" } } else { W "   None." }

$file = Join-Path ([Environment]::GetFolderPath('Desktop')) 'Visual Studio check.txt'
$out | Set-Content -LiteralPath $file -Encoding UTF8
Write-Host ""
Write-Host "Saved to $file - please upload it in the chat."
Start-Process notepad.exe -ArgumentList ('"' + $file + '"')
