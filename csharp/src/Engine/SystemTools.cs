using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace CleanSweep.Engine
{
    public class ToolResult { public int Code; public string Text = ""; public bool Cancelled; public string LogFile; }

    /// <summary>
    /// Runs Windows' own console tools (DISM, sfc, chkdsk, defrag) hidden, with live output and a working Cancel.
    /// Output goes to a temp file (SFC writes UTF-16 when redirected, DISM the console code page) which is copied to the logs folder.
    /// </summary>
    public static class ConsoleTool
    {
        /// <summary>64-bit tools even from a 32-bit process (32-bit DISM can't service 64-bit Windows).</summary>
        public static string SysExe(string name)
        {
            string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string n = Path.Combine(win, "Sysnative", name);
            return File.Exists(n) ? n : Path.Combine(win, "System32", name);
        }
        public static bool IsAdmin { get { using (var id = WindowsIdentity.GetCurrent()) return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator); } }

        /// <summary>Raw tool output: progress lines overwrite themselves with \r, so keep only the last state of each line.</summary>
        public static string Format(string raw)
        {
            var lines = new List<string>();
            foreach (var l in Regex.Split(raw ?? "", "\r?\n"))
            {
                var parts = l.Split('\r').Where(p => p.Trim().Length > 0).ToList();
                if (parts.Count > 0) lines.Add(parts.Last().TrimEnd());
            }
            return string.Join("\r\n", lines);
        }
        public static string Decode(byte[] b)
        {
            if (b == null || b.Length == 0) return "";
            int zeros = 0, n = Math.Min(b.Length, 400);
            for (int i = 1; i < n; i += 2) if (b[i] == 0) zeros++;
            if (zeros > n / 4) return Encoding.Unicode.GetString(b).TrimStart('\uFEFF');
            Encoding oem; try { oem = Encoding.GetEncoding(System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage); } catch { oem = Encoding.Default; }
            return oem.GetString(b);
        }
        static string ReadShared(string file)
        {
            try { using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) using (var ms = new MemoryStream()) { fs.CopyTo(ms); return Decode(ms.ToArray()); } }
            catch { return ""; }
        }
        /// <summary>The last "NN%" or "NN.N%" in the output, if any.</summary>
        public static int? Percent(string text)
        {
            var m = Regex.Matches(text ?? "", @"(\d{1,3})(?:[.,]\d)?\s?%");
            if (m.Count == 0) return null;
            int v = int.Parse(m[m.Count - 1].Groups[1].Value); return v <= 100 ? v : (int?)null;
        }

        /// <summary>Runs the tool; <paramref name="update"/> gets (formatted output, percent) about twice a second on the caller's context.</summary>
        public static async Task<ToolResult> Run(string exe, string args, string title, Action<string, int?> update, CancellationToken ct)
        {
            Directory.CreateDirectory(AppPaths.Logs);
            string tmp = Path.Combine(Path.GetTempPath(), "CleanSweep-" + Guid.NewGuid().ToString("N") + ".txt");
            var psi = new ProcessStartInfo(SysExe("cmd.exe"), $"/c \"\"{exe}\" {args} > \"{tmp}\" 2>&1\"") { UseShellExecute = false, CreateNoWindow = true };
            var res = new ToolResult(); string shown = null;
            Trace.Write($"Tool start: {exe} {args}");
            using (var p = Process.Start(psi))
            {
                while (!p.HasExited)
                {
                    try { await Task.Delay(500, ct); } catch (OperationCanceledException) { }
                    if (ct.IsCancellationRequested) { KillTree(p.Id); res.Cancelled = true; break; }
                    var txt = Format(ReadShared(tmp));
                    if (txt != shown) { shown = txt; update?.Invoke(txt, Percent(txt)); }
                }
                p.WaitForExit(5000);
                res.Code = res.Cancelled ? -1 : (p.HasExited ? p.ExitCode : -1);
            }
            res.Text = Format(ReadShared(tmp));
            update?.Invoke(res.Text + (res.Cancelled ? "\r\n(Cancelled)" : ""), res.Cancelled ? (int?)null : Percent(res.Text));
            try
            {
                res.LogFile = Path.Combine(AppPaths.Logs, $"{Regex.Replace(title, @"[^\w]+", "-")}-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
                File.Copy(tmp, res.LogFile, true); File.Delete(tmp);
            }
            catch { }
            Trace.Write($"Tool end: {title} code {res.Code}{(res.Cancelled ? " (cancelled)" : "")}");
            return res;
        }
        /// <summary>Process IDs stopped by the last Cancel (the tool and everything it started).</summary>
        public static List<int> LastKilled = new List<int>();
        static List<int> Tree(int root)
        {
            var kids = new Dictionary<int, List<int>>();
            try
            {
                using (var q = new System.Management.ManagementObjectSearcher("SELECT ProcessId, ParentProcessId FROM Win32_Process"))
                    foreach (System.Management.ManagementObject o in q.Get())
                    {
                        int id = Convert.ToInt32(o["ProcessId"]), par = Convert.ToInt32(o["ParentProcessId"]);
                        if (!kids.TryGetValue(par, out var l)) kids[par] = l = new List<int>(); l.Add(id);
                    }
            }
            catch { }
            var all = new List<int> { root };
            for (int i = 0; i < all.Count; i++) if (kids.TryGetValue(all[i], out var l)) foreach (var k in l) if (!all.Contains(k)) all.Add(k);
            return all;
        }
        static void KillTree(int pid)
        {
            var tree = Tree(pid); LastKilled = tree;
            try { using (var k = Process.Start(new ProcessStartInfo(SysExe("taskkill.exe"), $"/PID {pid} /T /F") { UseShellExecute = false, CreateNoWindow = true })) k.WaitForExit(10000); } catch { }
            // make sure every process in the tree is really gone (taskkill can miss a child that was just starting)
            foreach (var id in tree)
                try { using (var p = Process.GetProcessById(id)) { if (!p.HasExited) { p.Kill(); } p.WaitForExit(10000); } } catch { }
        }
        public static bool AnyAlive(IEnumerable<int> ids) => ids.Any(id => { try { using (var p = Process.GetProcessById(id)) return !p.HasExited; } catch { return false; } });
    }

    /// <summary>Short Windows commands (netsh, ipconfig, reg, powercfg...) run hidden; returns exit code and output.</summary>
    public static class Cmd
    {
        public static (int Code, string Out) Run(string exe, string args, int timeoutMs = 60000)
        {
            try
            {
                var psi = new ProcessStartInfo(exe.Contains("\\") ? exe : ConsoleTool.SysExe(exe), args)
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                try { psi.StandardOutputEncoding = Encoding.GetEncoding(System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage); } catch { }
                using (var p = Process.Start(psi))
                {
                    var err = p.StandardError.ReadToEndAsync(); var o = p.StandardOutput.ReadToEnd();
                    if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } return (-1, o); }
                    Trace.Write($"cmd: {exe} {args} -> {p.ExitCode}");
                    return (p.ExitCode, o + err.Result);
                }
            }
            catch (Exception e) { return (-1, e.Message); }
        }
        /// <summary>Windows PowerShell for the few things only its cmdlets expose (network adapter advanced properties).</summary>
        public static (int Code, string Out) PowerShell(string script, int timeoutMs = 60000) =>
            Run(ConsoleTool.SysExe(@"WindowsPowerShell\v1.0\powershell.exe"),
                "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes("$ProgressPreference='SilentlyContinue'; " + script)), timeoutMs);
    }

    public enum ToolKind { Check, Repair, Undo, Cleanup }
    public class RepairTool
    {
        public string Id, Name, Time, What; public ToolKind Kind;
        public bool Changes => Kind == ToolKind.Repair || Kind == ToolKind.Cleanup;
    }
    /// <summary>A plain-language verdict: Level is OK, Repaired, Problem or Info.</summary>
    public class Verdict { public string Level, Text; public Verdict(string l, string t) { Level = l; Text = t; } public override string ToString() => Text; }

    /// <summary>Windows' own repair tools only. Same tool ids, result file and Windows Update undo journal as the main app.</summary>
    public static class Repair
    {
        public static readonly RepairTool[] Tools =
        {
            new RepairTool { Id = "dism-check", Name = "Quick Windows health check", Time = "1 min", Kind = ToolKind.Check,
                What = "Asks Windows if its image has been flagged as damaged (DISM /CheckHealth). Changes nothing." },
            new RepairTool { Id = "dism-scan", Name = "Scan Windows image", Time = "5-15 min", Kind = ToolKind.Check,
                What = "Thoroughly checks the Windows component store for damage (DISM /ScanHealth). Changes nothing." },
            new RepairTool { Id = "dism-restore", Name = "Repair Windows image", Time = "10-30 min", Kind = ToolKind.Repair,
                What = "Repairs the component store using fresh copies from Windows Update (DISM /RestoreHealth). Run this before System File Checker." },
            new RepairTool { Id = "sfc", Name = "System File Checker", Time = "10-20 min", Kind = ToolKind.Repair,
                What = "Checks all protected Windows files and replaces damaged ones with good copies (sfc /scannow)." },
            new RepairTool { Id = "wu-reset", Name = "Repair Windows Update", Time = "1-2 min", Kind = ToolKind.Repair,
                What = "For updates that fail or get stuck: restarts the update services and sets aside the update download cache and catroot2 (kept as backups so it can be undone). Re-enables update services if another tool disabled them." },
            new RepairTool { Id = "wu-undo", Name = "Undo Windows Update repair", Time = "1 min", Kind = ToolKind.Undo,
                What = "Puts back the update cache folders and service settings from the last Windows Update repair." },
            new RepairTool { Id = "wu-purge", Name = "Delete Windows Update repair backups", Time = "1 min", Kind = ToolKind.Cleanup,
                What = "Frees the space used by the backup folders once updates are working again. After this the repair can't be undone." },
            new RepairTool { Id = "dism-cleanup", Name = "Clean up old update components", Time = "5-20 min", Kind = ToolKind.Cleanup,
                What = "Lets Windows remove superseded update files from WinSxS (DISM /StartComponentCleanup). Safe, but older updates can no longer be uninstalled afterwards." },
        };
        public static RepairTool Tool(string id) => Tools.First(t => t.Id == id);
        public static readonly string[] Recommended = { "dism-restore", "sfc" };

        static string Win => Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        public static string ResultFile = Path.Combine(AppPaths.Dir, "repair-results.json");
        public static string Journal = Path.Combine(AppPaths.Dir, "wu-repair.json");
        public static string CbsLog => Path.Combine(Win, @"Logs\CBS\CBS.log");
        /// <summary>Folders the Windows Update repair renames (overridable by the self-test so it can use stand-in folders).</summary>
        public static string[] WuFolders = { Path.Combine(Win, "SoftwareDistribution"), Path.Combine(Win, @"System32\catroot2") };
        public static string[] WuServices = { "wuauserv", "bits", "cryptsvc", "UsoSvc" };
        static readonly Dictionary<string, string> DefaultStart = new Dictionary<string, string> { ["wuauserv"] = "Manual", ["bits"] = "Manual", ["cryptsvc"] = "Automatic", ["UsoSvc"] = "Automatic" };
        /// <summary>The self-test sets this so services aren't touched.</summary>
        public static bool SkipServices;

        // ---------------------------------------------------------------- last results (shared JSON: { id: "MMM d HH:mm - text" })
        public static Dictionary<string, string> Results()
        {
            try
            {
                if (File.Exists(ResultFile))
                {
                    var d = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(ResultFile).TrimStart('\uFEFF'));
                    return d.ToDictionary(k => k.Key, k => k.Value?.ToString() ?? "");
                }
            }
            catch { }
            return new Dictionary<string, string>();
        }
        public static void SetResult(string id, string text)
        {
            var r = Results(); r[id] = DateTime.Now.ToString("MMM d HH:mm", System.Globalization.CultureInfo.InvariantCulture) + " - " + text;
            try { Directory.CreateDirectory(AppPaths.Dir); File.WriteAllText(ResultFile, new JavaScriptSerializer().Serialize(r), new UTF8Encoding(true)); } catch { }
        }

        /// <summary>When a stored result was recorded ("MMM d HH:mm - ..." - no year, so it's the most recent such date).</summary>
        public static DateTime? ResultTime(string stored)
        {
            if (string.IsNullOrEmpty(stored)) return null;
            int dash = stored.IndexOf(" - "); if (dash < 0) return null;
            if (!DateTime.TryParseExact(stored.Substring(0, dash), "MMM d HH:mm", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d)) return null;
            if (d > DateTime.Now.AddDays(1)) d = d.AddYears(-1);
            return d;
        }
        /// <summary>For a check that found damage: the later repair that succeeded ("Repair Windows image Oct 3"), or null.</summary>
        public static string FixedLater(string id, Dictionary<string, string> res)
        {
            var when = ResultTime(res.TryGetValue(id, out var v) ? v : null); if (when == null) return null;
            string[] fixers = id == "sfc" ? new[] { "sfc" } : new[] { "dism-restore" };
            foreach (var f in fixers)
            {
                if (f == id || !res.TryGetValue(f, out var r)) continue;
                var t = ResultTime(r);
                if (t > when && (r.Contains("healthy") || r.Contains("No damage") || r.Contains("No problems"))) return $"{Tool(f).Name} {t:MMM d}";
            }
            // sfc "some files could not be repaired" is fixed by a later clean sfc run
            if (id == "sfc") return null;
            return null;
        }

        public static List<DirectoryInfo> WuBackups() =>
            WuFolders.SelectMany(f => { try { var p = new DirectoryInfo(Path.GetDirectoryName(f)); return p.GetDirectories(Path.GetFileName(f) + ".bak-*"); } catch { return new DirectoryInfo[0]; } }).ToList();
        public static bool CanUndo => File.Exists(Journal);
        /// <summary>Tools to list right now (undo/purge only when there's something to undo/purge).</summary>
        public static IEnumerable<RepairTool> Available()
        {
            bool j = CanUndo, b = WuBackups().Count > 0;
            return Tools.Where(t => (t.Id != "wu-undo" || j) && (t.Id != "wu-purge" || b));
        }

        // ---------------------------------------------------------------- verdicts (English text first, exit code as fallback for other languages)
        public static Verdict Judge(string id, ToolResult r)
        {
            if (r.Cancelled) return new Verdict("Info", "Cancelled");
            string t = r.Text ?? ""; string hex = "0x" + r.Code.ToString("X8");
            bool Has(string pattern) => Regex.IsMatch(t, pattern, RegexOptions.IgnoreCase);
            if (id == "sfc")
            {
                if (Has("did not find any integrity violations")) return new Verdict("OK", "No problems found");
                if (Has("successfully repaired")) return new Verdict("Repaired", "Found and repaired damaged files - restart your PC");
                if (Has("unable to fix|not able to fix")) return new Verdict("Problem", "Some files could not be repaired - run Repair Windows image, restart, then run this again");
                if (Has("repair pending")) return new Verdict("Problem", "A repair is waiting for a restart - restart and run this again");
                if (Has("could not perform")) return new Verdict("Problem", "Windows could not run the check - restart and try again");
                if (r.Code == 0) return new Verdict("OK", "Finished (see the output for details)");
            }
            else if (id.StartsWith("dism-"))
            {
                if (r.Code == 0)
                {
                    if (Has("No component store corruption detected")) return new Verdict("OK", "No damage found");
                    if (Has("is repairable")) return new Verdict("Problem", "Damage found - run Repair Windows image");
                    if (Has("restore operation completed successfully")) return new Verdict("Repaired", "Windows image is healthy (repaired if needed) - now run System File Checker");
                    return new Verdict("OK", "Completed successfully");
                }
                if (hex == "0x800F081F" || hex == "0x800F0906") return new Verdict("Problem", $"Repair files could not be downloaded ({hex}) - check your internet connection and Windows Update, then try again");
                if (hex == "0x800F0954") return new Verdict("Problem", $"Blocked by a Windows Update policy ({hex}) - common on work-managed PCs");
                if (hex == "0x800F0806" || r.Code == 3010) return new Verdict("Problem", "Another update or repair is pending - restart and try again");
                if (hex == "0x800F082F") return new Verdict("Problem", "A restart is needed to finish earlier changes - restart and try again");
            }
            return new Verdict("Problem", $"Failed (code {hex}) - see the output and log");
        }

        public static (string Exe, string Args) Command(string id)
        {
            switch (id)
            {
                case "dism-check": return (ConsoleTool.SysExe("Dism.exe"), "/Online /Cleanup-Image /CheckHealth");
                case "dism-scan": return (ConsoleTool.SysExe("Dism.exe"), "/Online /Cleanup-Image /ScanHealth");
                case "dism-restore": return (ConsoleTool.SysExe("Dism.exe"), "/Online /Cleanup-Image /RestoreHealth");
                case "dism-cleanup": return (ConsoleTool.SysExe("Dism.exe"), "/Online /Cleanup-Image /StartComponentCleanup");
                case "sfc": return (ConsoleTool.SysExe("sfc.exe"), "/scannow");
            }
            return (null, null);
        }

        // ---------------------------------------------------------------- Windows Update repair (with undo journal)
        public class JournalData
        {
            public string Date;
            public List<Dictionary<string, string>> Renamed = new List<Dictionary<string, string>>();
            public Dictionary<string, string> StartTypes = new Dictionary<string, string>();
        }
        public static JournalData ReadJournal()
        {
            try
            {
                var d = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(Journal).TrimStart('\uFEFF'));
                var j = new JournalData { Date = d.TryGetValue("Date", out var dt) ? dt?.ToString() : "" };
                if (d.TryGetValue("Renamed", out var rn))
                {
                    IEnumerable<object> items = rn is System.Collections.ArrayList al ? al.Cast<object>() : rn is object[] arr ? arr : rn is Dictionary<string, object> one ? new object[] { one } : new object[0];
                    foreach (var o in items.OfType<Dictionary<string, object>>()) j.Renamed.Add(new Dictionary<string, string> { ["From"] = o["From"]?.ToString(), ["To"] = o["To"]?.ToString() });
                }
                if (d.TryGetValue("StartTypes", out var stt) && stt is Dictionary<string, object> sd) foreach (var kv in sd) j.StartTypes[kv.Key] = kv.Value?.ToString();
                return j;
            }
            catch { return null; }
        }

        static string StartMode(string svc) => Wmi.TryQuery($"SELECT StartMode FROM Win32_Service WHERE Name='{svc}'").Select(o => Wmi.Str(o, "StartMode")).FirstOrDefault();
        static void SetStart(string svc, string mode)
        {
            string sc = mode == "Disabled" ? "disabled" : mode == "Automatic" || mode == "Auto" ? "auto" : "demand";
            using (var p = Process.Start(new ProcessStartInfo(ConsoleTool.SysExe("sc.exe"), $"config {svc} start= {sc}") { UseShellExecute = false, CreateNoWindow = true })) p.WaitForExit(15000);
        }
        static void StopServices(Action<string> log, CancellationToken ct)
        {
            if (SkipServices) { log("(test) Update services left running"); return; }
            foreach (var s in WuServices)
            {
                try
                {
                    using (var svc = new ServiceController(s))
                    {
                        if (svc.Status == ServiceControllerStatus.Stopped) continue;
                        log($"Stopping {svc.DisplayName}...");
                        try { svc.Stop(); } catch (Exception e) { log($"  could not stop {s} right away: {e.Message}"); }
                        try { svc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30)); } catch { }
                    }
                }
                catch { }
            }
        }
        static void StartServices(Action<string> log)
        {
            if (SkipServices) return;
            foreach (var s in new[] { "cryptsvc", "bits", "wuauserv", "UsoSvc" })
            {
                try
                {
                    if (StartMode(s) == "Disabled") continue;
                    using (var svc = new ServiceController(s))
                    {
                        if (svc.Status == ServiceControllerStatus.Running) continue;
                        try { svc.Start(); log($"Started {svc.DisplayName}"); } catch (Exception e) { log($"  {s} will start when needed ({e.InnerException?.Message ?? e.Message})"); }
                    }
                }
                catch { }
            }
        }

        public static Verdict WuReset(Action<string> log, CancellationToken ct)
        {
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var j = new JournalData { Date = DateTime.Now.ToString("s") };
            // 1. Services another "optimizer" may have disabled
            if (!SkipServices)
                foreach (var s in WuServices)
                    if (StartMode(s) == "Disabled")
                    {
                        j.StartTypes[s] = "Disabled"; SetStart(s, DefaultStart[s]);
                        log($"{s} was disabled - set back to the Windows default ({DefaultStart[s]})");
                    }
            // 2. Stop, set aside the caches (renamed, never deleted), start again
            StopServices(log, ct);
            foreach (var f in WuFolders)
            {
                if (!Directory.Exists(f)) continue;
                string to = f + ".bak-" + stamp;
                try { Directory.Move(f, to); j.Renamed.Add(new Dictionary<string, string> { ["From"] = f, ["To"] = to }); log($"Set aside {f}  ->  {Path.GetFileName(to)}"); }
                catch (Exception e) { log($"Could not set aside {f} ({e.Message}) - a restart usually releases it"); }
            }
            if (j.Renamed.Count > 0 || j.StartTypes.Count > 0) SaveJournal(j);
            StartServices(log);
            log("Windows will rebuild the update cache on the next check for updates.");
            if (!Msg.Test) Shell.Open("ms-settings:windowsupdate-action");
            int need = WuFolders.Count(f => true);
            if (j.Renamed.Count == need) return new Verdict("Repaired", "Update components reset - Windows Update is checking again (undo available)");
            if (j.Renamed.Count > 0) return new Verdict("Repaired", $"Partly reset ({j.Renamed.Count} of {need} folders) - restart and run again if updates still fail");
            return new Verdict("Problem", "The update folders were in use - restart your PC and run this again");
        }
        static void SaveJournal(JournalData j)
        {
            Directory.CreateDirectory(AppPaths.Dir);
            var o = new Dictionary<string, object> { ["Date"] = j.Date, ["Renamed"] = j.Renamed, ["StartTypes"] = j.StartTypes };
            File.WriteAllText(Journal, new JavaScriptSerializer().Serialize(o), new UTF8Encoding(true));
        }
        public static Verdict WuUndo(Action<string> log, CancellationToken ct)
        {
            var j = File.Exists(Journal) ? ReadJournal() : null;
            if (j == null) return new Verdict("Info", "Nothing to undo");
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss"); bool ok = true;
            StopServices(log, ct);
            foreach (var r in j.Renamed)
            {
                string from = r["From"], to = r["To"];
                if (!Directory.Exists(to)) { log($"Backup {to} no longer exists"); ok = false; continue; }
                try
                {
                    if (Directory.Exists(from)) Directory.Move(from, from + ".rebuilt-" + stamp);
                    Directory.Move(to, from); log($"Restored {from}");
                }
                catch (Exception e) { log($"Could not restore {from}: {e.Message}"); ok = false; }
            }
            if (!SkipServices) foreach (var kv in j.StartTypes) { SetStart(kv.Key, kv.Value); log($"Service {kv.Key} set back to {kv.Value}"); }
            StartServices(log);
            if (ok) { try { File.Delete(Journal); } catch { } return new Verdict("OK", "Windows Update repair undone"); }
            return new Verdict("Problem", "Partly undone - see the output");
        }
        public static Verdict WuPurge(Action<string> log, CancellationToken ct)
        {
            StopServices(log, ct); long freed = 0;
            foreach (var d in WuBackups())
            {
                long sz = 0; try { sz = d.EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => { try { return f.Length; } catch { return 0L; } }); } catch { }
                try { ForceDelete(d.FullName); freed += sz; log($"Deleted {d.Name} ({Fmt.Size(sz)})"); }
                catch (Exception e) { log($"Could not delete {d.Name}: {e.Message}"); }
            }
            foreach (var f in WuFolders)
                try { foreach (var d in new DirectoryInfo(Path.GetDirectoryName(f)).GetDirectories(Path.GetFileName(f) + ".rebuilt-*")) ForceDelete(d.FullName); } catch { }
            try { File.Delete(Journal); } catch { }
            StartServices(log);
            return new Verdict("OK", "Freed " + Fmt.Size(freed));
        }
        static void ForceDelete(string dir)
        {
            foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories)) try { f.Attributes = FileAttributes.Normal; } catch { }
            Directory.Delete(dir, true);
        }
    }
}
