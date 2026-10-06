using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace CleanSweep.Engine
{
    public enum Status { Problem = 0, Warning = 1, Info = 2, OK = 3 }

    /// <summary>One line in Recommendations. Actions only navigate or open Windows' own tools - checks never change anything.</summary>
    public class Finding
    {
        public string Area, Text, Advice = "", Action = "", ActionText = "";
        public Status Status;
        // Used when Action points at a CleanSweep page that isn't in this edition yet
        public string FallbackAction = "", FallbackText = "";
        public Finding(string area, Status status, string text, string advice = "", string action = "", string actionText = "")
        { Area = area; Status = status; Text = text; Advice = advice; Action = action; ActionText = actionText; }
        public Finding Fallback(string action, string text) { FallbackAction = action; FallbackText = text; return this; }
    }

    /// <summary>
    /// The health checks behind the Dashboard score (same checks and thresholds as the PowerShell edition).
    /// Report-first: every check only reads the system. Each check is independent - if one fails it reports
    /// "Could not check" and the rest still run.
    /// </summary>
    public static class Health
    {
        public static readonly string HistoryFile = Path.Combine(AppPaths.Dir, "history.csv");

        public static readonly (string Name, Func<IEnumerable<Finding>> Run)[] Checks =
        {
            ("Storage", Storage), ("Drive health", DriveHealth), ("Memory", Memory), ("Startup apps", StartupApps),
            ("Security", Security), ("Windows Update", WindowsUpdate), ("Uptime", Uptime), ("Junk files", JunkFiles),
            ("Battery", Battery), ("Devices", Devices), ("Stability", Stability), ("Internet", Internet), ("Automatic cleanup", AutoCleanCheck),
        };

        // Info only - it never lowers the score
        static IEnumerable<Finding> AutoCleanCheck()
        {
            var st = AutoClean.State();
            if (st.Exists && st.Enabled) yield return new Finding("Automatic cleanup", Status.OK, "On - " + AutoCleanConfig.Load().Describe());
            else yield return new Finding("Automatic cleanup", Status.Info, "Off - junk builds up until you clean it",
                "Schedule a weekly cleanup of temp files and update leftovers. It runs in the background and skips anything recent or in use.", "schedule", "Set up schedule");
        }

        public static List<Finding> RunCheck(string name, Func<IEnumerable<Finding>> check)
        {
            try { return check().Where(f => f != null).ToList(); }
            catch (Exception e) { return new List<Finding> { new Finding(name, Status.Info, "Could not check (" + e.Message + ")") }; }
        }

        /// <summary>100 minus 15 per problem and 5 per warning (never below 0).</summary>
        public static int Score(IEnumerable<Finding> rows) =>
            Math.Max(0, 100 - 15 * rows.Count(r => r.Status == Status.Problem) - 5 * rows.Count(r => r.Status == Status.Warning));
        public static string Grade(int s) => s >= 90 ? "Excellent" : s >= 75 ? "Good" : s >= 50 ? "Fair" : "Needs attention";

        static string Sys => AppPaths.SystemDrive;

        // ---------------------------------------------------------------- checks
        static IEnumerable<Finding> Storage()
        {
            foreach (var d in DriveInfo.GetDrives())
            {
                long size, free;
                try { if (d.DriveType != DriveType.Fixed || !d.IsReady || d.TotalSize <= 0) continue; size = d.TotalSize; free = d.TotalFreeSpace; } catch { continue; }
                string id = d.Name.TrimEnd('\\'); int pct = (int)Math.Round(free * 100.0 / size); string f = Fmt.Size(free);
                bool sys = id.Equals(Sys, StringComparison.OrdinalIgnoreCase);
                if ((sys && free < 10L << 30) || pct < 10)
                    yield return new Finding("Storage", Status.Problem, $"{id} is almost full: {f} free ({pct}%)",
                        "Windows slows down and updates can fail when the system drive is nearly full. Clean junk files, then use Large files to spot big files you don't need (or move them to another drive).",
                        "page:Large files", "Find large files").Fallback("settings:ms-settings:storagesense", "Open Storage settings");
                else if (pct < 20)
                    yield return new Finding("Storage", Status.Warning, $"{id} is getting full: {f} free ({pct}%)",
                        "Keeping at least 15-20% free helps performance and leaves room for updates. Clean junk files first; Large files shows what else takes up space.", "page:Cleanup", "Clean junk files");
                else yield return new Finding("Storage", Status.OK, $"{id} {f} free ({pct}%)");
            }
        }

        static IEnumerable<Finding> DriveHealth()
        {
            var disks = Wmi.Query("SELECT * FROM MSFT_PhysicalDisk", @"root\Microsoft\Windows\Storage");
            if (disks.Count == 0) { yield return new Finding("Drive health", Status.Info, "No drive health data available"); yield break; }
            foreach (var pd in disks)
            {
                long media = Wmi.Long(pd, "MediaType"); string mt = media == 4 ? "SSD, " : media == 3 ? "HDD, " : "";
                string name = $"{Wmi.Str(pd, "FriendlyName")} ({mt}{Math.Round(Wmi.Long(pd, "Size") / (double)(1L << 30))} GB)";
                ManagementBaseObject rc = null;
                try { foreach (ManagementBaseObject r in pd.GetRelated("MSFT_StorageReliabilityCounter")) { rc = r; break; } } catch { }
                long? wear = Wmi.NLong(rc, "Wear"), temp = Wmi.NLong(rc, "Temperature"), hours = Wmi.NLong(rc, "PowerOnHours"), uncorrected = Wmi.NLong(rc, "ReadErrorsUncorrected");
                var bits = new List<string>();
                if (wear != null && media == 4) bits.Add(wear + "% worn");
                if (temp > 0) bits.Add(temp + " C");
                if (hours > 0) bits.Add(hours.Value.ToString("N0") + " hours on");
                string info = bits.Count > 0 ? " - " + string.Join(", ", bits) : "";
                long health = Wmi.Long(pd, "HealthStatus", 0);
                string backup = "settings:ms-settings:backup", backupText = "Open backup settings";
                if (health != 0)
                    yield return new Finding("Drive health", Status.Problem, $"{name} reports {(health == 1 ? "Warning" : health == 2 ? "Unhealthy" : "Unknown")}{info}",
                        "The drive itself is reporting a problem. Back up your important files now, then check the maker's support tool or plan a replacement.", backup, backupText);
                else if (uncorrected > 0)
                    yield return new Finding("Drive health", Status.Problem, $"{name} has {uncorrected} unreadable-data errors{info}", "Uncorrected read errors often come before a drive fails. Back up your files now.", backup, backupText);
                else if (wear >= 90)
                    yield return new Finding("Drive health", Status.Problem, $"{name} is near the end of its rated life{info}", "The SSD has used most of its rated write endurance. Back up and plan a replacement.", backup, backupText);
                else if (wear >= 70)
                    yield return new Finding("Drive health", Status.Warning, $"{name} is wearing out{info}", "The SSD has used over 70% of its rated life. Make sure backups are up to date.", backup, backupText);
                else if (temp > 70)
                    yield return new Finding("Drive health", Status.Warning, $"{name} is running hot{info}", "Above 70 C the drive slows itself down. Keep the laptop's air vents clear and avoid soft surfaces under it.");
                else yield return new Finding("Drive health", Status.OK, $"{name} is healthy{info}");
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MEMORYSTATUSEX
        {
            public uint dwLength, dwMemoryLoad; public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
        }
        [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);
        public static MEMORYSTATUSEX MemoryStatus() { var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX)) }; GlobalMemoryStatusEx(ref m); return m; }

        static IEnumerable<Finding> Memory()
        {
            var m = MemoryStatus(); double total = m.ullTotalPhys, free = m.ullAvailPhys;
            int pct = (int)Math.Round((total - free) / total * 100);
            if (pct >= 90) yield return new Finding("Memory", Status.Warning, $"Memory is {pct}% in use right now ({Fmt.Size((long)total)} installed)",
                "Close apps or browser tabs you aren't using. Task Manager shows what is using the most memory. (CleanSweep does not force-clear RAM - that only makes Windows slower.)", "run:taskmgr.exe", "Open Task Manager");
            else if (total < 7.5 * (1L << 30)) yield return new Finding("Memory", Status.Info, $"{Fmt.Size((long)total)} installed, {pct}% in use", "8 GB or more makes multitasking much smoother on Windows 11, if your laptop can be upgraded.");
            else yield return new Finding("Memory", Status.OK, $"{pct}% in use of {Fmt.Size((long)total)}");
        }

        /// <summary>Apps that start with Windows and are not turned off in Settings > Startup apps.</summary>
        public static int CountStartupApps()
        {
            var approved = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            const string sa = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";
            foreach (var (hive, key) in new[] { (Registry.CurrentUser, sa + "Run"), (Registry.LocalMachine, sa + "Run"), (Registry.LocalMachine, sa + "Run32"), (Registry.CurrentUser, sa + "StartupFolder") })
                using (var k = hive.OpenSubKey(key))
                    if (k != null) foreach (var v in k.GetValueNames()) if (k.GetValue(v) is byte[] b && b.Length > 0) approved[v] = (b[0] & 1) == 0;
            int n = 0;
            foreach (var (hive, key) in new[] { (Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Run"), (Registry.LocalMachine, @"Software\Microsoft\Windows\CurrentVersion\Run"), (Registry.LocalMachine, @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run") })
                using (var k = hive.OpenSubKey(key))
                    if (k != null) foreach (var v in k.GetValueNames()) if (v.Length > 0 && !(approved.TryGetValue(v, out var on) && !on)) n++;
            foreach (var dir in new[] { Environment.GetFolderPath(Environment.SpecialFolder.Startup), Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup) })
            {
                try { foreach (var f in new DirectoryInfo(dir).GetFiles()) if (!f.Name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase) && !(approved.TryGetValue(f.Name, out var on) && !on)) n++; } catch { }
            }
            return n;
        }
        static IEnumerable<Finding> StartupApps()
        {
            int n = CountStartupApps();
            if (n > 15) yield return new Finding("Startup apps", Status.Warning, $"{n} apps start with Windows",
                "Many startup apps slow down sign-in. Turn off the ones you don't need right away (you can still open them normally).", "settings:ms-settings:startupapps", "Open Startup apps");
            else yield return new Finding("Startup apps", Status.OK, $"{n} apps start with Windows");
        }

        static IEnumerable<Finding> Security()
        {
            const string ws = "settings:windowsdefender:", wsText = "Open Windows Security";
            var mp = Wmi.First("SELECT RealTimeProtectionEnabled, AntivirusSignatureAge FROM MSFT_MpComputerStatus", @"root\Microsoft\Windows\Defender");
            var third = Wmi.TryQuery("SELECT displayName FROM AntiVirusProduct", @"root\SecurityCenter2").Select(o => Wmi.Str(o, "displayName"))
                .Where(n => n.Length > 0 && n.IndexOf("Defender", StringComparison.OrdinalIgnoreCase) < 0).ToList();
            if (third.Count > 0) yield return new Finding("Security", Status.OK, "Antivirus: " + third[0]);
            else if (mp != null)
            {
                long age = Wmi.Long(mp, "AntivirusSignatureAge");
                if (!Wmi.Bool(mp, "RealTimeProtectionEnabled")) yield return new Finding("Security", Status.Problem, "Microsoft Defender real-time protection is off", "Turn real-time protection back on unless you use another antivirus.", ws, wsText);
                else if (age > 7) yield return new Finding("Security", Status.Warning, $"Virus definitions are {age} days old", "Run Windows Update or check for protection updates in Windows Security.", ws, wsText);
                else yield return new Finding("Security", Status.OK, "Microsoft Defender is on and up to date");
            }
            else yield return new Finding("Security", Status.Warning, "No antivirus status found", "Check Windows Security to make sure you are protected.", ws, wsText);

            // Firewall: GpoBoolean - 0 = off, 1 = on, 2 = not configured (on)
            var off = Wmi.TryQuery("SELECT Name, Enabled FROM MSFT_NetFirewallProfile", @"root\StandardCimv2").Where(p => Wmi.Long(p, "Enabled", 1) == 0).Select(p => Wmi.Str(p, "Name")).ToList();
            if (off.Count > 0) yield return new Finding("Security", Status.Problem, "Firewall is off for: " + string.Join(", ", off), "Turn the firewall back on unless another security app manages it.", ws, wsText);
            else yield return new Finding("Security", Status.OK, "Firewall is on");

            using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System"))
                if (k?.GetValue("EnableLUA") is int lua && lua == 0)
                    yield return new Finding("Security", Status.Problem, "User Account Control is turned off",
                        "UAC stops apps from making system changes without asking. Turn it back on in Control Panel > User Accounts.", "run:UserAccountControlSettings.exe", "Open UAC settings");
        }

        static DateTime? LastUpdateInstall()
        {
            try
            {
                var t = Type.GetTypeFromProgID("Microsoft.Update.AutoUpdate"); if (t == null) return null;
                dynamic au = Activator.CreateInstance(t);
                object d = au.Results.LastInstallationSuccessDate;
                return d is DateTime dt && dt.Year > 2000 ? dt : (DateTime?)null;
            }
            catch { return null; }
        }
        static IEnumerable<Finding> WindowsUpdate()
        {
            const string wu = "settings:ms-settings:windowsupdate", wuText = "Open Windows Update";
            var last = LastUpdateInstall();
            if (last != null)
            {
                int days = (int)(DateTime.Now - last.Value.ToLocalTime()).TotalDays;
                if (days > 45) yield return new Finding("Windows Update", Status.Warning, $"Updates last installed {Fmt.Count(days, "day")} ago",
                    "Security fixes come out monthly. Check Windows Update; if updates keep failing, use Repair Windows Update on the Repair tab.", wu, wuText);
                else yield return new Finding("Windows Update", Status.OK, days == 0 ? "Updates installed today" : $"Updates installed {Fmt.Count(days, "day")} ago");
            }
            else yield return new Finding("Windows Update", Status.Info, "Last update date not available", "", wu, wuText);
            bool pending = KeyExists(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending") || KeyExists(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired");
            if (pending) yield return new Finding("Windows Update", Status.Warning, "A restart is needed to finish installing updates", "Restart when convenient so updates can complete.");
        }
        static bool KeyExists(string path) { using (var k = Registry.LocalMachine.OpenSubKey(path)) return k != null; }

        public static DateTime? BootTime() => Wmi.Date(Wmi.First("SELECT LastBootUpTime FROM Win32_OperatingSystem"), "LastBootUpTime");
        static IEnumerable<Finding> Uptime()
        {
            var boot = BootTime() ?? throw new Exception("boot time not available");
            int d = (int)(DateTime.Now - boot).TotalDays;
            if (d >= 14) yield return new Finding("Uptime", Status.Warning, $"Not restarted for {Fmt.Count(d, "day")}", "A restart clears memory leaks and finishes pending updates. (Shut down with Fast Startup doesn't count as a restart.)");
            else yield return new Finding("Uptime", Status.OK, d == 0 ? "Restarted today" : $"Restarted {Fmt.Count(d, "day")} ago");
        }

        static IEnumerable<Finding> JunkFiles()
        {
            string w = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            long sz = CleanEngine.SizeOf(new[] { CleanEngine.MyTemp, w + @"\Temp", w + @"\SoftwareDistribution\Download" });
            if (sz > 2L << 30) yield return new Finding("Junk files", Status.Warning, $"About {Fmt.Size(sz)} of temporary files", "These are safe to remove on the Cleanup page.", "page:Cleanup", "Open Cleanup");
            else yield return new Finding("Junk files", Status.OK, $"About {Fmt.Size(sz)} of temporary files");
        }

        public static bool HasBattery() => Wmi.TryQuery("SELECT DeviceID FROM Win32_Battery").Count > 0;
        static IEnumerable<Finding> Battery()
        {
            if (!HasBattery()) yield break;   // desktops: no finding
            long full = Wmi.Long(Wmi.First("SELECT FullChargedCapacity FROM BatteryFullChargedCapacity", @"root\wmi"), "FullChargedCapacity");
            long design = Wmi.Long(Wmi.First("SELECT DesignedCapacity FROM BatteryStaticData", @"root\wmi"), "DesignedCapacity");
            long cycles = Wmi.Long(Wmi.First("SELECT CycleCount FROM BatteryCycleCount", @"root\wmi"), "CycleCount");
            if (full > 0 && design > 0)
            {
                int h = (int)Math.Round(full * 100.0 / design); string c = cycles > 0 ? $", {cycles} charge cycles" : "";
                if (h < 60) yield return new Finding("Battery", Status.Warning, $"Battery holds {h}% of its original charge{c}", "The battery is well worn. A replacement will restore battery life; the detailed report shows the trend.", "battery", "Open battery report");
                else yield return new Finding("Battery", Status.OK, $"Battery holds {h}% of its original charge{c}", "", "battery", "Open battery report");
            }
            else yield return new Finding("Battery", Status.Info, "Battery capacity not reported", "", "battery", "Open battery report");
        }

        static IEnumerable<Finding> Devices()
        {
            var bad = Wmi.Query("SELECT Name, Description, PNPDeviceID, ConfigManagerErrorCode FROM Win32_PnPEntity WHERE ConfigManagerErrorCode <> 0")
                .Where(d => { long c = Wmi.Long(d, "ConfigManagerErrorCode"); return c != 22 && c != 45; }).ToList();
            if (bad.Count > 0)
            {
                var names = bad.Take(3).Select(d => Wmi.Str(d, "Name") is string n && n.Length > 0 ? n : Wmi.Str(d, "Description") is string s && s.Length > 0 ? s : Wmi.Str(d, "PNPDeviceID"));
                yield return new Finding("Devices", Status.Warning, $"{Fmt.Count(bad.Count, "device")} not working: {string.Join("; ", names)}",
                    "Usually a missing or broken driver. Device Manager shows the error; Windows Update > Optional updates may have the driver.", "run:devmgmt.msc", "Open Device Manager");
            }
            else yield return new Finding("Devices", Status.OK, "All devices are working");
        }

        static List<EventRecord> Events(string log, string provider, int id, int days)
        {
            var list = new List<EventRecord>();
            string q = $"*[System[Provider[@Name='{provider}'] and EventID={id} and TimeCreated[timediff(@SystemTime) <= {(long)days * 86400000}]]]";
            try { using (var r = new EventLogReader(new EventLogQuery(log, PathType.LogName, q))) for (var e = r.ReadEvent(); e != null; e = r.ReadEvent()) list.Add(e); } catch { }
            return list;
        }
        static IEnumerable<Finding> Stability()
        {
            int bsod = Events("System", "Microsoft-Windows-WER-SystemErrorReporting", 1001, 30).Count;
            int power = Events("System", "Microsoft-Windows-Kernel-Power", 41, 30).Count;
            var apps = Events("Application", "Application Error", 1000, 7);
            const string rel = "run:perfmon.exe /rel", relText = "Open Reliability Monitor";
            if (bsod > 0) yield return new Finding("Stability", Status.Problem, $"{Fmt.Count(bsod, "blue-screen crash", "blue-screen crashes")} in the last 30 days",
                "Repeated blue screens usually point to a driver or hardware problem. Note the stop code in Reliability Monitor and update the related driver. If Windows files are damaged, the Recommended repair on the Repair tab can fix them.",
                "page:Repair", "Open Repair tools").Fallback(rel, relText);
            else if (power > 0) yield return new Finding("Stability", Status.Warning, $"{Fmt.Count(power, "unexpected shutdown")} in the last 30 days", "The PC lost power or froze. If you didn't hold the power button, check Reliability Monitor.", rel, relText);
            else yield return new Finding("Stability", Status.OK, "No blue screens or unexpected shutdowns in 30 days");
            if (apps.Count >= 5)
            {
                var top = apps.Select(e => { try { return e.Properties.Count > 0 ? e.Properties[0].Value?.ToString() : null; } catch { return null; } })
                    .Where(n => !string.IsNullOrEmpty(n)).GroupBy(n => n).OrderByDescending(g => g.Count()).Take(2).Select(g => $"{g.Key} x{g.Count()}");
                yield return new Finding("Stability", Status.Warning, $"{apps.Count} app crashes this week (mostly {string.Join(", ", top)})", "Update or reinstall the app that keeps crashing.", rel, relText);
            }
        }

        /// <summary>Round-trip time in ms by ping, or by a TCP connect to port 443 if ping is blocked. Null if unreachable.</summary>
        public static int? PingMs(string host, bool tcp = false)
        {
            if (tcp)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                try { using (var c = new TcpClient()) { var t = c.ConnectAsync(host, 443); if (t.Wait(1500) && c.Connected) return (int)sw.ElapsedMilliseconds; } } catch { }
                return null;
            }
            try { using (var p = new Ping()) { var r = p.Send(host, 1000); if (r.Status == IPStatus.Success) return (int)r.RoundtripTime; } } catch { }
            return null;
        }
        static IEnumerable<Finding> Internet()
        {
            int? ms = PingMs("1.1.1.1") ?? PingMs("1.1.1.1", true);
            if (ms == null) yield return new Finding("Internet", Status.Problem, "No internet connection", "Check Wi-Fi or the cable, then run a connection test on the Network page.", "page:Network", "Open Network").Fallback("settings:ms-settings:network-status", "Open Network settings");
            else if (ms > 100) yield return new Finding("Internet", Status.Warning, $"Slow response: {ms} ms", "Run a connection test on the Network page.", "page:Network", "Open Network").Fallback("settings:ms-settings:network-status", "Open Network settings");
            else yield return new Finding("Internet", Status.OK, $"Connected ({ms} ms)");
        }

        // ---------------------------------------------------------------- history (history.csv, shared with the PowerShell edition)
        public class HistoryEntry { public DateTime Date; public int Score, Problems, Warnings; public double SystemFreeGB; }

        public static void SaveHistory(int score, IList<Finding> rows)
        {
            try
            {
                Directory.CreateDirectory(AppPaths.Dir);
                double freeGb = 0; try { freeGb = Math.Round(new DriveInfo(Sys).AvailableFreeSpace / (double)(1L << 30), 1); } catch { }
                bool header = !File.Exists(HistoryFile) || new FileInfo(HistoryFile).Length == 0;
                var sb = new StringBuilder();
                if (header) sb.AppendLine("\"Date\",\"Score\",\"Problems\",\"Warnings\",\"SystemFreeGB\"");
                sb.AppendLine(string.Join(",", new[] { DateTime.Now.ToString("s"), score.ToString(), rows.Count(r => r.Status == Status.Problem).ToString(),
                    rows.Count(r => r.Status == Status.Warning).ToString(), freeGb.ToString(CultureInfo.InvariantCulture) }.Select(v => "\"" + v + "\"")));
                File.AppendAllText(HistoryFile, sb.ToString(), new UTF8Encoding(false));
            }
            catch (Exception e) { Trace.Write("History: " + e.Message); }
        }

        public static List<HistoryEntry> LoadHistory(string file = null)
        {
            var list = new List<HistoryEntry>(); file = file ?? HistoryFile;
            try
            {
                if (!File.Exists(file)) return list;
                var lines = File.ReadAllLines(file); if (lines.Length < 2) return list;
                var cols = lines[0].Split(',').Select(c => c.Trim('"', ' ', '\uFEFF')).ToList();
                int iD = cols.IndexOf("Date"), iS = cols.IndexOf("Score"), iP = cols.IndexOf("Problems"), iW = cols.IndexOf("Warnings"), iF = cols.IndexOf("SystemFreeGB");
                foreach (var line in lines.Skip(1))
                {
                    var v = line.Split(',').Select(c => c.Trim('"', ' ')).ToArray();
                    if (iD < 0 || iS < 0 || v.Length <= Math.Max(iD, iS)) continue;
                    if (!DateTime.TryParse(v[iD], CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) || !int.TryParse(v[iS], out var s)) continue;
                    var e = new HistoryEntry { Date = d, Score = s };
                    if (iP >= 0 && iP < v.Length) int.TryParse(v[iP], out e.Problems);
                    if (iW >= 0 && iW < v.Length) int.TryParse(v[iW], out e.Warnings);
                    if (iF >= 0 && iF < v.Length) double.TryParse(v[iF], NumberStyles.Float, CultureInfo.InvariantCulture, out e.SystemFreeGB);
                    list.Add(e);
                }
            }
            catch { }
            return list;
        }

        // ---------------------------------------------------------------- report
        public static string Report(IList<Finding> rows, string computer)
        {
            int score = Score(rows);
            var sb = new StringBuilder();
            sb.Append("CleanSweep health report - ").AppendLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm"));
            sb.Append("Computer: ").AppendLine(computer);
            sb.AppendLine($"Health score: {score} ({Grade(score)})").AppendLine();
            foreach (var r in rows.OrderBy(r => r.Status))
            {
                sb.AppendLine($"[{r.Status}] {r.Area}: {r.Text}");
                if (r.Advice.Length > 0 && r.Status != Status.OK) sb.AppendLine("    What to do: " + r.Advice);
            }
            return sb.ToString();
        }
    }
}
