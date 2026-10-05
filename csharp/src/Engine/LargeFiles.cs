using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace CleanSweep.Engine
{
    public class LargeFile
    {
        public string Path, Name, Folder, Kind;
        public long Size;
        public DateTime Modified;
        /// <summary>Belongs to an installed app or game, or to Windows - remove it by uninstalling, not by deleting.</summary>
        public bool IsApp;
    }

    /// <summary>
    /// Finds files of a minimum size on the chosen drives. Read-only: it only lists files. Links are never followed,
    /// Windows' own folder and system files are skipped. Runs on background threads.
    /// </summary>
    public class LargeFileSearch
    {
        public long Checked;                 // files looked at so far (for progress)
        public string Current = "";          // folder being searched
        public static readonly long[] Sizes = { 100L << 20, 250L << 20, 500L << 20, 1L << 30 };

        static readonly string Win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        static readonly HashSet<string> SkipNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "System Volume Information", "$Recycle.Bin", "$WinREAgent", "$SysReset", "$Windows.~BT", "$Windows.~WS", "Recovery",
              "pagefile.sys", "hiberfil.sys", "swapfile.sys", "DumpStack.log", "DumpStack.log.tmp" };

        static readonly Regex AppPath = new Regex(@"\\(Program Files( \(x86\))?|ProgramData|Windows|WindowsApps)\\|\\AppData\\|\\steamapps\\|\\Epic Games\\|\\XboxGames\\|\\Riot Games\\|\\EA Games\\|\\Ubisoft\\|\\GOG Galaxy\\Games\\|\\Battle\.net\\", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex GamePath = new Regex(@"\\steamapps\\|\\Epic Games\\|\\XboxGames\\|\\Riot Games\\|\\EA Games\\|\\Ubisoft\\|\\GOG Galaxy\\Games\\|\\Battle\.net\\", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly (string Kind, string Ext)[] Kinds =
        {
            ("Video", "mp4 mkv avi mov wmv m4v webm flv mpg mpeg ts m2ts"),
            ("Disk image", "iso img vhd vhdx vmdk vdi wim esd"),
            ("Archive", "zip rar 7z tar gz tgz bz2 xz cab"),
            ("Installer", "exe msi msix msixbundle appx appxbundle"),
            ("Picture", "jpg jpeg png gif bmp tif tiff heic raw cr2 nef arw psd"),
            ("Audio", "mp3 wav flac m4a aac ogg wma"),
            ("Document", "pdf docx doc pptx ppt xlsx xls"),
            ("Backup", "bak old tib vbk"),
        };

        public static string KindOf(string path)
        {
            if (GamePath.IsMatch(path)) return "Game files";
            if (AppPath.IsMatch(path)) return "App or system file";
            string ext = System.IO.Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
            foreach (var k in Kinds) if ((" " + k.Ext + " ").Contains(" " + ext + " ")) return k.Kind;
            return "Other";
        }

        // ---- fast directory reading: FindFirstFileEx with large fetch (no FileInfo objects, no extra disk reads)
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct FindData
        {
            // FILETIMEs are two DWORDs (4-byte aligned) - a long here would shift every field after it
            public FileAttributes Attr; public uint CreatedLow, CreatedHigh, AccessedLow, AccessedHigh, WriteLow, WriteHigh, SizeHigh, SizeLow, Reserved0, Reserved1;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Name;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)] public string Alt;
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr FindFirstFileExW(string path, int infoLevel, out FindData data, int searchOp, IntPtr filter, int flags);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool FindNextFileW(IntPtr h, out FindData data);
        [DllImport("kernel32.dll")] static extern bool FindClose(IntPtr h);
        static readonly IntPtr Invalid = new IntPtr(-1);

        /// <summary>
        /// Searches with several threads sharing one queue of folders, so one huge folder (Users, Program Files)
        /// doesn't leave the other threads idle.
        /// </summary>
        public Task<List<LargeFile>> Run(IEnumerable<string> roots, long min, CancellationToken ct) => Task.Run(() =>
        {
            var found = new ConcurrentBag<LargeFile>(); var queue = new ConcurrentQueue<string>(); int pending = 0;
            foreach (var r in roots) { queue.Enqueue(r.TrimEnd('\\')); pending++; }
            int workers = Math.Max(4, Math.Min(16, Environment.ProcessorCount * 2));
            var threads = Enumerable.Range(0, workers).Select(_ => new Thread(() =>
            {
                var spin = new SpinWait();
                while (Volatile.Read(ref pending) > 0 && !ct.IsCancellationRequested)
                {
                    if (!queue.TryDequeue(out var dir)) { spin.SpinOnce(); continue; }
                    try { ReadDir(dir, min, found, queue, ref pending); } catch { }
                    Interlocked.Decrement(ref pending);
                }
            }) { IsBackground = true, Name = "CleanSweep large files" }).ToList();
            threads.ForEach(t => t.Start()); threads.ForEach(t => t.Join());
            ct.ThrowIfCancellationRequested();
            return found.OrderByDescending(f => f.Size).ToList();
        }, ct);

        void ReadDir(string dir, long min, ConcurrentBag<LargeFile> found, ConcurrentQueue<string> queue, ref int pending)
        {
            Current = dir;
            var h = FindFirstFileExW(@"\\?\" + dir + @"\*", 1 /* FindExInfoBasic */, out var d, 0, IntPtr.Zero, 2 /* LARGE_FETCH */);
            if (h == Invalid) return;
            try
            {
                do
                {
                    string n = d.Name; if (n == "." || n == "..") continue;
                    if ((d.Attr & FileAttributes.ReparsePoint) != 0) continue;   // never follow links (and skip cloud placeholders' link points)
                    if (SkipNames.Contains(n)) continue;
                    string full = dir + "\\" + n;
                    if ((d.Attr & FileAttributes.Directory) != 0)
                    {
                        if (full.Equals(Win, StringComparison.OrdinalIgnoreCase)) continue;
                        Interlocked.Increment(ref pending); queue.Enqueue(full);
                        continue;
                    }
                    Interlocked.Increment(ref Checked);
                    long size = ((long)d.SizeHigh << 32) | d.SizeLow;
                    if (size < min) continue;
                    string kind = KindOf(full);
                    found.Add(new LargeFile
                    {
                        Path = full, Name = n, Folder = dir, Size = size, Kind = kind, IsApp = kind == "Game files" || kind == "App or system file",
                        Modified = DateTime.FromFileTime(((long)d.WriteHigh << 32) | d.WriteLow)
                    });
                } while (FindNextFileW(h, out d));
            }
            finally { FindClose(h); }
        }

        /// <summary>"Video 12.3 GB · Disk image 4.1 GB · ..." biggest first.</summary>
        public static string Breakdown(IEnumerable<LargeFile> files) =>
            string.Join("  \u00B7  ", files.GroupBy(f => f.Kind).Select(g => (g.Key, Sum: g.Sum(x => x.Size))).OrderByDescending(x => x.Sum).Select(x => $"{x.Key} {Fmt.Size(x.Sum)}"));
    }
}
