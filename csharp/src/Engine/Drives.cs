using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management;
using System.Text.RegularExpressions;

namespace CleanSweep.Engine
{
    public class DriveRow
    {
        public string Letter;            // "C:" or null for hidden partitions
        public string Name, Media, FS, Health = "Healthy", Disk;
        public long Size; public long? Free;
        public int? Pct => Free != null && Size > 0 ? (int?)Math.Round(Free.Value * 100.0 / Size) : null;
        public bool IsSystemPart => Letter == null;
        public bool IsSsd => Media.StartsWith("SSD");
        public bool IsBoot;
    }

    /// <summary>
    /// Every volume, like Disk Management's list. Read-only. Hidden Windows partitions (EFI, recovery, reserved) are listed
    /// for reference but can't be selected. Partitions are never changed by CleanSweep.
    /// </summary>
    public static class Drives
    {
        const string NS = @"root\Microsoft\Windows\Storage";
        static readonly Dictionary<string, string> Gpt = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["{c12a7328-f81f-11d2-ba4b-00a0c93ec93b}"] = "EFI system partition", ["{e3c9e316-0b5c-4db8-817d-f92df00215ae}"] = "Microsoft reserved",
            ["{de94bba4-06d1-4d40-a16a-bfd50179d6ac}"] = "Recovery partition", ["{ebd0a0a2-b9e5-4433-87c0-68b6b72699c7}"] = "Partition",
        };
        static readonly Dictionary<int, string> Mbr = new Dictionary<int, string> { [0x27] = "Recovery partition", [0xEF] = "EFI system partition", [7] = "Partition", [12] = "Partition", [6] = "Partition" };

        static char Letter(ManagementBaseObject o)
        {
            var v = Wmi.Prop(o, "DriveLetter");
            if (v == null) return '\0';
            if (v is char c) return c;
            try { return (char)Convert.ToInt32(v); } catch { var s = v.ToString(); return s.Length > 0 ? s[0] : '\0'; }
        }

        public static List<DriveRow> List()
        {
            var rows = new List<DriveRow>();
            string sys = AppPaths.SystemDrive;
            var phys = Wmi.TryQuery("SELECT DeviceId, MediaType, BusType, FriendlyName FROM MSFT_PhysicalDisk", NS).ToDictionary(p => Wmi.Str(p, "DeviceId"), p => p);
            var disks = Wmi.TryQuery("SELECT Number, BusType FROM MSFT_Disk", NS).ToDictionary(d => Wmi.Long(d, "Number").ToString(), d => d);
            var vols = new Dictionary<string, ManagementObject>(StringComparer.OrdinalIgnoreCase);
            foreach (var v in Wmi.TryQuery("SELECT Path, DriveLetter, FileSystemLabel, FileSystem, HealthStatus, Size, SizeRemaining FROM MSFT_Volume", NS)) vols[Wmi.Str(v, "Path")] = v;

            foreach (var p in Wmi.TryQuery("SELECT DiskNumber, Offset, DriveLetter, Size, GptType, MbrType, AccessPaths FROM MSFT_Partition", NS)
                                 .OrderBy(p => Wmi.Long(p, "DiskNumber")).ThenBy(p => Wmi.Long(p, "Offset")))
            {
                string dn = Wmi.Long(p, "DiskNumber").ToString();
                ManagementObject v = null;
                if (Wmi.Prop(p, "AccessPaths") is string[] paths) foreach (var ap in paths) if (vols.TryGetValue(ap, out v)) break;
                phys.TryGetValue(dn, out var pd); disks.TryGetValue(dn, out var dk);
                long mt = pd != null ? Wmi.Long(pd, "MediaType") : 0, bus = pd != null ? Wmi.Long(pd, "BusType") : dk != null ? Wmi.Long(dk, "BusType") : 0;
                string media = mt == 4 ? "SSD" : mt == 3 ? "HDD" : "Disk";
                if (bus == 7 || (dk != null && Wmi.Long(dk, "BusType") == 7)) media = "USB drive";
                char lc = Letter(p); string letter = char.IsLetter(lc) ? char.ToUpperInvariant(lc) + ":" : null;
                string label = v != null && Wmi.Str(v, "FileSystemLabel").Length > 0 ? Wmi.Str(v, "FileSystemLabel") :
                               letter == sys ? "Windows" : letter != null ? "Local Disk" :
                               Gpt.TryGetValue(Wmi.Str(p, "GptType"), out var g) ? g : Mbr.TryGetValue((int)Wmi.Long(p, "MbrType"), out var m) ? m : "Partition";
                long vsize = v != null ? Wmi.Long(v, "Size") : 0;
                long hs = v != null ? Wmi.Long(v, "HealthStatus") : 0;
                rows.Add(new DriveRow
                {
                    Letter = letter, Name = letter != null ? $"{letter} {label}" : $"({label})", IsBoot = letter == sys,
                    Media = letter == sys ? media + " (boot)" : media, FS = v != null ? Wmi.Str(v, "FileSystem") : "",
                    Health = hs == 0 ? "Healthy" : hs == 1 ? "Warning" : hs == 2 ? "Unhealthy" : "Unknown",
                    Size = vsize > 0 ? vsize : Wmi.Long(p, "Size"), Free = vsize > 0 ? (long?)Wmi.Long(v, "SizeRemaining") : null, Disk = "Disk " + dn,
                });
            }
            // Drives without partition info (some USB card readers), or if the Storage WMI isn't available
            foreach (var ld in Wmi.TryQuery("SELECT DeviceID, VolumeName, FileSystem, Size, FreeSpace, DriveType FROM Win32_LogicalDisk WHERE DriveType=2 OR DriveType=3"))
            {
                string id = Wmi.Str(ld, "DeviceID"); long size = Wmi.Long(ld, "Size");
                if (size <= 0 || rows.Any(r => r.Letter == id)) continue;
                bool fixedDisk = Wmi.Long(ld, "DriveType") == 3; string vn = Wmi.Str(ld, "VolumeName");
                rows.Add(new DriveRow
                {
                    Letter = id, Name = $"{id} {(vn.Length > 0 ? vn : id == sys ? "Windows" : fixedDisk ? "Local Disk" : "Removable")}", IsBoot = id == sys,
                    Media = fixedDisk ? (id == sys ? "Disk (boot)" : "Disk") : "Removable", FS = Wmi.Str(ld, "FileSystem"), Size = size, Free = Wmi.Long(ld, "FreeSpace"), Disk = ""
                });
            }
            // Lettered drives first, then hidden partitions
            return rows.OrderBy(r => r.IsSystemPart).ThenBy(r => r.Letter ?? "").ThenBy(r => r.Disk).ToList();
        }

        // ---------------------------------------------------------------- tool verdicts
        public static string CheckVerdict(string letter, ToolResult r)
        {
            if (r.Cancelled) return "cancelled";
            if (r.Code == 0 || Regex.IsMatch(r.Text, "found no problems", RegexOptions.IgnoreCase)) return "no problems found";
            if (Regex.IsMatch(r.Text, "not supported|cannot be scanned|not available|cannot open volume for direct access", RegexOptions.IgnoreCase)) return "not supported for this file system";
            return $"problems found - see the output. Windows can fix them: Settings > System > Storage, or run 'chkdsk {letter} /spotfix' ({AppPaths.SystemDrive} is fixed at the next restart)";
        }
        public static string OptimizeVerdict(DriveRow d, ToolResult r)
        {
            if (r.Cancelled) return "cancelled";
            if (r.Code == 0) return d.IsSsd ? "optimized (TRIM)" : "optimized";
            if (Regex.IsMatch(r.Text, "not supported|cannot be optimized|not eligible|optimization not available", RegexOptions.IgnoreCase)) return "can't be optimized (Windows doesn't support it for this drive)";
            return $"failed (code {r.Code}) - see the output";
        }
        /// <summary>When Windows last optimized each drive (from defrag's own history in the registry, if present).</summary>
        public static DateTime? LastOptimized(string letter)
        {
            try
            {
                foreach (var e in new System.Diagnostics.EventLog("Application").Entries.Cast<System.Diagnostics.EventLogEntry>().Reverse().Take(3000))
                    if (e.Source == "Microsoft-Windows-Defrag" && e.InstanceId == 258 && e.ReplacementStrings.Any(s => s.StartsWith(letter, StringComparison.OrdinalIgnoreCase) || s.Contains("(" + letter + ")")))
                        return e.TimeGenerated;
            }
            catch { }
            return null;
        }
    }
}
