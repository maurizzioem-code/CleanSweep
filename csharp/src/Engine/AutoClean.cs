using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace CleanSweep.Engine
{
    /// <summary>Saved choices for automatic cleanup ("AutoClean" in settings.json, same keys as the PowerShell edition).</summary>
    public class AutoCleanConfig
    {
        public bool Enabled;
        public string Freq = "Weekly", Day = "Sunday";
        public int Minutes = 19 * 60, RecycleDays;
        public List<string> Cats = CleanEngine.DefaultOn.ToList();
        public bool ACOnly = true, Idle, CatchUp = true, Notify = true;

        public static readonly string[] Days = { "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday" };
        public static readonly (string Key, string Text)[] Freqs = { ("Daily", "Every day"), ("Weekly", "Every week"), ("Monthly", "Every 4 weeks") };

        public static bool Saved => Settings.Get("AutoClean") is Dictionary<string, object>;

        public static AutoCleanConfig Load()
        {
            var c = new AutoCleanConfig();
            if (!(Settings.Get("AutoClean") is Dictionary<string, object> d)) return c;
            object V(string k) => d.FirstOrDefault(kv => kv.Key.Equals(k, StringComparison.OrdinalIgnoreCase)).Value;
            bool B(string k, bool def) { var v = V(k); if (v is bool b) return b; return v != null && bool.TryParse(v.ToString(), out var r) ? r : def; }
            int I(string k, int def) { var v = V(k); try { return v == null ? def : Convert.ToInt32(v); } catch { return def; } }
            string S(string k, string def) => V(k)?.ToString() is string s && s.Length > 0 ? s : def;
            c.Enabled = B("Enabled", false); c.Freq = S("Freq", c.Freq); c.Day = S("Day", c.Day);
            c.Minutes = Math.Max(0, Math.Min(24 * 60 - 1, I("Minutes", c.Minutes))); c.RecycleDays = Math.Max(0, I("RecycleDays", 0));
            c.ACOnly = B("ACOnly", true); c.Idle = B("Idle", false); c.CatchUp = B("CatchUp", true); c.Notify = B("Notify", true);
            var cats = V("Cats");
            if (cats is string one) c.Cats = new List<string> { one };
            else if (cats is System.Collections.IEnumerable e) c.Cats = e.Cast<object>().Where(x => x != null).Select(x => x.ToString()).ToList();
            if (!Freqs.Any(f => f.Key == c.Freq)) c.Freq = "Weekly";
            if (!Days.Contains(c.Day)) c.Day = "Sunday";
            return c;
        }

        public void Save()
        {
            Settings.Set("AutoClean", new Dictionary<string, object>
            {
                ["Enabled"] = Enabled, ["Freq"] = Freq, ["Day"] = Day, ["Minutes"] = Minutes, ["Cats"] = Cats.ToArray(), ["RecycleDays"] = RecycleDays,
                ["ACOnly"] = ACOnly, ["Idle"] = Idle, ["CatchUp"] = CatchUp, ["Notify"] = Notify,
            });
            Settings.Save();
        }

        public static string TimeText(int minutes) => DateTime.Today.AddMinutes(minutes).ToString("h:mm tt", CultureInfo.CurrentCulture);
        public string Describe() => Freq == "Daily" ? "every day at " + TimeText(Minutes) : Freq == "Weekly" ? $"every {Day} at {TimeText(Minutes)}" : $"every 4 weeks on {Day} at {TimeText(Minutes)}";
    }

    public class AutoCleanRun { public DateTime Time; public string Trigger = ""; public long Freed; public int Files, Skipped, Seconds; public string Details = ""; }

    /// <summary>
    /// Automatic cleanup. Windows Task Scheduler starts "CleanSweep.exe --autoclean Scheduled" with no window, so it runs even
    /// when CleanSweep is closed. Same engine and safety rules as the Cleanup page: only the ticked junk categories, files
    /// changed in the last 24 hours, ignored files and files in use are skipped, links are never followed.
    /// Turning it off deletes the scheduled task completely.
    /// </summary>
    public static class AutoClean
    {
        public const string TaskFolder = @"\CleanSweep", TaskName = "Automatic cleanup";
        public static readonly string HistoryFile = Path.Combine(AppPaths.Dir, "autoclean-history.csv");
        public static readonly string LogFile = Path.Combine(AppPaths.Logs, "autoclean.log");
        /// <summary>The scheduled task runs this copy, so moving or deleting the downloaded exe doesn't break the schedule.</summary>
        public static readonly string RunnerExe = Path.Combine(AppPaths.Dir, "bin", "CleanSweep.exe");

        // ---------------------------------------------------------------- Task Scheduler (COM, no console windows)
        static dynamic Service() { dynamic s = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")); s.Connect(); return s; }
        static dynamic GetTask()
        {
            try { return Service().GetFolder(TaskFolder).GetTask(TaskName); } catch { return null; }
        }

        public class TaskState { public bool Exists, Enabled; public DateTime? NextRun, LastRun; public int LastResult; public string Command = "", Xml = ""; public bool RunsPowerShellEdition => Command.IndexOf("wscript", StringComparison.OrdinalIgnoreCase) >= 0 || Command.IndexOf("powershell", StringComparison.OrdinalIgnoreCase) >= 0; }
        public static TaskState State()
        {
            var st = new TaskState();
            try
            {
                var t = GetTask(); if (t == null) return st;
                st.Exists = true; st.Enabled = t.Enabled; st.Xml = t.Xml;
                DateTime n = t.NextRunTime; if (n.Year > 2000) st.NextRun = n;
                DateTime l = t.LastRunTime; if (l.Year > 2000) st.LastRun = l; try { st.LastResult = (int)t.LastTaskResult; } catch { }
                foreach (dynamic a in t.Definition.Actions) { try { st.Command = a.Path + " " + a.Arguments; } catch { } break; }
            }
            catch (Exception e) { Trace.Write("Task state: " + e.Message); }
            return st;
        }
        public static bool IsOn { get { var s = State(); return s.Exists && s.Enabled; } }

        static string X(string s) => SecurityElement.Escape(s ?? "");
        public static string TaskXml(AutoCleanConfig c, string exe)
        {
            var start = DateTime.Today.AddMinutes(c.Minutes);
            string sched;
            if (c.Freq == "Daily") sched = "<ScheduleByDay><DaysInterval>1</DaysInterval></ScheduleByDay>";
            else
            {
                // Start on the chosen weekday so "every 4 weeks" counts from the right day
                while (start.DayOfWeek.ToString() != c.Day) start = start.AddDays(1);
                sched = $"<ScheduleByWeek><DaysOfWeek><{c.Day} /></DaysOfWeek><WeeksInterval>{(c.Freq == "Monthly" ? 4 : 1)}</WeeksInterval></ScheduleByWeek>";
            }
            string who = WindowsIdentity.GetCurrent().Name;
            return $@"<?xml version=""1.0"" encoding=""UTF-16""?>
<Task version=""1.2"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">
  <RegistrationInfo>
    <Author>CleanSweep</Author>
    <Description>CleanSweep: removes the junk files chosen in CleanSweep (Dashboard &gt; Automatic cleanup). Turn it off in CleanSweep to remove this task.</Description>
  </RegistrationInfo>
  <Triggers>
    <CalendarTrigger>
      <StartBoundary>{start:yyyy-MM-ddTHH:mm:ss}</StartBoundary>
      <Enabled>true</Enabled>
      {sched}
    </CalendarTrigger>
  </Triggers>
  <Principals>
    <Principal id=""Author"">
      <UserId>{X(who)}</UserId>
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>HighestAvailable</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>{(c.ACOnly ? "true" : "false")}</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>{(c.ACOnly ? "true" : "false")}</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>{(c.CatchUp ? "true" : "false")}</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <IdleSettings>
      <Duration>PT10M</Duration>
      <WaitTimeout>PT2H</WaitTimeout>
      <StopOnIdleEnd>false</StopOnIdleEnd>
      <RestartOnIdle>false</RestartOnIdle>
    </IdleSettings>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>false</Hidden>
    <RunOnlyIfIdle>{(c.Idle ? "true" : "false")}</RunOnlyIfIdle>
    <WakeToRun>false</WakeToRun>
    <ExecutionTimeLimit>PT1H</ExecutionTimeLimit>
    <Priority>7</Priority>
  </Settings>
  <Actions Context=""Author"">
    <Exec>
      <Command>{X(exe)}</Command>
      <Arguments>--autoclean Scheduled</Arguments>
      <WorkingDirectory>{X(Path.GetDirectoryName(exe))}</WorkingDirectory>
    </Exec>
  </Actions>
</Task>";
        }

        /// <summary>Copies this exe to a fixed place for the scheduled task (only when it changed).</summary>
        public static string InstallRunner()
        {
            string me = Application.ExecutablePath;
            try
            {
                if (me.Equals(RunnerExe, StringComparison.OrdinalIgnoreCase)) return me;
                Directory.CreateDirectory(Path.GetDirectoryName(RunnerExe));
                var a = new FileInfo(me); var b = new FileInfo(RunnerExe);
                if (!b.Exists || b.Length != a.Length || b.LastWriteTimeUtc != a.LastWriteTimeUtc) { File.Copy(me, RunnerExe, true); File.SetLastWriteTimeUtc(RunnerExe, a.LastWriteTimeUtc); }
                return RunnerExe;
            }
            catch (Exception e) { Trace.Write("Runner copy failed, using " + me + ": " + e.Message); return File.Exists(RunnerExe) ? RunnerExe : me; }
        }

        /// <summary>Creates or updates the scheduled task and saves the choices.</summary>
        public static void Schedule(AutoCleanConfig c)
        {
            string exe = InstallRunner();
            RegisterXml(TaskXml(c, exe));
            c.Enabled = true; c.Save();
        }
        public static void RegisterXml(string xml)
        {
            dynamic svc = Service(); dynamic folder;
            try { folder = svc.GetFolder(TaskFolder); } catch { folder = svc.GetFolder(@"\").CreateFolder("CleanSweep"); }
            folder.RegisterTask(TaskName, xml, 6 /* CREATE_OR_UPDATE */, null, null, 3 /* INTERACTIVE_TOKEN */);
        }
        /// <summary>Asks Task Scheduler to start the task now (used by the self-test to check the real scheduled run).</summary>
        /// <summary>True when a laptop is running on battery (desktops and plugged-in laptops return false).</summary>
        public static bool OnBattery => System.Windows.Forms.SystemInformation.PowerStatus.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Offline;
        public static void RunTaskNow() { var t = GetTask(); if (t == null) throw new Exception("task not found"); t.Run(null); }
        public static void DeleteTaskOnly() { try { Service().GetFolder(TaskFolder).DeleteTask(TaskName, 0); } catch { } }

        /// <summary>Deletes the scheduled task (nothing is left behind in Task Scheduler).</summary>
        public static void Unschedule()
        {
            try { Service().GetFolder(TaskFolder).DeleteTask(TaskName, 0); } catch { }
            var c = AutoCleanConfig.Load(); c.Enabled = false; c.Save();
        }

        // ---------------------------------------------------------------- the cleanup itself (no window)
        static void Log(string t)
        {
            try
            {
                Directory.CreateDirectory(AppPaths.Logs);
                if (File.Exists(LogFile) && new FileInfo(LogFile).Length > 1 << 20) File.Copy(LogFile, LogFile + ".old", true);
                File.AppendAllText(LogFile, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + t + Environment.NewLine, Encoding.UTF8);
            }
            catch { }
        }

        /// <summary>Runs one cleanup with the saved choices. Returns null if another cleanup is already running.</summary>
        public static AutoCleanRun RunOnce(string trigger, CancellationToken ct = default)
        {
            using (var mutex = new Mutex(false, @"Local\CleanSweepAutoClean"))
            {
                bool got; try { got = mutex.WaitOne(0); } catch (AbandonedMutexException) { got = true; }
                if (!got) { Log("Another cleanup is already running - skipped."); return null; }
                try { return Clean(trigger, ct); }
                catch (Exception e) { Log("Error: " + e); return null; }
                finally { mutex.ReleaseMutex(); }
            }
        }

        static AutoCleanRun Clean(string trigger, CancellationToken ct)
        {
            Settings.Load();
            CleanEngine.IgnoreRules = Settings.GetList("TempIgnore");
            var c = AutoCleanConfig.Load();
            var sw = Stopwatch.StartNew(); var cut = DateTime.Now.AddDays(-1);
            var run = new AutoCleanRun { Time = DateTime.Now, Trigger = trigger }; var parts = new List<string>();
            Log($"Started ({trigger}). Categories: {string.Join(", ", c.Cats)}{(c.RecycleDays > 0 ? $"; Recycle Bin items older than {c.RecycleDays} days" : "")}");
            var rows = CleanEngine.CleanupRows().Where(r => r.Kind != TargetKind.Recycle).ToDictionary(r => r.Name, StringComparer.OrdinalIgnoreCase);
            foreach (var name in c.Cats)
            {
                if (ct.IsCancellationRequested) break;
                if (!rows.TryGetValue(name, out var t)) continue;   // category doesn't exist on this PC
                long before = run.Freed; var stat = new ScanStat();
                var items = t.Kind == TargetKind.Drive ? CleanEngine.DriveJunk(t.Drive, cut, stat, ct) : CleanEngine.FindFiles(t.Paths, cut, stat, ct);
                foreach (var f in items.ToList())
                {
                    if (ct.IsCancellationRequested) break;
                    if (CleanEngine.TryDelete(f) == null) { run.Freed += f.Length; run.Files++; } else run.Skipped++;
                }
                run.Skipped += stat.Recent;
                if (t.Kind == TargetKind.Paths && name.IndexOf("Temp", StringComparison.OrdinalIgnoreCase) >= 0)
                    foreach (var root in t.Paths) CleanEngine.RemoveEmptyDirs(root, cut);
                parts.Add($"{name} {Fmt.Size(run.Freed - before)}");
            }
            if (c.RecycleDays > 0 && !ct.IsCancellationRequested)
            {
                long before = run.Freed;
                foreach (var it in CleanEngine.OldRecycleItems(c.RecycleDays).ToList())
                    if (CleanEngine.TryDelete(it) == null) { run.Freed += it.Length; run.Files++; } else run.Skipped++;
                parts.Add($"Recycle Bin {Fmt.Size(run.Freed - before)}");
            }
            run.Seconds = (int)sw.Elapsed.TotalSeconds; run.Details = string.Join("; ", parts);
            Log($"{Summary(run)}  [{run.Details}]  {run.Seconds}s");
            SaveHistory(run);
            if (c.Notify && trigger == "Scheduled" && Environment.GetEnvironmentVariable("CLEANSWEEP_TEST") != "1") Notify(Summary(run));
            return run;
        }

        public static string Summary(AutoCleanRun r) => $"Freed {Fmt.Size(r.Freed)} ({Fmt.Files(r.Files)}). Skipped {Fmt.Count(r.Skipped, "file")} that were recent or in use.";

        static void Notify(string text)
        {
            try
            {
                using (var ni = new NotifyIcon())
                {
                    try { ni.Icon = new System.Drawing.Icon(System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("CleanSweep.ico")); } catch { ni.Icon = System.Drawing.SystemIcons.Information; }
                    ni.Text = "CleanSweep"; ni.Visible = true; ni.ShowBalloonTip(8000, "CleanSweep automatic cleanup", text, ToolTipIcon.Info);
                    var end = DateTime.Now.AddSeconds(9); while (DateTime.Now < end) { Application.DoEvents(); Thread.Sleep(200); }
                    ni.Visible = false;
                }
            }
            catch { }
        }

        // ---------------------------------------------------------------- history (autoclean-history.csv, shared with the PowerShell edition)
        static readonly string[] Cols = { "Time", "Trigger", "Freed", "Files", "Skipped", "Seconds", "Details" };
        static string Q(string s) => "\"" + (s ?? "").Replace("\"", "\"\"") + "\"";
        static void SaveHistory(AutoCleanRun r)
        {
            try
            {
                Directory.CreateDirectory(AppPaths.Dir);
                var lines = File.Exists(HistoryFile) ? File.ReadAllLines(HistoryFile).Where(l => l.Trim().Length > 0).ToList() : new List<string>();
                if (lines.Count == 0) lines.Add(string.Join(",", Cols.Select(Q)));
                lines.Add(string.Join(",", new[] { r.Time.ToString("o"), r.Trigger, r.Freed.ToString(), r.Files.ToString(), r.Skipped.ToString(), r.Seconds.ToString(), r.Details }.Select(Q)));
                if (lines.Count > 101) lines = new[] { lines[0] }.Concat(lines.Skip(lines.Count - 100)).ToList();   // keep the last 100 runs
                File.WriteAllLines(HistoryFile, lines, new UTF8Encoding(false));
            }
            catch (Exception e) { Log("History: " + e.Message); }
        }

        static List<string> SplitCsv(string line)
        {
            var r = new List<string>(); var sb = new StringBuilder(); bool q = false;
            for (int i = 0; i < line.Length; i++)
            {
                char ch = line[i];
                if (q) { if (ch == '"') { if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; } else q = false; } else sb.Append(ch); }
                else if (ch == '"') q = true; else if (ch == ',') { r.Add(sb.ToString()); sb.Clear(); } else sb.Append(ch);
            }
            r.Add(sb.ToString()); return r;
        }
        public static List<AutoCleanRun> History(string file = null)
        {
            var list = new List<AutoCleanRun>(); file = file ?? HistoryFile;
            try
            {
                if (!File.Exists(file)) return list;
                var lines = File.ReadAllLines(file); if (lines.Length < 2) return list;
                var cols = SplitCsv(lines[0].TrimStart('\uFEFF'));
                int Ix(string n) => cols.FindIndex(x => x.Equals(n, StringComparison.OrdinalIgnoreCase));
                int iT = Ix("Time"), iG = Ix("Trigger"), iF = Ix("Freed"), iN = Ix("Files"), iS = Ix("Skipped"), iSec = Ix("Seconds"), iD = Ix("Details");
                foreach (var l in lines.Skip(1))
                {
                    var v = SplitCsv(l); string G(int i) => i >= 0 && i < v.Count ? v[i] : "";
                    if (!DateTime.TryParse(G(iT), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t)) continue;
                    var r = new AutoCleanRun { Time = t.Kind == DateTimeKind.Utc ? t.ToLocalTime() : t, Trigger = G(iG), Details = G(iD) };
                    long.TryParse(G(iF), out r.Freed); int.TryParse(G(iN), out r.Files); int.TryParse(G(iS), out r.Skipped); int.TryParse(G(iSec), out r.Seconds);
                    list.Add(r);
                }
            }
            catch { }
            return list;
        }
    }
}
