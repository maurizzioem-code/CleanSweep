' Launches CleanSweep as Administrator with no console window
Dim fso, dir
Set fso = CreateObject("Scripting.FileSystemObject")
dir = fso.GetParentFolderName(WScript.ScriptFullName)
CreateObject("Shell.Application").ShellExecute "powershell.exe", _
  "-NoProfile -ExecutionPolicy Bypass -STA -WindowStyle Hidden -File """ & dir & "\CleanSweep.ps1""", _
  dir, "runas", 0
