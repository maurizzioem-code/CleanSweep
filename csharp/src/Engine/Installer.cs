using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace CleanSweep.Engine
{
    /// <summary>
    /// Per-user install, the way modern Windows apps do it (like VS Code or Teams):
    ///   %LOCALAPPDATA%\Programs\CleanSweep\CleanSweep.exe, a Start menu shortcut, an optional Desktop shortcut,
    ///   and an entry in Settings > Apps so it can be uninstalled like any other app.
    /// Nothing is added to Program Files, services, drivers or startup. Settings and history stay in %LOCALAPPDATA%\CleanSweep
    /// (shared with the PowerShell edition) and are kept on uninstall unless the user asks to remove them.
    /// Shortcuts that the PowerShell edition made are backed up and put back on uninstall.
    /// </summary>
    public static class Installer
    {
        public const string AppName = "CleanSweep", LinkName = "CleanSweep.lnk";
        // All overridable so the self-test can install into a sandbox
        public static string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "CleanSweep");
        public static string StartMenuDir = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        public static string DesktopDir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        public static string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\CleanSweep";
        public static string DataDir = AppPaths.Dir;
        public static string Source = Application.ExecutablePath;

        public static bool TouchSchedule = true;
        /// <summary>Self-test: keep every install path inside <paramref name="root"/> (never the real Start menu or Settings > Apps).</summary>
        public static void UseSandbox(string root)
        {
            Dir = Path.Combine(root, @"Programs\CleanSweep"); StartMenuDir = Path.Combine(root, "Start menu"); DesktopDir = Path.Combine(root, "Desktop");
            DataDir = Path.Combine(root, @"Data\CleanSweep"); UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\CleanSweepSelfTestInstall"; TouchSchedule = false;
        }
        public static string Exe => Path.Combine(Dir, "CleanSweep.exe");
        static string BackupDir => Path.Combine(Dir, "previous shortcuts");
        public static string StartMenuLink => Path.Combine(StartMenuDir, LinkName);
        public static string DesktopLink => Path.Combine(DesktopDir, LinkName);

        public static Version MyVersion => Assembly.GetExecutingAssembly().GetName().Version;
        public static Version VersionOf(string exe)
        {
            try { var v = FileVersionInfo.GetVersionInfo(exe); return new Version(v.FileMajorPart, v.FileMinorPart, v.FileBuildPart, v.FilePrivatePart); } catch { return null; }
        }
        public static string VersionLabel(Version v) => v == null ? "?" : $"{v.Major}.{v.Minor} preview (build {v.Revision})";

        public static bool IsInstalled => File.Exists(Exe);
        public static bool RunningInstalled => string.Equals(Path.GetFullPath(Source), Path.GetFullPath(Exe), StringComparison.OrdinalIgnoreCase);
        public static bool PowerShellEdition => File.Exists(Path.Combine(DataDir, "CleanSweep.ps1"));
        public static Version InstalledVersion => IsInstalled ? VersionOf(Exe) : null;
        /// <summary>The copy being run is newer than the installed one (or nothing is installed).</summary>
        public static bool Newer { get { var iv = InstalledVersion; return iv == null || MyVersion > iv; } }

        public class Options { public bool StartMenu = true, Desktop = true; }

        // ------------------------------------------------------------------ install / update
        /// <summary>Installs or updates. <paramref name="closeRunning"/> is asked before a running installed copy is closed.</summary>
        public static List<string> Install(Options o, Func<int, bool> closeRunning = null)
        {
            var log = new List<string>();
            if (!Dir.EndsWith(@"\CleanSweep", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("unexpected install folder " + Dir);
            Directory.CreateDirectory(Dir);
            bool update = File.Exists(Exe);

            if (!RunningInstalled)
            {
                var running = RunningCopies();
                if (running.Count > 0)
                {
                    if (closeRunning != null && !closeRunning(running.Count)) throw new OperationCanceledException("CleanSweep is still open");
                    foreach (var p in running) using (p)
                    {
                        try { if (p.MainWindowHandle != IntPtr.Zero) p.CloseMainWindow(); if (!p.WaitForExit(5000)) { p.Kill(); p.WaitForExit(5000); } } catch { }
                    }
                    log.Add($"Closed the open CleanSweep window ({running.Count}).");
                }
                // copy to a temp name first so a failed copy never leaves a broken exe behind
                string tmp = Exe + ".new";
                File.Copy(Source, tmp, true);
                for (int i = 0; ; i++)
                {
                    try { if (File.Exists(Exe)) File.Delete(Exe); File.Move(tmp, Exe); break; }
                    catch (IOException) when (i < 20) { Thread.Sleep(250); }
                }
                try { Unblock(Exe); } catch { }
                log.Add((update ? "Updated " : "Installed ") + Exe);
            }

            SetLink(StartMenuLink, "start", o.StartMenu, log, "Start menu");
            SetLink(DesktopLink, "desktop", o.Desktop, log, "Desktop");
            WriteUninstallEntry();
            log.Add("Added CleanSweep to Settings > Apps > Installed apps.");
            Trace.Write("Install: " + string.Join(" | ", log));
            return log;
        }

        /// <summary>Installed copies that are running now (not this process).</summary>
        public static List<Process> RunningCopies()
        {
            var me = Process.GetCurrentProcess().Id; var r = new List<Process>();
            foreach (var p in Process.GetProcessesByName("CleanSweep"))
            {
                bool ours = false;
                try { ours = p.Id != me && string.Equals(p.MainModule.FileName, Exe, StringComparison.OrdinalIgnoreCase); } catch { }
                if (ours) r.Add(p); else p.Dispose();
            }
            return r;
        }

        static void Unblock(string file) { try { File.Delete(file + ":Zone.Identifier"); } catch { } }

        static void SetLink(string lnk, string tag, bool want, List<string> log, string where)
        {
            string target = File.Exists(lnk) ? Shortcuts.Target(lnk) : null;
            bool ours = target != null && string.Equals(target, Exe, StringComparison.OrdinalIgnoreCase);
            if (want)
            {
                // keep the PowerShell edition's shortcut so uninstalling puts it back
                if (target != null && !ours)
                {
                    Directory.CreateDirectory(BackupDir);
                    string bak = Path.Combine(BackupDir, tag + ".lnk");
                    if (!File.Exists(bak)) File.Copy(lnk, bak);
                }
                Directory.CreateDirectory(Path.GetDirectoryName(lnk));
                MakeLink(lnk, Exe, Dir, "CleanSweep - PC Health, Cleanup and Repair");
                log.Add($"{where} shortcut: {lnk}");
            }
            else if (ours)
            {
                File.Delete(lnk); RestoreLink(lnk, tag);
                log.Add($"Removed the {where} shortcut.");
            }
        }
        static bool RestoreLink(string lnk, string tag)
        {
            string bak = Path.Combine(BackupDir, tag + ".lnk");
            if (!File.Exists(bak)) return false;
            try { File.Copy(bak, lnk, true); File.Delete(bak); return true; } catch { return false; }
        }

        static dynamic shell;
        public static void MakeLink(string lnk, string target, string workDir, string description)
        {
            shell = shell ?? Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
            if (File.Exists(lnk)) File.Delete(lnk);
            var s = shell.CreateShortcut(lnk);
            s.TargetPath = target; s.WorkingDirectory = workDir; s.IconLocation = target + ",0"; s.Description = description;
            s.Save();
        }

        static void WriteUninstallEntry()
        {
            long kb = 0; try { kb = new FileInfo(Exe).Length / 1024; } catch { }
            var v = VersionOf(Exe) ?? MyVersion;
            using (var k = Registry.CurrentUser.CreateSubKey(UninstallKey))
            {
                k.SetValue("DisplayName", AppName);
                k.SetValue("DisplayVersion", $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}");
                k.SetValue("Publisher", "CleanSweep");
                k.SetValue("DisplayIcon", Exe + ",0");
                k.SetValue("InstallLocation", Dir);
                k.SetValue("UninstallString", $"\"{Exe}\" --uninstall");
                k.SetValue("QuietUninstallString", $"\"{Exe}\" --uninstall --quiet");
                k.SetValue("URLInfoAbout", "https://github.com/maurizzioem-code/CleanSweep");
                k.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
                k.SetValue("EstimatedSize", (int)Math.Max(1, kb), RegistryValueKind.DWord);
                k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            }
        }
        public static Dictionary<string, object> UninstallEntry()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(UninstallKey))
                return k == null ? null : k.GetValueNames().ToDictionary(n => n, n => k.GetValue(n));
        }

        // ------------------------------------------------------------------ uninstall
        /// <summary>
        /// Removes the app, its shortcuts and its Settings > Apps entry, and puts back any PowerShell edition shortcuts.
        /// Settings, history and the automatic cleanup schedule are only removed when <paramref name="removeData"/> is set.
        /// The app folder is deleted right away, or a moment after this process exits when it runs from that folder.
        /// </summary>
        public static List<string> Uninstall(bool removeData)
        {
            var log = new List<string>();
            if (!Dir.EndsWith(@"\CleanSweep", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("unexpected install folder " + Dir);
            foreach (var (lnk, tag, where) in new[] { (StartMenuLink, "start", "Start menu"), (DesktopLink, "desktop", "Desktop") })
            {
                string t = File.Exists(lnk) ? Shortcuts.Target(lnk) : null;
                if (t != null && string.Equals(t, Exe, StringComparison.OrdinalIgnoreCase)) { try { File.Delete(lnk); log.Add($"Removed the {where} shortcut."); } catch { } }
                if (!File.Exists(lnk) && RestoreLink(lnk, tag)) log.Add($"Put back the PowerShell edition's {where} shortcut.");
            }
            try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false); log.Add("Removed CleanSweep from Settings > Apps."); } catch { }

            if (removeData)
            {
                var st = TouchSchedule ? AutoClean.State() : new AutoClean.TaskState();
                if (st.Exists && !st.RunsPowerShellEdition) { AutoClean.Unschedule(); log.Add("Removed the automatic cleanup schedule."); }
                if (PowerShellEdition)
                {
                    // settings are shared with the PowerShell edition: only remove what the C# edition added
                    foreach (var d in new[] { "bin" }) try { Directory.Delete(Path.Combine(DataDir, d), true); } catch { }
                    log.Add("Kept settings and history because the PowerShell edition still uses them.");
                }
                else if (DataDir.EndsWith(@"\CleanSweep", StringComparison.OrdinalIgnoreCase) && Directory.Exists(DataDir))
                {
                    try { Directory.Delete(DataDir, true); } catch { }
                    log.Add("Removed settings, history and logs.");
                }
            }
            else log.Add("Kept your settings and history in " + DataDir + ".");

            if (RunningInstalled || RunningCopies().Count > 0) { DeleteLater(Dir); log.Add("The app folder is removed when CleanSweep closes."); }
            else { try { Directory.Delete(Dir, true); log.Add("Removed " + Dir); } catch (Exception e) { DeleteLater(Dir); log.Add("Folder will be removed shortly (" + e.Message + ")."); } }
            Trace.Write("Uninstall: " + string.Join(" | ", log));
            return log;
        }

        /// <summary>Deletes a folder after this process has exited (a running exe can't delete itself).</summary>
        public static void DeleteLater(string dir)
        {
            if (!dir.EndsWith(@"\CleanSweep", StringComparison.OrdinalIgnoreCase)) return;
            int pid = Process.GetCurrentProcess().Id;
            string q = dir.Replace("'", "''");
            string ps = $"Wait-Process -Id {pid} -Timeout 60 -ErrorAction SilentlyContinue; " +
                        $"foreach ($i in 1..20) {{ Remove-Item -LiteralPath '{q}' -Recurse -Force -ErrorAction SilentlyContinue; if (-not (Test-Path -LiteralPath '{q}')) {{ break }}; Start-Sleep -Milliseconds 500 }}";
            try
            {
                Process.Start(new ProcessStartInfo(ConsoleTool.SysExe("WindowsPowerShell\\v1.0\\powershell.exe"),
                    "-NoProfile -NonInteractive -WindowStyle Hidden -Command \"" + ps.Replace("\"", "\\\"") + "\"")
                    // must not start inside the folder it deletes (a process's working folder can't be removed)
                    { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetTempPath() })?.Dispose();
            }
            catch (Exception e) { Trace.Write("DeleteLater failed: " + e.Message); }
        }
    }
}
