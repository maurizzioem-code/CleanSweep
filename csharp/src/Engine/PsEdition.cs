using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace CleanSweep.Engine
{
    /// <summary>
    /// Retires the PowerShell edition (4.x) once the C# edition is installed.
    ///   - The automatic cleanup keeps its exact schedule and conditions: only the task's action is switched
    ///     from the PowerShell script to the C# edition's own copy (Data\bin\CleanSweep.exe).
    ///   - The PowerShell edition's program files (scripts, modules, launcher) are moved - not deleted - to
    ///     "old PowerShell edition" in the data folder, with a note on how to put them back.
    ///   - Its shortcuts are moved there too (the installed C# edition has its own).
    ///   - Settings, history, logs and registry backups are shared and stay exactly where they are.
    /// Everything can be undone with Restore() until the old copy is deleted.
    /// </summary>
    public static class PsEdition
    {
        /// <summary>Files and folders the PowerShell edition's installer copies (app/ + the launcher exe). Nothing else is touched.</summary>
        public static readonly string[] AppFiles = { "CleanSweep.ps1", "CleanSweep.vbs", "AutoClean.ps1", "AutoClean.vbs", "CleanSweep.ico", "Install.ps1", "Install CleanSweep.bat", "CleanSweep.exe" };
        public static readonly string[] AppFolders = { "modules" };
        public const string OldFolderName = "old PowerShell edition";

        static string Data => Installer.DataDir;
        public static string OldDir => Path.Combine(Data, OldFolderName);
        public static string Script => Path.Combine(Data, "CleanSweep.ps1");
        public static string Launcher => Path.Combine(Data, "CleanSweep.exe");
        public static string RunnerExe => Path.Combine(Data, "bin", "CleanSweep.exe");

        public static bool Present => File.Exists(Script);
        public static bool Retired => !Present && File.Exists(Path.Combine(OldDir, "CleanSweep.ps1"));
        public static string Version
        {
            get
            {
                string f = Present ? Script : Path.Combine(OldDir, "CleanSweep.ps1");
                try { var m = Regex.Match(File.ReadAllText(f), "^\\$Version = \"([^\"]+)\"", RegexOptions.Multiline); return m.Success ? m.Groups[1].Value : "4.x"; } catch { return "4.x"; }
            }
        }
        public static long OldSize { get { try { return new DirectoryInfo(OldDir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length); } catch { return 0; } } }

        /// <summary>PowerShell edition windows that are open now (powershell.exe running CleanSweep.ps1, or its launcher).</summary>
        public static List<int> Running()
        {
            var r = new List<int>();
            string script = Script, launcher = Launcher;
            try
            {
                foreach (var p in Wmi.TryQuery("SELECT ProcessId, CommandLine, ExecutablePath FROM Win32_Process WHERE Name='powershell.exe' OR Name='CleanSweep.exe'"))
                {
                    string cmd = Wmi.Str(p, "CommandLine"), path = Wmi.Str(p, "ExecutablePath");
                    if (cmd.IndexOf(script, StringComparison.OrdinalIgnoreCase) >= 0 || cmd.IndexOf("CleanSweep.ps1", StringComparison.OrdinalIgnoreCase) >= 0 && cmd.IndexOf(Data, StringComparison.OrdinalIgnoreCase) >= 0
                        || string.Equals(path, launcher, StringComparison.OrdinalIgnoreCase))
                        r.Add((int)Wmi.Long(p, "ProcessId"));
                }
            }
            catch (Exception e) { Trace.Write("PowerShell edition process check: " + e.Message); }
            r.Remove(Process.GetCurrentProcess().Id);
            return r;
        }

        static bool PointsAtOld(string lnk)
        {
            if (!File.Exists(lnk)) return false;
            string t = Shortcuts.Target(lnk) ?? "", a = Shortcuts.Arguments(lnk) ?? "";
            return t.StartsWith(Data + "\\", StringComparison.OrdinalIgnoreCase) && !t.StartsWith(Path.Combine(Data, "bin") + "\\", StringComparison.OrdinalIgnoreCase)
                || t.EndsWith("wscript.exe", StringComparison.OrdinalIgnoreCase) && a.IndexOf(Path.Combine(Data, "CleanSweep.vbs"), StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// Swaps only the action of a PowerShell-edition task for the C# runner; triggers, conditions and the account stay as they are.
        /// Returns the new XML, or null when the XML has no action to replace.
        /// </summary>
        public static string SwitchTaskAction(string xml, string exe)
        {
            var m = Regex.Match(xml, "<Actions[^>]*>.*?</Actions>", RegexOptions.Singleline);
            if (!m.Success) return null;
            string ctx = Regex.Match(m.Value, "<Actions[^>]*>").Value;
            string X(string s) => System.Security.SecurityElement.Escape(s);
            string actions = $"{ctx}\n    <Exec>\n      <Command>{X(exe)}</Command>\n      <Arguments>--autoclean Scheduled</Arguments>\n      <WorkingDirectory>{X(Path.GetDirectoryName(exe))}</WorkingDirectory>\n    </Exec>\n  </Actions>";
            return xml.Substring(0, m.Index) + actions + xml.Substring(m.Index + m.Length);
        }
        /// <summary>The trigger and condition parts of a task (to show that a migration kept them).</summary>
        public static string ScheduleParts(string xml) =>
            string.Join("\n", new[] { "Triggers", "Settings", "Principals" }.Select(t => Regex.Match(xml ?? "", $"<{t}>.*?</{t}>", RegexOptions.Singleline).Value));

        /// <summary>Copies this exe to the automatic cleanup's own place (Data\bin) and returns that path.</summary>
        static string CopyRunner()
        {
            string me = Installer.Source;
            if (string.Equals(me, RunnerExe, StringComparison.OrdinalIgnoreCase)) return me;
            Directory.CreateDirectory(Path.GetDirectoryName(RunnerExe));
            for (int i = 0; ; i++)
            {
                try { File.Copy(me, RunnerExe, true); File.SetLastWriteTimeUtc(RunnerExe, File.GetLastWriteTimeUtc(me)); return RunnerExe; }
                catch (IOException) when (i < 20) { System.Threading.Thread.Sleep(250); }
            }
        }

        /// <summary>Moves the automatic cleanup to the C# edition (keeps days, time and conditions). Returns what was done.</summary>
        public static string MoveSchedule()
        {
            var st = AutoClean.State();
            if (!st.Exists) return "There was no automatic cleanup schedule to move.";
            if (!st.RunsPowerShellEdition) return "The automatic cleanup already runs the new CleanSweep.";
            string xml = SwitchTaskAction(st.Xml, CopyRunner()) ?? throw new Exception("the scheduled task could not be read");
            AutoClean.RegisterXml(xml);
            var now = AutoClean.State();
            if (now.RunsPowerShellEdition || !now.Exists) throw new Exception("the scheduled task still runs the PowerShell edition");
            if (!st.Enabled && now.Enabled) AutoClean.SetEnabled(false);
            return "Moved the automatic cleanup to the new CleanSweep - same days, time and conditions" +
                   (now.NextRun != null ? $" (next run {now.NextRun.Value:ddd MMM d, h:mm tt})." : ".");
        }

        /// <summary>Retires the PowerShell edition. The C# edition must be installed first. <paramref name="closeRunning"/> is asked before open windows are closed.</summary>
        public static List<string> Retire(Func<int, bool> closeRunning = null)
        {
            var log = new List<string>();
            if (!Present) throw new InvalidOperationException("the PowerShell edition is not on this PC");
            if (!Installer.IsInstalled) throw new InvalidOperationException("install this version of CleanSweep first");
            if (!Data.EndsWith(@"\CleanSweep", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("unexpected data folder " + Data);
            string ver = Version;

            var open = Running();
            if (open.Count > 0)
            {
                if (closeRunning != null && !closeRunning(open.Count)) throw new OperationCanceledException("the PowerShell edition is still open");
                foreach (var id in open) try { using (var p = Process.GetProcessById(id)) { p.Kill(); p.WaitForExit(5000); } } catch { }
                log.Add("Closed the open PowerShell edition window.");
            }

            // 1. the schedule first: if this fails nothing else has changed
            if (Installer.TouchSchedule) log.Add(MoveSchedule());

            // 2. program files -> "old PowerShell edition" (data files are never moved)
            Directory.CreateDirectory(OldDir);
            int moved = 0;
            foreach (var f in AppFiles)
            {
                string from = Path.Combine(Data, f), to = Path.Combine(OldDir, f);
                if (!File.Exists(from)) continue;
                for (int i = 0; ; i++)
                {
                    try { if (File.Exists(to)) File.Delete(to); File.Move(from, to); moved++; break; }
                    catch (IOException) when (i < 20) { System.Threading.Thread.Sleep(250); }
                }
            }
            foreach (var d in AppFolders)
            {
                string from = Path.Combine(Data, d), to = Path.Combine(OldDir, d);
                if (!Directory.Exists(from)) continue;
                if (Directory.Exists(to)) Directory.Delete(to, true);
                Directory.Move(from, to); moved++;
            }
            log.Add($"Moved the PowerShell edition's program files ({moved}) to \"{OldDir}\".");

            // 3. shortcuts: any left pointing at the old files, and the installer's backups of them
            string links = Path.Combine(OldDir, "shortcuts");
            foreach (var (lnk, tag, where) in new[] { (Installer.StartMenuLink, "start", "Start menu"), (Installer.DesktopLink, "desktop", "Desktop") })
            {
                if (PointsAtOld(lnk))
                {
                    Directory.CreateDirectory(links); string to = Path.Combine(links, tag + ".lnk");
                    File.Copy(lnk, to, true); File.Delete(lnk); log.Add($"Removed the PowerShell edition's {where} shortcut.");
                }
                string bak = Path.Combine(Installer.BackupDir, tag + ".lnk");
                if (File.Exists(bak)) { Directory.CreateDirectory(links); File.Copy(bak, Path.Combine(links, tag + ".lnk"), true); File.Delete(bak); }
            }
            try { if (Directory.Exists(Installer.BackupDir) && !Directory.EnumerateFileSystemEntries(Installer.BackupDir).Any()) Directory.Delete(Installer.BackupDir); } catch { }
            if (!File.Exists(Installer.StartMenuLink) && !File.Exists(Installer.DesktopLink))
            {
                // never leave the user without a way to open CleanSweep
                Directory.CreateDirectory(Installer.StartMenuDir);
                Installer.MakeLink(Installer.StartMenuLink, Installer.Exe, Installer.Dir, "CleanSweep - PC Health, Cleanup and Repair");
                log.Add("Added a Start menu shortcut for the new CleanSweep.");
            }

            File.WriteAllText(Path.Combine(OldDir, "READ ME.txt"),
                $"This is the PowerShell edition of CleanSweep ({ver}), retired on {DateTime.Now:MMMM d, yyyy}.\r\n\r\n" +
                "The new CleanSweep (C# edition) replaced it. Your settings, history and automatic cleanup schedule were kept.\r\n\r\n" +
                "To bring it back: open CleanSweep > Settings > PowerShell edition > Put it back.\r\n" +
                "To free the space: Settings > PowerShell edition > Delete the old copy (or delete this folder).\r\n");
            Settings.Set("PsEditionRetired", DateTime.Now.ToString("o")); Settings.Set("PsEditionVersion", ver); Settings.Save();
            log.Add("Kept your settings, history, logs and registry backups.");
            Trace.Write("Retire PowerShell edition: " + string.Join(" | ", log));
            return log;
        }

        /// <summary>Puts the PowerShell edition's files and shortcuts back. The automatic cleanup keeps running the new CleanSweep.</summary>
        public static List<string> Restore()
        {
            var log = new List<string>();
            if (!Retired) throw new InvalidOperationException("there is no retired PowerShell edition to put back");
            foreach (var f in AppFiles) { string from = Path.Combine(OldDir, f), to = Path.Combine(Data, f); if (File.Exists(from) && !File.Exists(to)) File.Move(from, to); }
            foreach (var d in AppFolders) { string from = Path.Combine(OldDir, d), to = Path.Combine(Data, d); if (Directory.Exists(from) && !Directory.Exists(to)) Directory.Move(from, to); }
            log.Add("Put the PowerShell edition's program files back.");
            string links = Path.Combine(OldDir, "shortcuts");
            foreach (var (lnk, tag, where) in new[] { (Installer.StartMenuLink, "start", "Start menu"), (Installer.DesktopLink, "desktop", "Desktop") })
            {
                string saved = Path.Combine(links, tag + ".lnk"); if (!File.Exists(saved)) continue;
                if (!File.Exists(lnk)) { File.Copy(saved, lnk); log.Add($"Put back its {where} shortcut."); }
                else { Directory.CreateDirectory(Installer.BackupDir); File.Copy(saved, Path.Combine(Installer.BackupDir, tag + ".lnk"), true); }   // put back if the new edition is uninstalled
            }
            try { Directory.Delete(OldDir, true); } catch { }
            Settings.Remove("PsEditionRetired"); Settings.Save();
            log.Add("The automatic cleanup keeps running the new CleanSweep (same schedule).");
            Trace.Write("Restore PowerShell edition: " + string.Join(" | ", log));
            return log;
        }

        /// <summary>Deletes the retired copy for good.</summary>
        public static void DeleteOld()
        {
            if (!Retired) return;
            Directory.Delete(OldDir, true);
            Trace.Write("Deleted the retired PowerShell edition");
        }
    }
}
