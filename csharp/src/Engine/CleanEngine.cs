using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Text.RegularExpressions;
using System.Threading;

namespace CleanSweep.Engine
{
    public enum TargetKind { Paths, Drive, Recycle }

    /// <summary>One row on the Cleanup page: a junk category, a drive's leftover temp files, or old Recycle Bin items.</summary>
    public class CleanTarget
    {
        public string Name;
        public TargetKind Kind;
        public string[] Paths = new string[0];
        public string Drive;
        public bool DefaultOn;
        public string Note = "";
        public List<CleanItem> Found = new List<CleanItem>();
        public long Size;
    }

    /// <summary>A file (or Recycle Bin item) that can be removed.</summary>
    public class CleanItem
    {
        public string FullName;
        public string Name;
        public string DirectoryName;
        public long Length;
        public bool IsRecycle;
        public string InfoFile;   // Recycle Bin: the $I file describing the item
    }

    /// <summary>Why files were left alone during a scan.</summary>
    public class ScanStat
    {
        public int Recent, Ignored, Links, Unreadable;
        public string Describe()
        {
            var p = new List<string>();
            if (Recent > 0) p.Add(Recent + " recent");
            if (Ignored > 0) p.Add(Ignored + " ignored");
            if (Links > 0) p.Add(Links + " links");
            return string.Join(", ", p);
        }
    }

    /// <summary>
    /// The CleanSweep cleaning engine. One set of safety rules for every cleaning path:
    ///  - only known junk locations, walked by CleanSweep itself so junctions and symbolic links are never followed
    ///  - files changed more recently than the chosen age are skipped (they may still be in use)
    ///  - "Always ignore" rules are respected
    ///  - files that can't be deleted are reported, never forced
    /// </summary>
    public static class CleanEngine
    {
        static readonly string L = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        static readonly string W = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        static readonly string ProgramData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        public static readonly string MySid = WindowsIdentity.GetCurrent().User?.Value ?? "";

        /// <summary>The user's temp folder in its long form (TEMP is often an 8.3 path like C:\Users\RUNNER~1).</summary>
        public static string MyTemp
        {
            get
            {
                string t = Path.GetTempPath().TrimEnd('\\');
                try { return new DirectoryInfo(t).FullName.TrimEnd('\\'); } catch { return t; }
            }
        }

        // Ticked by default (safe to remove, real space); the rest are offered with a note about the trade-off.
        // Names are stable: saved schedules refer to them.
        public static readonly string[] DefaultOn =
        {
            "User Temp Files", "Windows Temp Files", "Other Users' Temp Files", "Internet Temporary Files",
            "Windows Update Cache", "Crash Dumps & Error Reports", "Delivery Optimization"
        };
        public static readonly Dictionary<string, string> Notes = new Dictionary<string, string>
        {
            ["Thumbnail Cache"] = "folders with pictures open slowly until rebuilt",
            ["Chrome Cache"] = "websites load a little slower at first",
            ["Edge Cache"] = "websites load a little slower at first",
            ["Firefox Cache"] = "websites load a little slower at first",
            ["DirectX Shader Cache"] = "games rebuild it and may stutter briefly",
        };

        /// <summary>Junk categories on the Windows drive. Prefetch is deliberately absent: Windows uses it to start apps faster.</summary>
        public static List<CleanTarget> StandardTargets()
        {
            var t = new List<(string, string[])>
            {
                ("User Temp Files", new[] { MyTemp }),
                ("Windows Temp Files", new[] { W + @"\Temp" }),
                ("Other Users' Temp Files", OtherUserTemps()),
                ("Internet Temporary Files", new[] { L + @"\Microsoft\Windows\INetCache" }),
                ("Windows Update Cache", new[] { W + @"\SoftwareDistribution\Download" }),
                ("Crash Dumps & Error Reports", new[] { L + @"\CrashDumps", L + @"\Microsoft\Windows\WER", ProgramData + @"\Microsoft\Windows\WER" }),
                ("Delivery Optimization", new[] { W + @"\ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Cache" }),
                ("Thumbnail Cache", new[] { L + @"\Microsoft\Windows\Explorer\thumbcache_*.db" }),
                ("Chrome Cache", new[] { L + @"\Google\Chrome\User Data\*\Cache", L + @"\Google\Chrome\User Data\*\Code Cache" }),
                ("Edge Cache", new[] { L + @"\Microsoft\Edge\User Data\*\Cache", L + @"\Microsoft\Edge\User Data\*\Code Cache" }),
                ("Firefox Cache", new[] { L + @"\Mozilla\Firefox\Profiles\*\cache2" }),
                ("DirectX Shader Cache", new[] { L + @"\D3DSCache" }),
            };
            return t.Where(x => x.Item2.Length > 0).Select(x => new CleanTarget
            {
                Name = x.Item1, Kind = TargetKind.Paths, Paths = x.Item2,
                DefaultOn = DefaultOn.Contains(x.Item1),
                Note = Notes.TryGetValue(x.Item1, out var n) ? n : ""
            }).ToList();
        }

        /// <summary>Everything shown on the Cleanup page: existing categories, other drives, old Recycle Bin items.</summary>
        public static List<CleanTarget> CleanupRows()
        {
            var rows = StandardTargets().Where(t => t.Paths.Any(p => Expand(p).Any())).ToList();
            foreach (var d in OtherDrives())
                rows.Add(new CleanTarget { Name = "Leftover temp files on " + d, Kind = TargetKind.Drive, Drive = d, Paths = new[] { d + "\\" }, Note = ".tmp, .chk and similar files" });
            rows.Add(new CleanTarget { Name = "Recycle Bin (old items)", Kind = TargetKind.Recycle, Note = "only items deleted more than the chosen days ago" });
            return rows;
        }

        static string[] OtherUserTemps()
        {
            string mine = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string root = Path.GetDirectoryName(mine);
            var skip = new[] { "Default", "Default User", "Public", "All Users" };
            try
            {
                return new DirectoryInfo(root).GetDirectories()
                    .Where(d => !d.FullName.Equals(mine, StringComparison.OrdinalIgnoreCase) && !skip.Contains(d.Name, StringComparer.OrdinalIgnoreCase)
                                && (d.Attributes & FileAttributes.ReparsePoint) == 0)
                    .Select(d => Path.Combine(d.FullName, @"AppData\Local\Temp"))
                    .Where(p => { try { return Directory.Exists(p) && !p.Equals(MyTemp, StringComparison.OrdinalIgnoreCase); } catch { return false; } })
                    .ToArray();
            }
            catch { return new string[0]; }
        }

        /// <summary>Fixed and removable drives other than the Windows drive (e.g. "D:").</summary>
        public static IEnumerable<string> OtherDrives() => AllDrives().Where(d => !d.Equals(AppPaths.SystemDrive, StringComparison.OrdinalIgnoreCase));
        public static IEnumerable<string> AllDrives()
        {
            foreach (var d in DriveInfo.GetDrives())
            {
                bool ok;
                try { ok = (d.DriveType == DriveType.Fixed || d.DriveType == DriveType.Removable) && d.IsReady && d.TotalSize > 0; } catch { ok = false; }
                if (ok) yield return d.Name.TrimEnd('\\');
            }
        }

        // ---------------------------------------------------------------- ignore rules ("Always ignore")
        // Rules: file:<path>, folder:<path>\ or ext:<folder>\*.<ext>  (same format as the PowerShell edition)
        public static List<string> IgnoreRules = new List<string>();

        public static bool IsIgnored(string p)
        {
            foreach (var r in IgnoreRules)
            {
                if (string.IsNullOrEmpty(r)) continue;
                if (r.StartsWith("file:")) { if (p.Equals(r.Substring(5), StringComparison.OrdinalIgnoreCase)) return true; }
                else if (r.StartsWith("folder:")) { if (p.StartsWith(r.Substring(7), StringComparison.OrdinalIgnoreCase)) return true; }
                else if (r.StartsWith("ext:"))
                {
                    string v = r.Substring(4);
                    if (string.Equals(Path.GetDirectoryName(p), Path.GetDirectoryName(v), StringComparison.OrdinalIgnoreCase)
                        && string.Equals(Path.GetExtension(p), Path.GetExtension(v), StringComparison.OrdinalIgnoreCase)) return true;
                }
            }
            return false;
        }

        // ---------------------------------------------------------------- finding files
        /// <summary>Resolves a path that may contain * or ? in any segment. Never descends through links while expanding.</summary>
        public static IEnumerable<FileSystemInfo> Expand(string pattern)
        {
            if (string.IsNullOrEmpty(pattern)) yield break;
            if (pattern.IndexOfAny(new[] { '*', '?' }) < 0)
            {
                if (Directory.Exists(pattern)) yield return new DirectoryInfo(pattern);
                else if (File.Exists(pattern)) yield return new FileInfo(pattern);
                yield break;
            }
            var parts = pattern.Split('\\');
            int i = 0; while (i < parts.Length && parts[i].IndexOfAny(new[] { '*', '?' }) < 0) i++;
            string bases = string.Join("\\", parts.Take(i)); if (bases.EndsWith(":")) bases += "\\";
            var current = new List<string> { bases };
            for (; i < parts.Length; i++)
            {
                bool last = i == parts.Length - 1; var next = new List<string>();
                foreach (var dir in current)
                {
                    if (!Directory.Exists(dir)) continue;
                    bool wild = parts[i].IndexOfAny(new[] { '*', '?' }) >= 0;
                    if (!wild) { next.Add(Path.Combine(dir, parts[i])); continue; }
                    FileSystemInfo[] found;
                    try { found = new DirectoryInfo(dir).GetFileSystemInfos(parts[i]); } catch { continue; }
                    foreach (var f in found)
                    {
                        bool isDir = (f.Attributes & FileAttributes.Directory) != 0;
                        if (isDir && (f.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                        if (isDir || last) next.Add(f.FullName);
                    }
                }
                current = next;
            }
            foreach (var p in current)
            {
                if (Directory.Exists(p)) yield return new DirectoryInfo(p);
                else if (File.Exists(p)) yield return new FileInfo(p);
            }
        }

        /// <summary>
        /// Files under the given paths older than <paramref name="cut"/>. Never follows junctions or symbolic links.
        /// <paramref name="nameFilter"/> limits to matching file names; <paramref name="skipRoot"/> skips matching folders at a drive root.
        /// </summary>
        public static IEnumerable<CleanItem> FindFiles(IEnumerable<string> paths, DateTime cut, ScanStat stat, CancellationToken ct = default,
                                                       Regex nameFilter = null, Regex skipRoot = null)
        {
            stat = stat ?? new ScanStat();
            foreach (var p in paths)
            {
                foreach (var start in Expand(p))
                {
                    if (start is FileInfo sf)
                    {
                        if (IsIgnored(sf.FullName)) stat.Ignored++;
                        else if (sf.LastWriteTime > cut) stat.Recent++;
                        else yield return Item(sf);
                        continue;
                    }
                    var stack = new Stack<string>(); stack.Push(start.FullName);
                    while (stack.Count > 0)
                    {
                        ct.ThrowIfCancellationRequested();
                        string dir = stack.Pop();
                        FileSystemInfo[] entries;
                        try { entries = new DirectoryInfo(dir).GetFileSystemInfos(); } catch { stat.Unreadable++; continue; }
                        foreach (var e in entries)
                        {
                            if ((e.Attributes & FileAttributes.Directory) != 0)
                            {
                                if ((e.Attributes & FileAttributes.ReparsePoint) != 0) { stat.Links++; continue; }
                                if (skipRoot != null && dir.Length <= 3 && skipRoot.IsMatch(e.Name)) continue;
                                stack.Push(e.FullName); continue;
                            }
                            if (nameFilter != null && !nameFilter.IsMatch(e.Name)) continue;
                            if (IsIgnored(e.FullName)) { stat.Ignored++; continue; }
                            if (e.LastWriteTime > cut) { stat.Recent++; continue; }
                            yield return Item((FileInfo)e);
                        }
                    }
                }
            }
        }

        static CleanItem Item(FileInfo f)
        {
            long len = 0; try { len = f.Length; } catch { }
            return new CleanItem { FullName = f.FullName, Name = f.Name, DirectoryName = f.DirectoryName, Length = len };
        }

        /// <summary>Total size of everything under the paths (no age limit) - for estimates such as the Dashboard health check.</summary>
        public static long SizeOf(IEnumerable<string> paths, CancellationToken ct = default) =>
            FindFiles(paths, DateTime.MaxValue, null, ct).Sum(f => f.Length);

        // Leftover temp files anywhere on a non-Windows drive (skips system and program folders)
        static readonly Regex DriveSkipDirs = new Regex(@"^(\$Recycle\.Bin|System Volume Information|Windows|Program Files|Program Files \(x86\)|ProgramData|Recovery|\$WinREAgent|\$SysReset|Config\.Msi|MSOCache)$", RegexOptions.IgnoreCase);
        static readonly Regex DriveJunkNames = new Regex(@"(\.tmp|\.temp|\._mp|\.chk|\.gid|\.old\.tmp)$|^(Thumbs\.db|ehthumbs\.db|~\$.+)$", RegexOptions.IgnoreCase);
        public static IEnumerable<CleanItem> DriveJunk(string drive, DateTime cut, ScanStat stat, CancellationToken ct = default) =>
            FindFiles(new[] { drive + "\\" }, cut, stat, ct, DriveJunkNames, DriveSkipDirs);

        /// <summary>Recycle Bin items deleted more than <paramref name="days"/> days ago (each item = a $I info file + a $R data file or folder).</summary>
        public static IEnumerable<CleanItem> OldRecycleItems(int days, IEnumerable<string> drives = null)
        {
            DateTime old = DateTime.Now.AddDays(-days);
            foreach (var d in drives ?? AllDrives())
            {
                string bin = d + @"\$Recycle.Bin\" + MySid;
                FileInfo[] infos;
                try { infos = new DirectoryInfo(bin).GetFiles("$I*"); } catch { continue; }
                foreach (var i in infos)
                {
                    if (i.LastWriteTime > old) continue;
                    string r = Path.Combine(bin, "$R" + i.Name.Substring(2)); long len = 0;
                    try
                    {
                        if (Directory.Exists(r)) len = FindFiles(new[] { r }, DateTime.MaxValue, null).Sum(x => x.Length);
                        else if (File.Exists(r)) len = new FileInfo(r).Length;
                    }
                    catch { }
                    yield return new CleanItem { FullName = r, Name = "Recycle Bin item (" + d + ")", DirectoryName = bin, Length = len, IsRecycle = true, InfoFile = i.FullName };
                }
            }
        }

        // ---------------------------------------------------------------- deleting
        /// <summary>Deletes one file. Read-only files and very long paths are handled; any other problem is returned, never forced.</summary>
        public static Exception TryDelete(string p)
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                try { File.Delete(p); return File.Exists(p) ? new IOException("The file is still there.") : null; }
                catch (UnauthorizedAccessException e) when (attempt == 0)
                {
                    try
                    {
                        var a = File.GetAttributes(p);
                        if ((a & FileAttributes.ReadOnly) != 0) { File.SetAttributes(p, a & ~FileAttributes.ReadOnly); continue; }
                    }
                    catch { }
                    return e;
                }
                catch (PathTooLongException) when (attempt == 0 && !p.StartsWith(@"\\?\")) { p = @"\\?\" + p; }
                catch (Exception e) { return e; }
            }
            return new IOException("Could not delete the file.");
        }

        static Exception TryDeleteRecycle(CleanItem item)
        {
            try
            {
                if (Directory.Exists(item.FullName)) Directory.Delete(item.FullName, true);
                else if (File.Exists(item.FullName)) { var e = TryDelete(item.FullName); if (e != null) return e; }
                File.Delete(item.InfoFile);
                return null;
            }
            catch (Exception e) { return e; }
        }

        /// <summary>Deletes a file or a Recycle Bin item. Returns null on success.</summary>
        public static Exception TryDelete(CleanItem item) => item.IsRecycle ? TryDeleteRecycle(item) : TryDelete(item.FullName);

        /// <summary>Removes folders left empty inside a junk folder (never the folder itself, never links).</summary>
        public static void RemoveEmptyDirs(string root, DateTime cut)
        {
            if (!Directory.Exists(root)) return;
            var dirs = new List<string>(); var stack = new Stack<string>(); stack.Push(root);
            while (stack.Count > 0)
            {
                string d = stack.Pop();
                try
                {
                    foreach (var s in new DirectoryInfo(d).GetDirectories())
                        if ((s.Attributes & FileAttributes.ReparsePoint) == 0) { dirs.Add(s.FullName); stack.Push(s.FullName); }
                }
                catch { }
            }
            foreach (var d in dirs.OrderByDescending(x => x.Length))
            {
                try { var di = new DirectoryInfo(d); if (di.LastWriteTime < cut && !di.EnumerateFileSystemInfos().Any()) di.Delete(); } catch { }
            }
        }

        /// <summary>A plain-language reason a file couldn't be deleted, plus which apps are using it.</summary>
        public static (string Reason, string Who) Describe(Exception e, string path)
        {
            int code = e.HResult & 0xFFFF;
            if (e is IOException && (code == 32 || code == 33))
            {
                string who = ""; try { who = FileLocks.Who(path); } catch { }
                return ("In use", who.Length > 0 ? who : "Unknown app");
            }
            if (e is UnauthorizedAccessException) return ("Access denied (protected by Windows or another account)", "");
            if (e is PathTooLongException) return ("Path too long", "");
            return (e.Message, "");
        }
    }
}
