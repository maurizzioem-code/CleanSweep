# CleanSweep C# preview - laptop test (started by "Test CleanSweep on this PC.cmd")
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$exe = Join-Path $here 'CleanSweep-CSharp-preview.exe'
if (-not (Test-Path $exe)) { $exe = (Get-ChildItem $here -Filter 'CleanSweep*.exe' | Select-Object -First 1).FullName }
if (-not $exe) { Write-Host 'CleanSweep-CSharp-preview.exe was not found next to this file. Extract the whole zip first.' -ForegroundColor Red; exit 1 }
Unblock-File $exe -ErrorAction SilentlyContinue
$desk = [Environment]::GetFolderPath('Desktop')
$out = Join-Path $desk 'CleanSweep test results'
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
Write-Host 'Running the test - please wait and do not touch the CleanSweep window...'
try { $p = Start-Process -FilePath $exe -ArgumentList '--selftest', ('"' + $out + '"') -Verb RunAs -Wait -PassThru }
catch { Write-Host 'The test needs administrator permission (click Yes).' -ForegroundColor Red; exit 1 }
if (-not (Test-Path (Join-Path $out 'log.txt'))) { Write-Host 'The test did not produce a log.' -ForegroundColor Red; exit 1 }
$os = Get-CimInstance Win32_OperatingSystem; $cs = Get-CimInstance Win32_ComputerSystem
$dpi = (Get-ItemProperty 'HKCU:\Control Panel\Desktop\WindowMetrics' -ErrorAction SilentlyContinue).AppliedDPI
Add-Type -AssemblyName System.Windows.Forms
$scr = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
"$($os.Caption) $($os.Version) | $($cs.Manufacturer) $($cs.Model) | screen $($scr.Width)x$($scr.Height) | DPI $dpi | exit code $($p.ExitCode)" | Set-Content (Join-Path $out 'pc.txt')
$zip = Join-Path $desk 'CleanSweep test results.zip'
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $out '*') -DestinationPath $zip
$last = Get-Content (Join-Path $out 'log.txt') -Tail 1
Write-Host ''
Write-Host ('Result: ' + $last) -ForegroundColor $(if ($p.ExitCode -eq 0) { 'Green' } else { 'Yellow' })
Write-Host ('Results saved to: ' + $zip)
Write-Host 'Please upload that zip file in the chat.'
Start-Process explorer.exe ('/select,"' + $zip + '"')
