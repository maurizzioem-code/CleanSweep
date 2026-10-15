using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace CleanSweep.Engine
{
    /// <summary>What the update file on GitHub says (update/csharp.json).</summary>
    public class UpdateInfo
    {
        public Version Version; public string Url = "", Sha256 = "", Notes = ""; public long Size;
        public string Label => Installer.VersionLabel(Version);
    }

    /// <summary>
    /// Automatic updates for the C# edition.
    ///   1. Once a day when CleanSweep opens (or on "Check now"), read a small JSON file from GitHub.
    ///   2. If it lists a newer build, show it - nothing is downloaded until the user clicks Install update.
    ///   3. Download the new CleanSweep.exe and check it three ways before running it:
    ///      the SHA-256 fingerprint in the JSON, the version inside the exe, and the product name.
    ///   4. Hand over to the new exe ("--install --quiet --keep-shortcuts --wait-pid"): it waits for this
    ///      window to close, replaces the installed copy, keeps the user's shortcut choices and opens again.
    /// Separate from the PowerShell edition's version.json, so the two editions never update each other by mistake.
    /// </summary>
    public static class Updater
    {
        public const string DefaultUrl = "https://raw.githubusercontent.com/maurizzioem-code/CleanSweep/main/update/csharp.json";
        public static string ManifestUrl => Settings.GetString("CsUpdateUrl", DefaultUrl);
        public static string DownloadDir = Path.Combine(Path.GetTempPath(), "CleanSweep update");
        public static Version Current = Installer.MyVersion;

        /// <summary>Shared with the PowerShell edition: "Check for updates automatically".</summary>
        public static bool AutoCheck
        {
            get { var v = Settings.Get("AutoCheck"); return v is bool b ? b : true; }
            set { Settings.Set("AutoCheck", value); Settings.Save(); }
        }
        public static DateTime? LastCheck
        {
            get { return DateTime.TryParse(Settings.GetString("CsLastUpdateCheck"), null, System.Globalization.DateTimeStyles.RoundtripKind, out var d) ? d : (DateTime?)null; }
            set { Settings.Set("CsLastUpdateCheck", value?.ToString("o")); Settings.Save(); }
        }
        public static bool Due => AutoCheck && (LastCheck == null || (DateTime.Now - LastCheck.Value).TotalHours >= 24);

        /// <summary>The newest update found by the last check (null = none or not checked).</summary>
        public static UpdateInfo Available { get; private set; }
        public static event Action Changed;

        static Updater()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        }

        static byte[] Get(string url, int timeoutMs, CancellationToken ct, Action<int> progress = null)
        {
            var uri = new Uri(url);
            if (uri.IsFile) return File.ReadAllBytes(uri.LocalPath);      // self-test
            if (uri.Scheme != Uri.UriSchemeHttps) throw new Exception("updates are only downloaded over HTTPS");
            var req = (HttpWebRequest)WebRequest.Create(uri.AbsoluteUri + (uri.Query.Length == 0 ? "?" : "&") + "t=" + DateTime.UtcNow.Ticks);
            req.Timeout = timeoutMs; req.ReadWriteTimeout = timeoutMs; req.UserAgent = "CleanSweep/" + Current;
            req.CachePolicy = new System.Net.Cache.RequestCachePolicy(System.Net.Cache.RequestCacheLevel.NoCacheNoStore);
            using (ct.Register(() => { try { req.Abort(); } catch { } }))
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var s = resp.GetResponseStream())
            using (var ms = new MemoryStream())
            {
                long total = resp.ContentLength; var buf = new byte[65536]; int n, last = -1;
                while ((n = s.Read(buf, 0, buf.Length)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    ms.Write(buf, 0, n);
                    if (ms.Length > 50 * 1024 * 1024) throw new Exception("download is unexpectedly large");
                    if (progress != null && total > 0) { int p = (int)(ms.Length * 100 / total); if (p != last) { last = p; progress(p); } }
                }
                return ms.ToArray();
            }
        }

        public static UpdateInfo Parse(string json)
        {
            var d = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json);
            var u = new UpdateInfo
            {
                Version = Version.Parse(d["version"].ToString()), Url = d["url"].ToString(), Sha256 = d["sha256"].ToString().ToUpperInvariant(),
                Notes = d.TryGetValue("notes", out var n) ? n?.ToString() ?? "" : "", Size = d.TryGetValue("size", out var z) ? Convert.ToInt64(z) : 0
            };
            if (u.Sha256.Length != 64) throw new Exception("the update file has no valid fingerprint");
            return u;
        }

        /// <summary>Reads the update file. Returns the update if it is newer than this copy, otherwise null.</summary>
        public static UpdateInfo Check(CancellationToken ct = default)
        {
            var info = Parse(System.Text.Encoding.UTF8.GetString(Get(ManifestUrl, 15000, ct)));
            LastCheck = DateTime.Now;
            Available = info.Version > Current && Settings.GetString("CsSkipVersion") != info.Version.ToString() ? info : null;
            Trace.Write($"Update check: newest {info.Version}, this {Current} -> {(Available != null ? "update available" : "up to date")}");
            Changed?.Invoke();
            return Available;
        }
        public static void Skip(UpdateInfo u) { Settings.Set("CsSkipVersion", u.Version.ToString()); Settings.Save(); Available = null; Changed?.Invoke(); }
        public static void Reset() { Available = null; Changed?.Invoke(); }

        public static string Sha256(string file)
        {
            using (var sha = SHA256.Create()) using (var f = File.OpenRead(file)) return BitConverter.ToString(sha.ComputeHash(f)).Replace("-", "");
        }

        /// <summary>Downloads and verifies the new exe. Throws with a plain-language message if any check fails (the file is deleted).</summary>
        public static string Download(UpdateInfo u, Action<int> progress, CancellationToken ct)
        {
            Directory.CreateDirectory(DownloadDir);
            string file = Path.Combine(DownloadDir, $"CleanSweep-{u.Version}.exe");
            var data = Get(u.Url, 120000, ct, progress);
            File.WriteAllBytes(file, data);
            string why = Verify(file, u);
            if (why != null) { try { File.Delete(file); } catch { } throw new Exception(why); }
            return file;
        }
        /// <summary>null = OK, otherwise the reason the file must not be run.</summary>
        public static string Verify(string file, UpdateInfo u)
        {
            if (!string.Equals(Sha256(file), u.Sha256, StringComparison.OrdinalIgnoreCase)) return "The download didn't match its published fingerprint (SHA-256), so it was not installed.";
            var fv = FileVersionInfo.GetVersionInfo(file);
            if (fv.ProductName != "CleanSweep") return "The download is not CleanSweep, so it was not installed.";
            var v = Installer.VersionOf(file);
            if (v != u.Version) return $"The download is build {v?.Revision} but build {u.Version.Revision} was expected, so it was not installed.";
            // once CleanSweep is signed, updates must be signed by the same publisher
            return Signing.UpdateProblem(Installer.Source, file);
        }

        /// <summary>Starts the verified exe to install itself once this process has closed. The caller then exits.</summary>
        public static Process HandOver(string newExe, bool startAfter, string extraArgs = "")
        {
            string args = $"--install --quiet --keep-shortcuts --wait-pid {Process.GetCurrentProcess().Id}" + (startAfter ? "" : " --no-start") + (extraArgs.Length > 0 ? " " + extraArgs : "");
            Trace.Write($"Update: handing over to {newExe} {args}");
            return Process.Start(new ProcessStartInfo(newExe, args) { UseShellExecute = false, WorkingDirectory = Path.GetTempPath() });
        }

        /// <summary>Removes downloaded updates (they're only needed until the install has run).</summary>
        public static void CleanDownloads()
        {
            try { foreach (var f in Directory.GetFiles(DownloadDir, "CleanSweep-*.exe")) if (!string.Equals(f, Installer.Source, StringComparison.OrdinalIgnoreCase)) try { File.Delete(f); } catch { } } catch { }
        }
    }
}
