# Builds CleanSweep.exe on Windows with the .NET Framework compiler (csc.exe)
param([string]$Out = "dist")
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot
$ver  = (Select-String "$root\app\CleanSweep.ps1" -Pattern '^\$Version = "(.+)"').Matches.Groups[1].Value
$parts = $ver.Split('.'); while ($parts.Count -lt 4) { $parts += "0" }; $fv = ($parts[0..3] -join '.')
$b = Join-Path $root "build"; Remove-Item $b -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory "$b\CleanSweep", (Join-Path $root $Out) -Force | Out-Null
Copy-Item "$root\app\*" "$b\CleanSweep" -Recurse
Compress-Archive -Path "$b\CleanSweep" -DestinationPath "$b\app.zip"
(Get-Content "$root\launcher\Launcher.cs" -Raw).Replace("__APPVERSION__", $ver).Replace("__FILEVERSION__", $fv) | Set-Content "$b\Launcher.cs"
$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
& $csc /nologo /target:winexe /optimize+ /out:"$b\CleanSweep.exe" /win32icon:"$root\app\CleanSweep.ico" /win32manifest:"$root\launcher\app.manifest" `
  /r:System.Windows.Forms.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll /resource:"$b\app.zip,app.zip" "$b\Launcher.cs"
if ($LASTEXITCODE -ne 0) { throw "csc failed" }
Copy-Item "$b\CleanSweep.exe" (Join-Path $root "$Out\CleanSweep.exe") -Force
Write-Host "Built CleanSweep.exe $fv"
