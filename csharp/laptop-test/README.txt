CleanSweep C# preview - laptop test

1. Right-click this zip > Extract All (the test can't run from inside the zip).
2. Double-click "Test CleanSweep on this PC.cmd".
   If Windows shows "Windows protected your PC", click More info > Run anyway.
3. Click Yes when Windows asks for administrator permission.
4. Don't touch the CleanSweep window while it tests itself (2-3 minutes).
5. Upload "CleanSweep test results.zip" from your Desktop in the chat.

What the test does on your PC:
- Runs the health check (read-only) and adds one entry to your score history.
- Cleans the recommended junk categories, like clicking Clean on the Cleanup page.
- Creates one System Restore point named "CleanSweep - manual restore point".
- Creates its own small test files in your Temp folder (removed by the test) and moves one
  test file to the Recycle Bin (you can empty it or leave it).
- Puts your CleanSweep settings back exactly as they were.
- Briefly creates the "CleanSweep\Automatic cleanup" scheduled task, runs it once
  (it only removes junk older than a day), then puts your own schedule back - or
  removes the task if you didn't have one.
- Drives and Repair: optimizes C: with Windows' own tool (the same TRIM Windows does
  every week), starts a chkdsk scan and a DISM scan and cancels them after a few seconds,
  and runs the read-only DISM health check (about a minute). It may create a System
  Restore point named "CleanSweep - before system repair". The Windows Update repair is
  tested on stand-in folders only - your real update files and services are not touched.
- The test window stays open until the test is finished, even if you click X.
- Creates a few large empty test files in C:\Users\Public, moves one to the Recycle Bin
  and removes it from there again, then deletes the rest. It also searches C: for
  large files for up to 90 seconds (it only reads, nothing is changed).
- Shortcuts: creates broken test shortcuts in C:\Users\Public\CleanSweepShortcutTest, moves
  them to the Recycle Bin and removes them from there, then only scans (reads) your own
  Desktop and Start menu - none of your shortcuts are removed.
- Registry: adds two fake entries of its own named "CleanSweepSelfTest", removes them
  with a backup, restores them from the backup and then deletes them. Your own registry
  entries are only scanned (read), never removed.
- Network: pings the internet (1.1.1.1) and looks up a few websites. The network fixes
  are only rehearsed - your connection, DNS and adapter settings are not changed.
- Installer: installs CleanSweep into a test folder in your Temp folder, starts it, updates
  it and uninstalls it again. Your real Start menu, Desktop and Settings > Apps are not
  changed. (To really install CleanSweep, just double-click CleanSweep-CSharp-preview.exe.)
