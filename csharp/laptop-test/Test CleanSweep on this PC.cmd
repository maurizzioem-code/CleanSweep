@echo off
title CleanSweep C# preview - laptop test
echo.
echo  CleanSweep C# preview - laptop test
echo  -----------------------------------
echo  This runs the same automated test as the Windows test machine (about 1-2 minutes).
echo   - Windows will ask for administrator permission: click Yes.
echo   - A CleanSweep window opens and clicks through the app by itself. Please don't touch it.
echo   - It cleans junk files (like Cleanup), runs a health check and creates one restore point.
echo   - Your CleanSweep settings are put back afterwards.
echo.
pause
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0run-test.ps1"
echo.
pause
