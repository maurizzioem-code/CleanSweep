' Starts CleanSweep's automatic cleanup without flashing a console window (used by Task Scheduler)
Set sh = CreateObject("WScript.Shell")
dir = CreateObject("Scripting.FileSystemObject").GetParentFolderName(WScript.ScriptFullName)
trig = "Scheduled"
If WScript.Arguments.Count > 0 Then trig = WScript.Arguments(0)
sh.Run "powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File """ & dir & "\AutoClean.ps1"" -Trigger " & trig, 0, True
