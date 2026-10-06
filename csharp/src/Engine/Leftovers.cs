using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32;

namespace CleanSweep.Engine
{
    /// <summary>Same rules as the main app: only real local paths that definitely no longer exist count as missing.</summary>
    public static class Missing
    {
        /// <summary>The program part of a command line ("C:\x\a.exe" /s  ->  C:\x\a.exe).</summary>
        public static string CmdPath(string cmd)
        {
            if (string.IsNullOrWhiteSpace(cmd)) return null;
            string c = Environment.ExpandEnvironmentVariables(cmd.Trim());
            if (c.StartsWith("\"")) { int e = c.IndexOf('"', 1); return e > 1 ? c.Substring(1, e - 1) : null; }
            var m = Regex.Match(c, @"^(.+?\.(exe|dll|com|bat|cmd|vbs|js|ps1|scr|cpl|msc|ico|lnk))(\s|,|$)", RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value : c;
        }
        public static bool IsMissing(string p)
        {
            if (string.IsNullOrWhiteSpace(p)) return false;
            if (!Regex.IsMatch(p, @"^[A-Za-z]:\\")) return false;                         // relative, network, shell: paths
            if (p.IndexOf(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase) >= 0) return false;   // Store apps are access-restricted
            try { if (!Directory.Exists(p.Substring(0, 3))) return false; } catch { return false; }  // unplugged drive
            try { return !File.Exists(p) && !Directory.Exists(p); } catch { return false; }
        }
    }

    // ==================================================================== broken shortcuts
    public class BrokenShortcut { public string Path, Name, Folder, Target; }

    public static class Shortcuts
    {
        public static List<string> DefaultDirs() => new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop), Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Internet Explorer\Quick Launch"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Windows\SendTo"),
        }.Where(d => !string.IsNullOrEmpty(d) && Directory.Exists(d)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        static dynamic shell;
        static dynamic Shell => shell ?? (shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")));
        public static string Target(string lnk)
        {
            try { return Environment.ExpandEnvironmentVariables((string)Shell.CreateShortcut(lnk).TargetPath ?? ""); } catch { return null; }
        }
        /// <summary>Creates a shortcut (used by the self-test).</summary>
        public static void Create(string lnk, string target) { var s = Shell.CreateShortcut(lnk); s.TargetPath = target; s.Save(); }

        public static List<BrokenShortcut> Scan(IEnumerable<string> dirs, Action<string> progress, CancellationToken ct)
        {
            var found = new List<BrokenShortcut>(); var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in dirs)
            {
                progress?.Invoke(d);
                foreach (var f in Walk(d, ct))
                {
                    ct.ThrowIfCancellationRequested();
                    if (!seen.Add(f)) continue;
                    string t = Target(f);
                    if (t != null && Missing.IsMissing(t))
                        found.Add(new BrokenShortcut { Path = f, Name = System.IO.Path.GetFileNameWithoutExtension(f), Folder = System.IO.Path.GetDirectoryName(f), Target = t });
                }
            }
            return found;
        }
        static IEnumerable<string> Walk(string dir, CancellationToken ct)
        {
            var stack = new Stack<string>(); stack.Push(dir);
            while (stack.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                var d = stack.Pop(); string[] files = new string[0], subs = new string[0];
                try { files = Directory.GetFiles(d, "*.lnk"); } catch { }
                try { subs = Directory.GetDirectories(d).Where(s => (new DirectoryInfo(s).Attributes & FileAttributes.ReparsePoint) == 0).ToArray(); } catch { }
                foreach (var f in files) yield return f;
                foreach (var s in subs) stack.Push(s);
            }
        }
    }

    // ==================================================================== registry leftovers
    public class RegIssue
    {
        public string Issue, Key, Value, Target; public bool Recommended;
        public string Location => Value != null ? $"{Key}  [{Value}]" : Key;
    }

    /// <summary>
    /// Registry leftovers that point to programs which no longer exist. Conservative on purpose: nothing else in the registry is
    /// touched, entries with no visible effect are listed but never ticked, and every removal is backed up to a .reg file first.
    /// </summary>
    public static class RegistryScan
    {
        public const string HKLM = "HKEY_LOCAL_MACHINE", HKCU = "HKEY_CURRENT_USER";
        public static string BackupDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "CleanSweep Backups");

        static RegistryKey Open(string full, bool write = false)
        {
            int i = full.IndexOf('\\'); string hive = i < 0 ? full : full.Substring(0, i), sub = i < 0 ? "" : full.Substring(i + 1);
            var root = RegistryKey.OpenBaseKey(hive == HKLM ? RegistryHive.LocalMachine : RegistryHive.CurrentUser, RegistryView.Registry64);
            return sub.Length == 0 ? root : root.OpenSubKey(sub, write);
        }
        static string[] Both(string sub) => new[] { $@"{HKCU}\Software\{sub}", $@"{HKLM}\SOFTWARE\{sub}", $@"{HKLM}\SOFTWARE\WOW6432Node\{sub}" };

        public static List<RegIssue> Scan(CancellationToken ct)
        {
            var r = new List<RegIssue>();
            void Add(string issue, string key, string value, string target, bool rec = true)
            {
                r.Add(new RegIssue { Issue = issue, Key = key, Value = value, Target = target, Recommended = rec });
            }
            // Startup entries pointing to missing programs
            foreach (var k in Both(@"Microsoft\Windows\CurrentVersion\Run"))
                using (var key = Open(k)) if (key != null)
                    foreach (var n in key.GetValueNames()) { ct.ThrowIfCancellationRequested(); if (n.Length == 0) continue; var p = Missing.CmdPath(key.GetValue(n)?.ToString()); if (Missing.IsMissing(p)) Add("Startup entry", k, n, p); }
            // App Paths pointing to missing programs
            foreach (var k in Both(@"Microsoft\Windows\CurrentVersion\App Paths"))
                using (var key = Open(k)) if (key != null)
                    foreach (var s in key.GetSubKeyNames())
                    {
                        ct.ThrowIfCancellationRequested();
                        using (var sk = key.OpenSubKey(s)) { var p = Missing.CmdPath(sk?.GetValue("")?.ToString()); if (Missing.IsMissing(p)) Add("Application path", k + "\\" + s, null, p); }
                    }
            // Uninstall entries for programs that are gone
            foreach (var k in Both(@"Microsoft\Windows\CurrentVersion\Uninstall"))
                using (var key = Open(k)) if (key != null)
                    foreach (var s in key.GetSubKeyNames())
                    {
                        ct.ThrowIfCancellationRequested();
                        using (var sk = key.OpenSubKey(s))
                        {
                            if (sk == null || Convert.ToInt32(sk.GetValue("SystemComponent") ?? 0) == 1) continue;
                            var u = Missing.CmdPath(sk.GetValue("UninstallString")?.ToString()); var loc = sk.GetValue("InstallLocation")?.ToString();
                            if (Missing.IsMissing(u) && (string.IsNullOrWhiteSpace(loc) || Missing.IsMissing(loc.Trim('"').TrimEnd('\\'))))
                                Add("Leftover uninstall entry: " + (sk.GetValue("DisplayName")?.ToString() ?? s), k + "\\" + s, null, u);
                        }
                    }
            // Shared DLL references to missing files (no benefit to remove)
            foreach (var k in new[] { $@"{HKLM}\SOFTWARE\Microsoft\Windows\CurrentVersion\SharedDLLs", $@"{HKLM}\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\SharedDLLs" })
                using (var key = Open(k)) if (key != null)
                    foreach (var n in key.GetValueNames()) { ct.ThrowIfCancellationRequested(); if (Missing.IsMissing(n)) Add("Missing shared DLL (no benefit to remove)", k, n, n, false); }
            // Program-name cache for programs that no longer exist (no benefit to remove)
            string mui = $@"{HKCU}\Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\MuiCache";
            using (var key = Open(mui)) if (key != null)
                foreach (var n in key.GetValueNames())
                {
                    var m = Regex.Match(n, @"^(.+?\.(exe|dll|com|bat|cmd|msc|cpl))\.[A-Za-z]+$", RegexOptions.IgnoreCase);
                    if (m.Success && Missing.IsMissing(m.Groups[1].Value)) Add("Obsolete program name cache (no benefit to remove)", mui, n, m.Groups[1].Value, false);
                }
            return r;
        }

        /// <summary>Exports every affected key to one .reg file (reg.exe's own format, so it can be imported again).</summary>
        /// <summary>Saves a .reg file with only what will be removed: single values for startup entries, whole keys for the rest.</summary>
        public static string Backup(IEnumerable<RegIssue> items)
        {
            Directory.CreateDirectory(BackupDir);
            string outFile = Path.Combine(BackupDir, "Registry backup " + DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss") + ".reg");
            var sb = new StringBuilder("Windows Registry Editor Version 5.00\r\n"); int ok = 0;
            var list = items.ToList();
            foreach (var g in list.Where(i => i.Value != null).GroupBy(i => i.Key, StringComparer.OrdinalIgnoreCase))
                using (var k = Open(g.Key))
                {
                    if (k == null) continue;
                    sb.Append("\r\n[").Append(g.Key).Append("]\r\n");
                    foreach (var i in g) { var line = RegLine(k, i.Value); if (line != null) { sb.Append(line).Append("\r\n"); ok++; } }
                }
            foreach (var key in list.Where(i => i.Value == null).Select(i => i.Key).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                string tmp = Path.GetTempFileName();
                var (code, _) = Cmd.Run("reg.exe", $"export \"{key}\" \"{tmp}\" /y /reg:64");
                if (code == 0) { foreach (var l in File.ReadAllLines(tmp, Encoding.Unicode).Skip(1)) sb.Append(l).Append("\r\n"); ok++; }
                try { File.Delete(tmp); } catch { }
            }
            if (ok == 0) throw new Exception("the registry backup could not be written");
            File.WriteAllText(outFile, sb.ToString(), Encoding.Unicode);
            return outFile;
        }
        static string Esc(string v) => v.Replace("\\", "\\\\").Replace("\"", "\\\"");
        /// <summary>One value in .reg syntax (same format regedit writes).</summary>
        static string RegLine(RegistryKey k, string name)
        {
            object v = k.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames); if (v == null) return null;
            string n = name.Length == 0 ? "@" : "\"" + Esc(name) + "\"";
            switch (k.GetValueKind(name))
            {
                case RegistryValueKind.String: return n + "=\"" + Esc((string)v) + "\"";
                case RegistryValueKind.DWord: return n + "=dword:" + unchecked((uint)(int)v).ToString("x8");
                case RegistryValueKind.ExpandString: return n + "=hex(2):" + Hex(Encoding.Unicode.GetBytes((string)v + "\0"));
                case RegistryValueKind.MultiString: return n + "=hex(7):" + Hex(Encoding.Unicode.GetBytes(string.Join("\0", (string[])v) + "\0\0"));
                case RegistryValueKind.QWord: return n + "=hex(b):" + Hex(BitConverter.GetBytes((long)v));
                case RegistryValueKind.Binary: return n + "=hex:" + Hex((byte[])v);
                default: return null;
            }
        }
        static string Hex(byte[] b) => string.Join(",", b.Select(x => x.ToString("x2")));
        public static void Remove(RegIssue i)
        {
            if (i.Value != null) using (var k = Open(i.Key, true)) { if (k == null) return; k.DeleteValue(i.Value, false); }
            else
            {
                int c = i.Key.LastIndexOf('\\');
                using (var parent = Open(i.Key.Substring(0, c), true)) parent?.DeleteSubKeyTree(i.Key.Substring(c + 1), false);
            }
        }
        public static bool Exists(RegIssue i)
        {
            using (var k = Open(i.Key)) return k != null && (i.Value == null || k.GetValueNames().Contains(i.Value, StringComparer.OrdinalIgnoreCase));
        }
        public static bool Import(string regFile) => Cmd.Run("reg.exe", $"import \"{regFile}\" /reg:64").Code == 0;
        public static List<FileInfo> Backups() { try { return new DirectoryInfo(BackupDir).GetFiles("*.reg").OrderByDescending(f => f.LastWriteTime).ToList(); } catch { return new List<FileInfo>(); } }
    }
}
