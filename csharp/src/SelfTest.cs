using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using CleanSweep.Engine;
using CleanSweep.Pages;
using CleanSweep.UI;
using Microsoft.Win32;

namespace CleanSweep
{
    /// <summary>
    /// Built-in automated test (CleanSweep.exe --selftest &lt;folder&gt;), run on a Windows VM by GitHub Actions.
    /// Drives the real window, checks layout at the supported sizes, takes screenshots and exercises the cleaning
    /// engine's safety rules on files it creates itself. Writes log.txt; the exit code is the number of failed steps.
    /// </summary>
    static class SelfTest
    {
        static string outDir, logFile;
        static MainForm form;
        static int steps, failed;

        public static int Run(MainForm f, string dir)
        {
            outDir = Path.GetFullPath(dir); Directory.CreateDirectory(outDir);
            logFile = Path.Combine(outDir, "log.txt"); File.WriteAllText(logFile, "");
            form = f; form.StartPosition = FormStartPosition.Manual; form.Location = new Point(0, 0);
            form.Size = new Size(1024, 720); form.TopMost = true;
            form.Shown += async (s, e) =>
            {
                try { await RunAll(); }
                catch (Exception ex) { Note("TEST HARNESS ERROR: " + ex); failed++; }
                Note(""); Note($"{steps} steps, {failed} with issues");
                form.Close();
            };
            Application.Run(form);
            return failed;
        }

        static void Note(string t) { Console.WriteLine(t); File.AppendAllText(logFile, t + Environment.NewLine); }

        static async Task Step(string name, Func<Task> body)
        {
            steps++; var sw = Stopwatch.StartNew(); string err = null;
            try { await body(); } catch (Exception e) { err = e is TestFail ? e.Message : e.ToString(); }
            sw.Stop();
            Note($"[{(err == null ? "PASS" : "ISSUES")}] {name} ({sw.Elapsed.TotalSeconds:0.0}s)");
            if (err != null) { failed++; Note("    " + err); }
        }
        static Task Step(string name, Action body) => Step(name, () => { body(); return Task.CompletedTask; });
        class TestFail : Exception { public TestFail(string m) : base(m) { } }
        static void Fail(string m) => throw new TestFail(m);
        static void Check(bool ok, string m) { if (!ok) Fail(m); }

        static void Pump(int ms = 300)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < ms) { Application.DoEvents(); System.Threading.Thread.Sleep(15); }
        }
        static void Shot(string name, Form f = null)
        {
            f = f ?? form; Pump();
            using (var bmp = new Bitmap(f.Width, f.Height))
            {
                try { using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(f.Location, Point.Empty, f.Size); }
                catch { f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height)); }
                bmp.Save(Path.Combine(outDir, name + ".png"), ImageFormat.Png);
            }
        }

        /// <summary>Every button and combo box on the page must be visible and fully inside the window.</summary>
        static void CheckLayout(Page p)
        {
            var client = form.ClientRectangle; var bad = new List<string>();
            void Walk(Control c, bool scrolls)
            {
                foreach (Control k in c.Controls)
                {
                    bool sc = scrolls || (k is ScrollableControl s && s.AutoScroll);
                    if ((k is ButtonBase || k is ComboBox) && !scrolls)
                    {
                        var r = form.RectangleToClient(k.RectangleToScreen(k.ClientRectangle));
                        if (!k.Visible) bad.Add($"'{k.Text}' is hidden");
                        else if (!client.Contains(r)) bad.Add($"'{k.Text}' is outside the window at {r} (window {client})");
                        else if (r.Width < 20 || r.Height < 15) bad.Add($"'{k.Text}' is too small ({r})");
                    }
                    Walk(k, sc);
                }
            }
            Walk(p, false);
            if (bad.Count > 0) Fail(string.Join("; ", bad));
        }

        static string Row(ListViewItem i) => $"{i.Text}: {i.SubItems[1].Text} files, {i.SubItems[2].Text} | {i.SubItems[3].Text}";

        // ================================================================ the tests
        static async Task RunAll()
        {
            Note("Windows: " + Environment.OSVersion.VersionString + (Environment.Is64BitOperatingSystem ? " 64-bit" : ""));
            Note(".NET: " + Environment.Version + "   screen: " + Screen.PrimaryScreen.Bounds + "   admin: " +
                 new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent()).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator));
            Pump(800);
            Note($"Window: {form.Size}  Title: {form.Text}  Pages: {string.Join(", ", form.Pages.Select(p => p.Title))}");
            var cl = form.Page<CleanupPage>();

            // ---------------------------------------------------------------- layout
            foreach (var size in new[] { new Size(1024, 720), new Size(960, 640) })
            {
                form.Size = size; Pump(200);
                foreach (var p in form.Pages)
                    await Step($"Layout: {p.Title} at {size.Width}x{size.Height}", () => { form.ShowPage(p); Pump(150); CheckLayout(p); Shot($"layout-{size.Width}x{size.Height}-{p.Title}"); });
            }
            form.Size = new Size(1024, 720); form.ShowPage(cl); Pump(200);

            // ---------------------------------------------------------------- dashboard
            var dash = form.Page<DashboardPage>();
            form.ShowPage(dash); Pump(200);
            foreach (var size in new[] { new Size(1024, 720), new Size(960, 640) })
                await Step($"Dashboard: responsive layout at {size.Width}x{size.Height}", () =>
                {
                    form.Size = size; Pump(300);
                    Note($"    score and actions stacked: {dash.Stacked}   tile columns: {dash.TileColumns}");
                    var bad = new List<string>();
                    foreach (var t in dash.Tiles.Values) if (t.Width < 150 || t.Height < 50) bad.Add($"tile '{t.Title}' too small ({t.Size})");
                    foreach (var c in dash.Hw.Values) if (c.Width < 200 || c.Height < 150) bad.Add($"card '{c.Title}' too small ({c.Size})");
                    if (dash.List.Width < 500) bad.Add($"recommendations list too narrow ({dash.List.Width})");
                    if (bad.Count > 0) Fail(string.Join("; ", bad));
                });
            form.Size = new Size(1024, 720); Pump(200);
            await Step("Dashboard: live hardware monitor", () =>
            {
                var sw = Stopwatch.StartNew(); while (dash.Samples < 3 && sw.ElapsedMilliseconds < 15000) Pump(200);
                Note($"    {dash.Samples} samples in {sw.ElapsedMilliseconds} ms");
                foreach (var c in dash.Hw.Values) Note($"      {c.Title}: {c.Value} | {c.Sub.Replace("\n", " / ")}");
                Note("    header: " + dash.Machine.Text);
                Check(dash.Samples >= 2, "no live samples");
                Check(dash.Hw["cpu"].Value.EndsWith("%") && dash.Hw["ram"].Value.EndsWith("%"), "CPU or memory not shown");
            });
            await Step("Dashboard: score formula", () =>
            {
                var rows = new List<Finding> { new Finding("a", Engine.Status.Problem, ""), new Finding("b", Engine.Status.Warning, ""), new Finding("c", Engine.Status.Warning, ""), new Finding("d", Engine.Status.OK, "") };
                Check(Health.Score(rows) == 75, "100 - 15 - 2x5 should be 75, got " + Health.Score(rows));
                Check(Health.Score(Enumerable.Range(0, 9).Select(i => new Finding("x", Engine.Status.Problem, ""))) == 0, "score should not go below 0");
                Check(Health.Grade(95) == "Excellent" && Health.Grade(80) == "Good" && Health.Grade(60) == "Fair" && Health.Grade(10) == "Needs attention", "grades wrong");
            });
            int histBefore = Health.LoadHistory().Count;
            await Step("Dashboard: run health check", async () =>
            {
                var sw = Stopwatch.StartNew(); await dash.RunCheckAsync(); sw.Stop();
                Note($"    {dash.Grade.Text} ({dash.Gauge.Score}) in {sw.Elapsed.TotalSeconds:0.0}s - {dash.Status.Text}");
                foreach (var f in dash.Findings.OrderBy(f => f.Status)) Note($"      [{f.Status}] {f.Area}: {f.Text}" + (f.ActionText.Length > 0 ? $"  -> {dash.Resolve(f).Text}" : ""));
                Check(dash.Gauge.Score == Health.Score(dash.Findings), "gauge doesn't match the findings");
                var areas = dash.Findings.Select(f => f.Area).Distinct().ToList();
                Check(areas.Count >= 10, $"only {areas.Count} areas checked");
                var broken = dash.Findings.Where(f => f.Text.StartsWith("Could not check") && new[] { "Storage", "Memory", "Uptime", "Startup apps", "Junk files", "Devices" }.Contains(f.Area)).ToList();
                Check(broken.Count == 0, "core checks failed: " + string.Join("; ", broken.Select(b => b.Area + " " + b.Text)));
                Check(dash.SaveReport.Enabled && dash.Run.Enabled, "buttons not re-enabled");
            });
            dash.ScrollToTop(); Pump(100); Shot("dashboard");
            await Step("Dashboard: history saved and shown", () =>
            {
                var h = Health.LoadHistory(); Note($"    {h.Count} entries (was {histBefore})  trend: {dash.Trend.Text}");
                Check(h.Count == histBefore + 1, "history entry not added");
                Check(h.Last().Score == dash.Gauge.Score, "saved score is different");
                // the PowerShell edition writes the same file with Export-Csv (BOM, quoted values)
                string ps = Path.Combine(outDir, "ps-history.csv");
                File.WriteAllText(ps, "\"Date\",\"Score\",\"Problems\",\"Warnings\",\"SystemFreeGB\"\r\n\"2026-09-01T10:00:00\",\"85\",\"0\",\"3\",\"40.5\"\r\n", new System.Text.UTF8Encoding(true));
                var p = Health.LoadHistory(ps); Check(p.Count == 1 && p[0].Score == 85 && p[0].Warnings == 3 && Math.Abs(p[0].SystemFreeGB - 40.5) < 0.01, "PowerShell history format not read");
            });
            await Step("Dashboard: save report", () =>
            {
                string f = Path.Combine(outDir, "health-report.txt"); dash.WriteReport(f);
                var txt = File.ReadAllText(f); Note("    " + txt.Split('\n').Take(3).Aggregate((a, b) => a.Trim() + " | " + b.Trim()));
                Check(txt.Contains("Health score: " + dash.Gauge.Score), "score missing from report");
            });
            await Step("Dashboard: recommendation actions", () =>
            {
                foreach (ListViewItem it in dash.List.Items)
                {
                    var f = (Finding)it.Tag; var (action, text) = dash.Resolve(f); if (action.Length == 0) continue;
                    it.Selected = true; Pump(50);
                    Check(dash.DoAction.Enabled && dash.DoAction.Text == text, $"button not set for {f.Area}");
                    int before = Shell.TestOpened.Count; var page = form.Current;
                    dash.DoAction.PerformClick(); Pump(50);
                    string what = Shell.TestOpened.Count > before ? "opens " + Shell.TestOpened.Last() : form.Current != page ? "shows page " + form.Current.Title : "(nothing)";
                    Note($"    {f.Area}: '{text}' {what}");
                    if (what == "(nothing)" && action != "battery") Fail($"{f.Area}: action did nothing");
                    form.ShowPage(dash); Pump(50);
                }
                // pages that aren't in this edition yet fall back to Windows' own settings
                var drives = new Finding("Storage", Engine.Status.Problem, "x", "", "page:Drives", "Free up space").Fallback("settings:ms-settings:storagesense", "Open Storage settings");
                Check(dash.Resolve(drives).Action == "settings:ms-settings:storagesense", "fallback not used for a missing page");
                var clean = new Finding("Junk files", Engine.Status.Warning, "x", "", "page:Cleanup", "Open Cleanup");
                dash.RunAction(clean); Pump(50); Check(form.Current is CleanupPage, "page action did not open Cleanup"); form.ShowPage(dash);
            });
            dash.ScrollTo(dash.RecommendationsCard); Pump(200); Shot("dashboard-recommendations"); dash.ScrollToTop();
            await Step("Dashboard: one-click tiles", async () =>
            {
                foreach (var t in dash.Tiles.Values) Note($"    {t.Title}: {(t.Available ? "ready" : "later phase")} - {t.Sub}");
                Check(dash.Tiles["clean"].Available && dash.Tiles["restore"].Available, "Quick clean / Restore point should work");
                Check(!dash.Tiles["repair"].Available, "Repair tile should wait for its page");
                int m = Msg.Log.Count; await dash.QuickAction("repair"); Check(Msg.Log.Count == m + 1, "unavailable tile gave no message");
                await dash.QuickAction("restore"); Note("    Restore point: " + dash.Tiles["restore"].Sub);
            });

            // ---------------------------------------------------------------- settings
            await Step("Settings: shared file keeps other keys", () =>
            {
                Settings.Set("CSharpSelfTest", "ok"); Settings.Save(); Settings.Load();
                Check(Settings.GetString("CSharpSelfTest") == "ok", "value not saved");
                Note("    file: " + Settings.FilePath + "  TempAge=" + Settings.GetString("TempAge") + "  TempMode=" + Settings.GetString("TempMode"));
                Settings.Remove("CSharpSelfTest"); Settings.Save();
            });

            // ---------------------------------------------------------------- engine
            await Step("Engine: wildcard paths, links never followed", () =>
            {
                string root = Path.Combine(CleanEngine.MyTemp, "CleanSweepEngineTest"); Reset(root);
                string outside = Path.Combine(Environment.GetEnvironmentVariable("PUBLIC"), "CleanSweepEngineOutside"); Directory.CreateDirectory(outside);
                foreach (var n in new[] { @"Profile 1\Cache\a.bin", @"Default\Cache\b.bin", @"Default\Other\c.bin", "thumbcache_32.db", "thumbcache_96.db", "keep.db" })
                    Old(Path.Combine(root, n));
                Old(Path.Combine(outside, "x.bin"));
                Junction(Path.Combine(root, @"Default\Cache\link"), outside);
                var caches = CleanEngine.FindFiles(new[] { root + @"\*\Cache" }, DateTime.Now, null).Select(x => x.Name).OrderBy(x => x).ToList();
                var thumbs = CleanEngine.FindFiles(new[] { root + @"\thumbcache_*.db" }, DateTime.Now, null).Select(x => x.Name).OrderBy(x => x).ToList();
                Note("    *\\Cache -> " + string.Join(", ", caches) + "   thumbcache_*.db -> " + string.Join(", ", thumbs));
                Check(caches.SequenceEqual(new[] { "a.bin", "b.bin" }), "cache wildcard wrong (a link may have been followed)");
                Check(thumbs.SequenceEqual(new[] { "thumbcache_32.db", "thumbcache_96.db" }), "file wildcard wrong");
                RemoveJunction(Path.Combine(root, @"Default\Cache\link")); Directory.Delete(root, true);
            });
            await Step("Engine: ignore rules (file, folder, type)", () =>
            {
                var saved = CleanEngine.IgnoreRules;
                CleanEngine.IgnoreRules = new List<string> { @"file:C:\T\a.txt", @"folder:C:\T\Keep\", @"ext:C:\T\*.log" };
                var cases = new Dictionary<string, bool> { [@"C:\T\a.txt"] = true, [@"C:\T\b.txt"] = false, [@"C:\T\Keep\x\y.bin"] = true, [@"C:\T\z.log"] = true, [@"C:\T\sub\z.log"] = false };
                foreach (var c in cases) Check(CleanEngine.IsIgnored(c.Key) == c.Value, $"{c.Key} should be {(c.Value ? "ignored" : "cleaned")}");
                CleanEngine.IgnoreRules = saved;
            });

            // ---------------------------------------------------------------- cleanup page
            await Step("Cleanup: categories", () =>
            {
                foreach (var i in cl.Rows) Note($"    [{(i.Checked ? "x" : " ")}] {i.Text}  ({cl.Target(i).Kind})");
                var names = cl.Rows.Select(i => i.Text).ToList();
                Check(!names.Contains("Prefetch Files"), "Prefetch should be gone");
                var rb = cl.Rows.FirstOrDefault(i => cl.Target(i).Kind == TargetKind.Recycle);
                Check(rb != null && !rb.Checked, "Recycle Bin row should exist and be unticked by default");
                Check(names.Contains("User Temp Files") && names.Contains("Windows Temp Files"), "temp rows missing");
            });
            await Step("Cleanup: scan recommended", async () =>
            {
                cl.TickDefaults(); await cl.ScanAsync(); Note("    " + cl.Status.Text);
                foreach (var i in cl.Rows.Where(i => i.Checked)) Note("      " + Row(i));
                Check(cl.Status.Text.StartsWith("Found"), "scan did not finish");
            });
            Shot("cleanup-after-scan");
            await Step("Cleanup: window stays responsive while scanning", async () =>
            {
                foreach (var i in cl.Rows) i.Checked = true;
                int ticks = 0; var t = new Timer { Interval = 50 }; t.Tick += (s, e) => ticks++; t.Start();
                var sw = Stopwatch.StartNew(); await cl.ScanAsync(); sw.Stop(); t.Stop();
                Note($"    {cl.Status.Text}  ({sw.ElapsedMilliseconds} ms, UI timer ticked {ticks} times)");
                foreach (var i in cl.Rows) Note("      " + Row(i));
                if (sw.ElapsedMilliseconds > 500) Check(ticks > sw.ElapsedMilliseconds / 50 / 4, "UI thread was blocked during the scan");
            });
            await Step("Cleanup: clean recommended", async () =>
            {
                cl.TickDefaults(); await cl.ScanAsync(); await cl.CleanAsync();
                Note("    " + cl.Status.Text); Note("    could not delete: " + cl.Problems.Items.Count);
                foreach (ListViewItem it in cl.Problems.Items.Cast<ListViewItem>().Take(5)) Note($"      {it.Text} | {it.SubItems[2].Text} | {it.SubItems[3].Text}");
                Check(cl.Status.Text.StartsWith("Freed"), "clean did not finish");
            });
            Shot("cleanup-after-clean");
            await Step("Cleanup: Cancel stops a scan", async () =>
            {
                foreach (var i in cl.Rows) i.Checked = true;
                var t = new Timer { Interval = 120 }; t.Tick += (s, e) => { t.Stop(); if (cl.Stop.Enabled) cl.Stop.PerformClick(); }; t.Start();
                await cl.ScanAsync(); t.Stop(); Note("    " + cl.Status.Text);
                if (cl.Status.Text != "Scan cancelled.") Note("    (scan finished before cancel)");
                Check(cl.Scan.Enabled && !cl.Stop.Enabled, "buttons not restored");
            });
            cl.TickDefaults();
            await Step("Cleanup: old Recycle Bin items only", () =>
            {
                string f = Path.Combine(CleanEngine.MyTemp, "CleanSweepRecycleTest.txt"); File.WriteAllText(f, "x");
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(f, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                var now = CleanEngine.OldRecycleItems(0, new[] { AppPaths.SystemDrive }).ToList();
                var old = CleanEngine.OldRecycleItems(30, new[] { AppPaths.SystemDrive }).ToList();
                Note($"    items in bin (any age): {now.Count}   older than 30 days: {old.Count}");
                Check(now.Count > 0, "test item not found in the Recycle Bin");
                Check(!old.Any(o => File.GetLastWriteTime(o.InfoFile) > DateTime.Now.AddDays(-30)), "recent item treated as old");
            });

            // ---------------------------------------------------------------- files that can't be deleted
            string d = Path.Combine(CleanEngine.MyTemp, "CleanSweepTempTest");
            string outside2 = Path.Combine(Environment.GetEnvironmentVariable("PUBLIC"), "CleanSweepOutside");
            FileStream lockA = null;
            await Step("Temp: clean, and list files that can't be deleted", async () =>
            {
                if (Directory.Exists(Path.Combine(d, "link"))) RemoveJunction(Path.Combine(d, "link"));
                Reset(d); Directory.CreateDirectory(outside2); Old(Path.Combine(outside2, "keep.txt"));
                Junction(Path.Combine(d, "link"), outside2);
                foreach (var n in new[] { "old.txt", "readonly.txt", "locked.txt", "keep.log", @"sub\nested.txt" }) Old(Path.Combine(d, n));
                File.WriteAllText(Path.Combine(d, "new.txt"), "x");
                File.SetAttributes(Path.Combine(d, "readonly.txt"), FileAttributes.ReadOnly);
                lockA = new FileStream(Path.Combine(d, "locked.txt"), FileMode.Open, FileAccess.Read, FileShare.None);
                cl.SetIgnore(new[] { "file:" + Path.Combine(d, "keep.log") });
                cl.Age.SelectedItem = "1 day"; cl.Mode.SelectedIndex = 0;
                foreach (var i in cl.Rows) i.Checked = i.Text == "User Temp Files";
                await cl.ScanAsync(); Note("    scan: " + cl.Status.Text + " | " + cl.Rows.First(i => i.Checked).SubItems[3].Text);
                await cl.CleanAsync(); Note("    clean: " + cl.Status.Text);
                foreach (ListViewItem it in cl.Problems.Items) Note($"    can't delete: {it.Text} | {it.SubItems[2].Text} | used by: {it.SubItems[3].Text}");
                var bad = new List<string>();
                if (File.Exists(Path.Combine(d, "old.txt"))) bad.Add("old file not deleted");
                if (File.Exists(Path.Combine(d, "readonly.txt"))) bad.Add("read-only file not deleted");
                if (File.Exists(Path.Combine(d, @"sub\nested.txt"))) bad.Add("nested file not deleted");
                if (!File.Exists(Path.Combine(d, "new.txt"))) bad.Add("recent file was deleted");
                if (!File.Exists(Path.Combine(d, "keep.log"))) bad.Add("ignored file was deleted");
                if (!File.Exists(Path.Combine(outside2, "keep.txt"))) bad.Add("SAFETY: file behind a junction was deleted");
                if (!File.Exists(Path.Combine(d, "locked.txt"))) bad.Add("locked file vanished");
                var row = cl.Problems.Items.Cast<ListViewItem>().FirstOrDefault(i => i.Text == "locked.txt");
                if (row == null) bad.Add("locked file not listed");
                else if (row.SubItems[2].Text != "In use" || !row.SubItems[3].Text.Contains("PID")) bad.Add($"reason/app not shown ({row.SubItems[2].Text} / {row.SubItems[3].Text})");
                if (bad.Count > 0) Fail(string.Join("; ", bad));
            });
            Shot("temp-files");
            await Step("Temp: Always ignore + ignore list", () =>
            {
                foreach (ListViewItem it in cl.Problems.Items) it.Checked = it.Text == "locked.txt";
                cl.AddIgnore("file"); Note("    " + cl.Status.Text); Note("    rules: " + string.Join(" | ", CleanEngine.IgnoreRules));
                Check(cl.Problems.Items.Count == 0, "ignored file still listed");
                string lp = Path.Combine(d, "locked.txt");
                Check(CleanEngine.IsIgnored(lp), "rule not applied");
                cl.ShowIgnoreList(); cl.IgnoreForm.TopMost = true; Shot("temp-ignore-list", cl.IgnoreForm);
                cl.IgnoreBox.SelectedIndex = CleanEngine.IgnoreRules.IndexOf("file:" + lp); cl.RemoveSelectedRules(); cl.IgnoreForm.Close();
                Check(!CleanEngine.IsIgnored(lp), "rule not removed");
                Note("    after removing: " + string.Join(" | ", CleanEngine.IgnoreRules));
            });
            await Step("Temp: Retry after the app lets go", async () =>
            {
                await cl.ScanAsync(); await cl.CleanAsync();
                Check(cl.Problems.Items.Cast<ListViewItem>().Any(i => i.Text == "locked.txt"), "locked file not listed again");
                lockA.Dispose();
                foreach (ListViewItem it in cl.Problems.Items) it.Checked = true;
                await cl.RetryAsync(); Note("    " + cl.Status.Text);
                Check(!File.Exists(Path.Combine(d, "locked.txt")), "retry did not delete the file");
            });
            await Step("Temp: Delete at next restart", async () =>
            {
                string p = Path.Combine(d, "locked2.txt"); Old(p);
                using (new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    await cl.ScanAsync(); await cl.CleanAsync();
                    foreach (ListViewItem it in cl.Problems.Items) it.Checked = it.Text == "locked2.txt";
                    cl.DeleteAtRestart(); Note("    " + cl.Status.Text);
                }
                var pend = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager")?.GetValue("PendingFileRenameOperations") as string[];
                var hit = pend?.FirstOrDefault(x => x.EndsWith("locked2.txt", StringComparison.OrdinalIgnoreCase));
                Note("    pending at restart: " + hit);
                Check(hit != null, "not scheduled for deletion at restart");
            });
            await Step("Temp: 'Skip it' mode", async () =>
            {
                string p = Path.Combine(d, "locked3.txt"); Old(p);
                using (new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    cl.Mode.SelectedIndex = 1; await cl.ScanAsync(); await cl.CleanAsync(); Note("    " + cl.Status.Text);
                    Check(cl.Problems.Items.Count == 0, "problems listed in Skip mode");
                    Check(cl.Status.Text.Contains("Skipped"), "skip not reported");
                }
                cl.Mode.SelectedIndex = 0;
            });
            cl.SetIgnore(new string[0]); cl.TickDefaults();
            try { RemoveJunction(Path.Combine(d, "link")); Directory.Delete(d, true); Directory.Delete(outside2, true); } catch { }

            await Step("Dashboard: Quick clean tile", async () =>
            {
                form.ShowPage(dash); Pump(100);
                await dash.QuickAction("clean"); Note("    Quick clean: " + cl.Status.Text);
                Check(form.Current is CleanupPage, "Quick clean should show the Cleanup page");
                Check(!cl.Busy && cl.Status.Text.Length > 0, "Quick clean did not finish");
            });
            await Step("Messages asked during the test", () => { foreach (var m in Msg.Log) Note("  " + m); foreach (var o in Shell.TestOpened) Note("  [would open] " + o); });
            form.ShowPage(cl); Shot("cleanup-final");
            form.ShowPage(dash); Pump(300); Shot("dashboard-final"); form.Size = new Size(960, 640); Pump(300); dash.ScrollToTop(); Shot("dashboard-960"); form.Size = new Size(1024, 720);
        }

        // ---------------------------------------------------------------- helpers
        static void Reset(string dir) { try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { } Directory.CreateDirectory(dir); }
        static void Old(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllText(path, "x");
            File.SetLastWriteTime(path, DateTime.Now.AddDays(-3));
        }
        static void Junction(string link, string target)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(link));
            var p = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") { CreateNoWindow = true, UseShellExecute = false });
            p.WaitForExit();
            if (!Directory.Exists(link)) Fail("could not create test junction");
        }
        static void RemoveJunction(string link) { try { if (Directory.Exists(link)) Directory.Delete(link, false); } catch { } }
    }
}
