using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

        public Task<List<LargeFile>> Run(IEnumerable<string> roots, long min, CancellationToken ct) => Task.Run(() =>
        {
            var found = new ConcurrentBag<LargeFile>();
            var starts = new List<DirectoryInfo>();
            foreach (var r in roots)
            {
                var root = new DirectoryInfo(r.EndsWith("\\") ? r : r + "\\");
                // files right in the root, then each top-level folder in parallel (much faster on SSDs)
                try { foreach (var f in root.EnumerateFiles()) Consider(f, min, found); } catch { }
                try { foreach (var d in root.EnumerateDirectories()) if (Walkable(d)) starts.Add(d); } catch { }
            }
            Parallel.ForEach(starts, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, d => Walk(d, min, found, ct));
            return found.OrderByDescending(f => f.Size).ToList();
        }, ct);

        static bool Walkable(DirectoryInfo d)
        {
            try
            {
                if ((d.Attributes & FileAttributes.ReparsePoint) != 0) return false;   // never follow links
                if (SkipNames.Contains(d.Name)) return false;
                if (d.FullName.TrimEnd('\\').Equals(Win, StringComparison.OrdinalIgnoreCase)) return false;
                return true;
            }
            catch { return false; }
        }

        void Walk(DirectoryInfo top, long min, ConcurrentBag<LargeFile> found, CancellationToken ct)
        {
            var stack = new Stack<DirectoryInfo>(); stack.Push(top);
            while (stack.Count > 0)
            {
                if (ct.IsCancellationRequested) return;
                var dir = stack.Pop(); Current = dir.FullName;
                try { foreach (var d in dir.EnumerateDirectories()) if (Walkable(d)) stack.Push(d); } catch { }
                try { foreach (var f in dir.EnumerateFiles()) Consider(f, min, found); } catch { }
            }
        }

        void Consider(FileInfo f, long min, ConcurrentBag<LargeFile> found)
        {
            Interlocked.Increment(ref Checked);
            try
            {
                if (f.Length < min || SkipNames.Contains(f.Name) || (f.Attributes & FileAttributes.ReparsePoint) != 0) return;
                string kind = KindOf(f.FullName);
                found.Add(new LargeFile { Path = f.FullName, Name = f.Name, Folder = f.DirectoryName, Size = f.Length, Modified = f.LastWriteTime, Kind = kind, IsApp = kind == "Game files" || kind == "App or system file" });
            }
            catch { }
        }

        /// <summary>"Video 12.3 GB · Disk image 4.1 GB · ..." biggest first.</summary>
        public static string Breakdown(IEnumerable<LargeFile> files) =>
            string.Join("  \u00B7  ", files.GroupBy(f => f.Kind).Select(g => (g.Key, Sum: g.Sum(x => x.Size))).OrderByDescending(x => x.Sum).Select(x => $"{x.Key} {Fmt.Size(x.Sum)}"));
    }
}
