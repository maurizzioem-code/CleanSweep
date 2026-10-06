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
        static int closeAttempts;
        static AutoClean.TaskState taskBefore = new AutoClean.TaskState();

        public static int Run(MainForm f, string dir)
        {
            outDir = Path.GetFullPath(dir); Directory.CreateDirectory(outDir);
            logFile = Path.Combine(outDir, "log.txt"); File.WriteAllText(logFile, "");
            form = f; form.StartPosition = FormStartPosition.Manual; form.Location = new Point(0, 0);
            form.Size = new Size(1024, 720); form.TopMost = true;
            // The test changes settings (ignore list, temp age/mode). Settings are shared with the PowerShell
            // edition, so keep a copy and put the user's own settings back afterwards.
            string saved = null; try { if (File.Exists(Settings.FilePath)) saved = File.ReadAllText(Settings.FilePath); } catch { }
            // ...and the user's automatic cleanup task, if they have one
            taskBefore = AutoClean.State(); string taskXml = taskBefore.Exists ? taskBefore.Xml : null;
            // ...and the repair results list (the test runs a few read-only checks)
            string repairSaved = null; try { if (File.Exists(Repair.ResultFile)) repairSaved = File.ReadAllText(Repair.ResultFile); } catch { }
            // ...and the automatic cleanup history (the test's own runs shouldn't show up as yours)
            string acHistSaved = null; try { if (File.Exists(AutoClean.HistoryFile)) acHistSaved = File.ReadAllText(AutoClean.HistoryFile); } catch { }
            bool done = false, restored = false;
            // Clicking X (or Alt+F4) while the test runs would end it early - keep the window open until it's finished
            form.FormClosing += (s, e) =>
            {
                if (done || e.CloseReason != CloseReason.UserClosing) return;
                e.Cancel = true; closeAttempts++;
                if (closeAttempts == 1) Note("    (the window was asked to close during the test - it stays open until the test is finished)");
            };
            void Restore()
            {
                if (restored) return; restored = true;
                try { if (acHistSaved != null) File.WriteAllText(AutoClean.HistoryFile, acHistSaved, new System.Text.UTF8Encoding(true)); else if (File.Exists(AutoClean.HistoryFile)) File.Delete(AutoClean.HistoryFile); } catch { }
                try { if (repairSaved != null) File.WriteAllText(Repair.ResultFile, repairSaved, new System.Text.UTF8Encoding(true)); else if (File.Exists(Repair.ResultFile)) File.Delete(Repair.ResultFile); } catch { }
                try
                {
                    if (taskXml != null) AutoClean.RegisterXml(taskXml); else AutoClean.DeleteTaskOnly();
                    Note(taskXml != null ? "Your automatic cleanup schedule was put back as it was." : "No automatic cleanup schedule was left behind.");
                }
                catch (Exception ex) { Note("Could not put the schedule back: " + ex.Message); failed++; }
                try
                {
                    if (saved != null) File.WriteAllText(Settings.FilePath, saved, new System.Text.UTF8Encoding(false)); else if (File.Exists(Settings.FilePath)) File.Delete(Settings.FilePath);
                    Settings.Load(); Note("Your settings were put back as they were before the test.");
                }
                catch (Exception ex) { Note("Could not put settings back: " + ex.Message); failed++; }
            }
            form.Shown += async (s, e) =>
            {
                try { await RunAll(); }
                catch (Exception ex) { Note("TEST HARNESS ERROR: " + ex); failed++; }
                Restore();
                Note(""); Note($"{steps} steps, {failed} with issues");
                done = true; form.Close();
            };
            try { Application.Run(form); }
            catch (Exception ex) { Note("TEST HARNESS ERROR: " + ex); failed++; }
            if (!done)
            {
                // the window went away before the test finished (Windows closed it, or the app crashed)
                Restore(); failed++;
                Note(""); Note($"TEST STOPPED EARLY after {steps} steps - the window was closed. {steps} steps, {failed} with issues");
            }
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
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
        [System.Runtime.InteropServices.DllImport("gdi32.dll")] static extern int GetDeviceCaps(IntPtr hdc, int index);
        static void SetSize(Size s) { if (form.WindowState != FormWindowState.Normal) form.WindowState = FormWindowState.Normal; form.Size = s; }
        static void Shot(string name, Form f = null)
        {
            f = f ?? form; Pump();
            if (f == form && form.WindowState != FormWindowState.Normal) { Note($"    (window was {form.WindowState} - restored for the screenshot)"); SetSize(new Size(1024, 720)); Pump(); }
            using (var bmp = new Bitmap(f.Width, f.Height))
            {
                // PrintWindow works at any display scaling (125%, 150%...); copying the screen crops on scaled displays
                bool ok = false;
                using (var g = Graphics.FromImage(bmp)) { var dc = g.GetHdc(); try { ok = PrintWindow(f.Handle, dc, 2 /* PW_RENDERFULLCONTENT */); } finally { g.ReleaseHdc(dc); } }
                if (!ok) f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height));
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
            void Clip(Control c)
            {
                foreach (Control k in c.Controls)
                {
                    if (k is Label l && !l.AutoSize && !l.AutoEllipsis && l.Visible && l.Text.Length > 0 && l.Width > 0)
                    {
                        int need = TextRenderer.MeasureText(l.Text, l.Font, new Size(l.Width - l.Padding.Horizontal, 0), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height;
                        if (need > l.Height - l.Padding.Vertical + 2) bad.Add($"text cut off: '{(l.Text.Length > 40 ? l.Text.Substring(0, 40) + "..." : l.Text)}' needs {need}px, has {l.Height - l.Padding.Vertical}px");
                    }
                    Clip(k);
                }
            }
            Clip(p);
            if (bad.Count > 0) Fail(string.Join("; ", bad));
        }

        static string Row(ListViewItem i) => $"{i.Text}: {i.SubItems[1].Text} files, {i.SubItems[2].Text} | {i.SubItems[3].Text}";

        // ================================================================ the tests
        static async Task RunAll()
        {
            Note("Windows: " + Environment.OSVersion.VersionString + (Environment.Is64BitOperatingSystem ? " 64-bit" : ""));
            Note(".NET: " + Environment.Version + "   screen: " + Screen.PrimaryScreen.Bounds + "   admin: " +
                 new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent()).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator));
            using (var g = Graphics.FromHwnd(IntPtr.Zero)) { var dc = g.GetHdc(); int phys = GetDeviceCaps(dc, 118 /* DESKTOPHORZRES */); g.ReleaseHdc(dc); Note($"Display scaling: {Math.Round(phys * 100.0 / Screen.PrimaryScreen.Bounds.Width)}% ({phys} pixels wide)"); }
            Pump(800);
            Note($"Window: {form.Size}  Title: {form.Text}  Pages: {string.Join(", ", form.Pages.Select(p => p.Title))}");
            var cl = form.Page<CleanupPage>();

            // ---------------------------------------------------------------- layout
            foreach (var size in new[] { new Size(1024, 720), new Size(960, 640) })
            {
                SetSize(size); Pump(200);
                foreach (var p in form.Pages)
                    await Step($"Layout: {p.Title} at {size.Width}x{size.Height}", () => { form.ShowPage(p); Pump(150); CheckLayout(p); Shot($"layout-{size.Width}x{size.Height}-{p.Title}"); });
            }
            SetSize(new Size(1024, 720)); form.ShowPage(cl); Pump(200);

            // ---------------------------------------------------------------- dashboard
            var dash = form.Page<DashboardPage>();
            form.ShowPage(dash); Pump(200);
            foreach (var size in new[] { new Size(1024, 720), new Size(960, 640) })
                await Step($"Dashboard: responsive layout at {size.Width}x{size.Height}", () =>
                {
                    SetSize(size); Pump(300);
                    Note($"    score and actions stacked: {dash.Stacked}   tile columns: {dash.TileColumns}");
                    var bad = new List<string>();
                    foreach (var t in dash.Tiles.Values) if (t.Width < 150 || t.Height < 50) bad.Add($"tile '{t.Title}' too small ({t.Size})");
                    foreach (var c in dash.Hw.Values) if (c.Width < 200 || c.Height < 150) bad.Add($"card '{c.Title}' too small ({c.Size})");
                    if (dash.List.Width < 500) bad.Add($"recommendations list too narrow ({dash.List.Width})");
                    if (bad.Count > 0) Fail(string.Join("; ", bad));
                });
            SetSize(new Size(1024, 720)); Pump(200);
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
                Note($"    Finding column {dash.List.Columns[2].Width}px, list {dash.List.ClientSize.Width}px");
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
                    string what = Shell.TestOpened.Count > before ? "opens " + Shell.TestOpened.Last() : form.Current != page ? "shows page " + form.Current.Title : dash.ScheduleForm != null ? "opens the schedule settings" : "(nothing)";
                    dash.ScheduleForm?.Close();
                    Note($"    {f.Area}: '{text}' {what}");
                    if (what == "(nothing)" && action != "battery") Fail($"{f.Area}: action did nothing");
                    form.ShowPage(dash); Pump(50);
                }
                // pages that aren't in this edition yet fall back to Windows' own settings
                var net = new Finding("Internet", Engine.Status.Problem, "x", "", "page:Not Yet Built", "Open it").Fallback("settings:ms-settings:network-status", "Open Network settings");
                Check(dash.Resolve(net).Action == "settings:ms-settings:network-status", "fallback not used for a missing page");
                var clean = new Finding("Junk files", Engine.Status.Warning, "x", "", "page:Cleanup", "Open Cleanup");
                dash.RunAction(clean); Pump(50); Check(form.Current is CleanupPage, "page action did not open Cleanup"); form.ShowPage(dash);
            });
            Pump(300); dash.ScrollTo(dash.RecommendationsCard); Pump(200); Note("    scrolled to recommendations: " + dash.RecommendationsCard.Top); Shot("dashboard-recommendations"); dash.ScrollToTop();
            await Step("Dashboard: one-click tiles", async () =>
            {
                foreach (var t in dash.Tiles.Values) Note($"    {t.Title}: {(t.Available ? "ready" : "later phase")} - {t.Sub}");
                Check(dash.Tiles["clean"].Available && dash.Tiles["restore"].Available, "Quick clean / Restore point should work");
                Check(dash.Tiles.Values.All(t => t.Available), "all one-click tiles should work now: " + string.Join(", ", dash.Tiles.Values.Where(t => !t.Available).Select(t => t.Title)));
                await dash.QuickAction("restore"); Note("    Restore point: " + dash.Tiles["restore"].Sub + "  (" + RestorePoint.LastResult + ")");
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
            SetSize(new Size(1024, 720)); form.ShowPage(cl); Pump(200);
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
                // plant an old junk file so there is always something to clean (e.g. when the test runs twice)
                string junk = Path.Combine(CleanEngine.MyTemp, "CleanSweepSelfTest-old.tmp");
                File.WriteAllText(junk, new string('x', 4096)); var old = DateTime.Now.AddDays(-120);
                File.SetCreationTime(junk, old); File.SetLastWriteTime(junk, old); File.SetLastAccessTime(junk, old);
                cl.TickDefaults(); await cl.ScanAsync(); await cl.CleanAsync();
                Note("    " + cl.Status.Text); Note("    could not delete: " + cl.Problems.Items.Count);
                Check(!File.Exists(junk), "old test junk file was not cleaned");
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
            // ---------------------------------------------------------------- automatic cleanup
            await Step("Automatic cleanup: settings window creates the task", () =>
            {
                form.ShowPage(dash); Pump(100);
                Note("    before: " + (taskBefore.Exists ? "task exists (" + taskBefore.Command + ")" : "no task"));
                dash.ShowSchedule(); var f = dash.ScheduleForm; Pump(200); Shot("autoclean-settings", f);
                f.On.Checked = true; f.Freq.SelectedIndex = 1; f.Day.SelectedItem = "Saturday"; f.Time.SelectedIndex = 38;
                foreach (var c in f.Cats) c.Checked = CleanEngine.DefaultOn.Contains((string)c.Tag);
                f.RecycleOn.Checked = false; f.ACOnly.Checked = true; f.Idle.Checked = false; f.CatchUp.Checked = true; f.Notify.Checked = false;
                f.DoSave(); Pump(200);
                Check(f.Saved, "not saved: " + f.Tip.Text);
                var st = AutoClean.State(); Note($"    task: {st.Command}  next run {st.NextRun}");
                Check(st.Exists && st.Enabled, "task not created");
                Check(st.Xml.Contains("<Saturday />") && st.Xml.Contains("<WeeksInterval>1</WeeksInterval>"), "wrong day or interval");
                Check(st.Xml.Contains("<DisallowStartIfOnBatteries>true</DisallowStartIfOnBatteries>") && st.Xml.Contains("<StartWhenAvailable>true</StartWhenAvailable>"), "conditions not saved");
                Check(st.Command.StartsWith(AutoClean.RunnerExe, StringComparison.OrdinalIgnoreCase) && File.Exists(AutoClean.RunnerExe), "task should run the installed copy " + AutoClean.RunnerExe);
                Check(st.NextRun != null && st.NextRun.Value.DayOfWeek == DayOfWeek.Saturday && st.NextRun.Value.Hour == 19, "next run should be a Saturday at 7 PM");
                Note("    card: " + dash.AcStatus.Text);
                Check(dash.AcStatus.Text.StartsWith("On - every Saturday at 7:00"), "card not updated");
                var cfg = AutoCleanConfig.Load(); Check(cfg.Enabled && !cfg.Notify && cfg.Cats.Count > 0, "settings not saved");
            });
            await Step("Automatic cleanup: daily and every 4 weeks", () =>
            {
                var cfg = AutoCleanConfig.Load();
                cfg.Freq = "Daily"; AutoClean.Schedule(cfg); var a = AutoClean.State();
                Check(a.Xml.Contains("<DaysInterval>1</DaysInterval>"), "daily schedule wrong"); Note("    daily: next run " + a.NextRun);
                cfg.Freq = "Monthly"; cfg.Day = "Wednesday"; cfg.Idle = true; AutoClean.Schedule(cfg); var b = AutoClean.State();
                Check(b.Xml.Contains("<WeeksInterval>4</WeeksInterval>") && b.Xml.Contains("<Wednesday />") && b.Xml.Contains("<RunOnlyIfIdle>true</RunOnlyIfIdle>"), "4-weekly schedule wrong");
                Note("    every 4 weeks: next run " + b.NextRun + "  - " + cfg.Describe());
                cfg.Freq = "Weekly"; cfg.Day = "Saturday"; cfg.Idle = false; AutoClean.Schedule(cfg);
            });
            string acJunk = Path.Combine(CleanEngine.MyTemp, "CleanSweepAutoCleanTest-old.tmp");
            await Step("Automatic cleanup: Run now", async () =>
            {
                Old(acJunk); int before = AutoClean.History().Count;
                var sw = Stopwatch.StartNew(); await dash.RunAutoCleanNow();
                var h = AutoClean.History(); Note($"    {sw.Elapsed.TotalSeconds:0.0}s  card: {dash.AcLast.Text}");
                Check(h.Count == before + 1 || (before >= 100 && h.Count == 100), "no history entry");
                Check(h.Last().Trigger == "Manual", "trigger should be Manual");
                Note("    " + AutoClean.Summary(h.Last()) + "  [" + h.Last().Details + "]");
                Check(!File.Exists(acJunk), "old junk file was not cleaned");
                Check(dash.AcLast.Text.StartsWith("Last run"), "card not updated");
            });
            await Step("Automatic cleanup: the scheduled task really runs", async () =>
            {
                Old(acJunk); int before = AutoClean.History().Count(r => r.Trigger == "Scheduled");
                AutoClean.RunTaskNow(); var sw = Stopwatch.StartNew();
                while (AutoClean.History().Count(r => r.Trigger == "Scheduled") == before && sw.ElapsedMilliseconds < 120000) { Pump(500); await Task.Delay(1); }
                var last = AutoClean.History().LastOrDefault();
                Note($"    after {sw.Elapsed.TotalSeconds:0}s: {(last != null ? last.Trigger + " - " + AutoClean.Summary(last) : "nothing")}");
                try { foreach (var l in File.ReadAllLines(AutoClean.LogFile).Reverse().Take(2).Reverse()) Note("    log: " + l); } catch { }
                Check(last != null && last.Trigger == "Scheduled" && AutoClean.History().Count(r => r.Trigger == "Scheduled") > before, "Task Scheduler did not run the cleanup");
                Check(!File.Exists(acJunk), "old junk file was not cleaned by the scheduled run");
            });
            await Step("Automatic cleanup: health check and PowerShell history format", () =>
            {
                var f = Health.RunCheck("Automatic cleanup", Health.Checks.First(c => c.Name == "Automatic cleanup").Run);
                Note("    " + string.Join(" | ", f.Select(x => $"[{x.Status}] {x.Text}")));
                Check(f.Count == 1 && f[0].Status == Engine.Status.OK, "health check should say On");
                string ps = Path.Combine(outDir, "ps-autoclean-history.csv");
                File.WriteAllText(ps, "\"Time\",\"Trigger\",\"Freed\",\"Files\",\"Skipped\",\"Seconds\",\"Details\"\r\n\"2026-09-27T19:00:04.1234567-06:00\",\"Scheduled\",\"52428800\",\"120\",\"7\",\"3\",\"User Temp Files 50.0 MB; Recycle Bin 0 KB\"\r\n", new System.Text.UTF8Encoding(true));
                var h = AutoClean.History(ps); Check(h.Count == 1 && h[0].Freed == 52428800 && h[0].Files == 120 && h[0].Details.Contains("; "), "PowerShell history not read");
            });
            dash.ScrollToTop(); Pump(200); Shot("dashboard-autoclean");
            await Step("Automatic cleanup: turning it off removes the task", () =>
            {
                dash.ShowSchedule(); var f = dash.ScheduleForm; f.On.Checked = false; f.DoSave(); Pump(200);
                Check(!AutoClean.State().Exists, "task still exists");
                Check(!AutoCleanConfig.Load().Enabled, "still marked as on");
                Note("    card: " + dash.AcStatus.Text); Check(dash.AcStatus.Text.StartsWith("Off"), "card not updated");
            });

            // ---------------------------------------------------------------- large files
            var lf = form.Page<LargeFilesPage>();
            string lroot = Path.Combine(Environment.GetEnvironmentVariable("PUBLIC"), "CleanSweepLargeTest"), lout = Path.Combine(Environment.GetEnvironmentVariable("PUBLIC"), "CleanSweepLargeOutside");
            await Step("Large files: finds big files, never follows links", async () =>
            {
                form.ShowPage(lf); Pump(100);
                if (Directory.Exists(Path.Combine(lroot, "link"))) RemoveJunction(Path.Combine(lroot, "link"));
                Reset(lroot); Reset(lout);
                void Big(string path, long mb) { Directory.CreateDirectory(Path.GetDirectoryName(path)); using (var fs = File.Create(path)) fs.SetLength(mb << 20); }
                Big(Path.Combine(lroot, "old-backup.iso"), 120); Big(Path.Combine(lroot, @"Videos\holiday.mp4"), 150);
                Big(Path.Combine(lroot, @"AppData\Local\SomeGame\data.pak"), 110); Big(Path.Combine(lroot, "small.bin"), 5);
                Big(Path.Combine(lout, "outside.bin"), 130); Junction(Path.Combine(lroot, "link"), lout);
                lf.MinSize.SelectedIndex = 0; lf.HideApps.Checked = true;
                await lf.SearchAsync(new[] { lroot });
                Note("    " + lf.Status.Text); Note("    " + lf.Summary.Text);
                foreach (var x in lf.Found) Note($"      {Fmt.Size(x.Size),10}  {x.Kind,-20} {x.Path}");
                Check(lf.Found.Count == 3, "expected 3 large files, found " + lf.Found.Count);
                Check(!lf.Found.Any(x => x.Name == "outside.bin"), "followed a link to another folder");
                Check(lf.Found.First(x => x.Name == "old-backup.iso").Kind == "Disk image" && lf.Found.First(x => x.Name == "holiday.mp4").Kind == "Video", "wrong types");
                Check(lf.Found.First(x => x.Name == "data.pak").IsApp, "AppData file should count as an app file");
                Check(lf.List.Items.Count == 2, "app file should be hidden by default");
                lf.HideApps.Checked = false; Pump(50); Check(lf.List.Items.Count == 3, "app file should show when not hidden"); lf.HideApps.Checked = true;
                Check(lf.List.CheckedItems.Count == 0, "nothing should be ticked for the user");
                Check(((LargeFile)lf.List.Items[0].Tag).Name == "holiday.mp4", "biggest file should be first");
            });
            await Step("Large files: Move ticked to Recycle Bin", () =>
            {
                var it = lf.List.Items.Cast<ListViewItem>().First(i => ((LargeFile)i.Tag).Name == "old-backup.iso"); it.Checked = true; Pump(50);
                Check(lf.Recycle.Enabled, "button should enable when a file is ticked"); Note("    " + lf.Ticked.Text);
                string path = ((LargeFile)it.Tag).Path; lf.RecycleTicked(); Pump(100);
                Note("    " + lf.Status.Text);
                Check(!File.Exists(path), "file still there"); Check(File.Exists(Path.Combine(lroot, @"Videos\holiday.mp4")), "unticked file was touched");
                Check(lf.List.Items.Count == 1, "list not updated");
                Note("    removed from Recycle Bin again: " + PurgeFromRecycleBin(path));
            });
            Shot("large-files-test");
            try { RemoveJunction(Path.Combine(lroot, "link")); Directory.Delete(lroot, true); Directory.Delete(lout, true); } catch { }
            await Step("Large files: search the Windows drive", async () =>
            {
                foreach (var dcb in lf.Drives) dcb.Checked = ((string)dcb.Tag).StartsWith(AppPaths.SystemDrive, StringComparison.OrdinalIgnoreCase);
                Note("    drives: " + string.Join(" | ", lf.Drives.Select(x => x.Text)));
                lf.MinSize.SelectedIndex = 0; var sw = Stopwatch.StartNew(); var t = lf.SearchAsync(); int ticks = 0;
                var ui = new System.Windows.Forms.Timer { Interval = 100 }; ui.Tick += (s2, e2) => ticks++; ui.Start();
                while (!t.IsCompleted && sw.ElapsedMilliseconds < 90000) { Pump(200); await Task.Delay(1); }
                bool cancelled = false;
                if (!t.IsCompleted) { Note($"    still searching after 90 s ({Fmt.Count(lf.Found.Count)} found so far) - testing Cancel"); lf.Stop.PerformClick(); cancelled = true; }
                await t; ui.Stop();
                Note($"    {lf.Status.Text}  (UI timer ticked {ticks} times in {sw.Elapsed.TotalSeconds:0} s)");
                Check(ticks > sw.ElapsedMilliseconds / 400, "window froze while searching");
                if (cancelled) Check(lf.Status.Text.StartsWith("Cancelled"), "Cancel did not stop the search");
                else
                {
                    Note("    " + lf.Summary.Text);
                    foreach (var x in lf.ShownFiles.Take(8)) Note($"      {Fmt.Size(x.Size),10}  {x.Kind,-12} {x.Path}");
                    Check(lf.Status.Text.StartsWith("Found") || lf.Status.Text.StartsWith("No files"), "search did not finish");
                    long disk = new DriveInfo(AppPaths.SystemDrive).TotalSize;
                    Check(lf.Found.All(x => x.Size <= disk && File.Exists(x.Path)), "file sizes or names are wrong");
                    Check(lf.Found.Count == 0 || lf.Found.Any(x => x.Folder.Length > 3), "did not look inside folders");
                }
            });
            Shot("large-files");
            await Step("Dashboard: Free up space tile opens Large files", () =>
            {
                form.ShowPage(dash); Pump(100);
                Check(dash.Tiles["space"].Available, "tile should be available now"); Note("    " + dash.Tiles["space"].Sub);
                var storage = new Finding("Storage", Engine.Status.Problem, "x", "", "page:Large files", "Find large files").Fallback("settings:ms-settings:storagesense", "Open Storage settings");
                Check(dash.Resolve(storage).Action == "page:Large files", "storage finding should open Large files");
                dash.RunAction(storage); Pump(50); Check(form.Current is LargeFilesPage, "did not open Large files"); form.ShowPage(dash);
            });

            // ---------------------------------------------------------------- drives
            var dv = form.Page<DrivesPage>();
            await Step("Drives: list of volumes", () =>
            {
                form.ShowPage(dv); Pump(200); dv.LoadList(); Pump(100);
                foreach (var r in dv.Rows) Note($"    {r.Name,-28} {r.Media,-12} {r.FS,-6} {r.Health,-9} {Fmt.Size(r.Size),10}  free {(r.Free != null ? Fmt.Size(r.Free.Value) : "-"),10}  {r.Disk}");
                var boot = dv.Rows.FirstOrDefault(r => r.IsBoot);
                Check(boot != null && boot.Size > 0 && boot.Free != null, "Windows drive missing from the list");
                Check(dv.Ticked.Count == 1 && dv.Ticked[0].IsBoot, "only the Windows drive should be ticked at first");
                var hidden = dv.List.Items.Cast<ListViewItem>().FirstOrDefault(i => ((DriveRow)i.Tag).IsSystemPart);
                if (hidden != null) { hidden.Checked = true; Pump(50); Check(!hidden.Checked, "hidden Windows partitions must not be tickable"); Note("    hidden partition can't be ticked: " + hidden.Text); }
                else Note("    (no hidden partitions on this PC)");
            });
            await Step("Drives: Optimize the Windows drive", async () =>
            {
                foreach (ListViewItem i in dv.List.Items) i.Checked = ((DriveRow)i.Tag).IsBoot;
                var sw = Stopwatch.StartNew(); await dv.RunTool("optimize");
                Note($"    {sw.Elapsed.TotalSeconds:0}s: {dv.Status.Text}");
                foreach (var l in dv.Out.Lines.Where(l => l.Trim().Length > 0).Reverse().Take(6).Reverse()) Note("      | " + l.Trim());
                Check(!dv.Busy && !ToolLock.Busy, "still busy");
                Check(dv.Status.Text.Contains("optimized") || dv.Status.Text.Contains("can't be optimized"), "optimize failed: " + dv.Status.Text);
            });
            Shot("drives");
            await Step("Drives: Check for errors, then Cancel", async () =>
            {
                dv.AutoCancelSec = 6; var sw = Stopwatch.StartNew(); await dv.RunTool("check"); dv.AutoCancelSec = 0;
                Note($"    {sw.Elapsed.TotalSeconds:0}s: {dv.Status.Text}");
                Check(dv.Status.Text == "Cancelled." || dv.Status.Text.Contains("no problems found"), "unexpected result: " + dv.Status.Text);
                await Task.Delay(2000);
                Check(Process.GetProcessesByName("chkdsk").Length == 0, "chkdsk is still running after Cancel");
                Check(sw.Elapsed.TotalSeconds < 40, "Cancel took too long");
            });
            await Step("Drives: Clean junk opens Cleanup for the ticked drive", async () =>
            {
                form.ShowPage(dv); foreach (ListViewItem i in dv.List.Items) i.Checked = ((DriveRow)i.Tag).IsBoot;
                await dv.CleanJunk();
                Check(form.Current is CleanupPage, "Cleanup not shown"); Check(!cl.Busy, "scan did not finish");
                Note("    ticked: " + string.Join(", ", cl.Rows.Where(i => i.Checked).Select(i => i.Text)));
                Check(cl.Rows.Where(i => i.Checked).All(i => ((CleanTarget)i.Tag).DefaultOn), "only the recommended rows should be ticked");
            });

            // ---------------------------------------------------------------- repair
            var rp = form.Page<RepairPage>();
            await Step("Repair: tools and plain-language verdicts", () =>
            {
                form.ShowPage(rp); Pump(100);
                Note("    tools: " + string.Join(" | ", rp.List.Items.Cast<ListViewItem>().Select(i => i.Text)));
                Check(rp.List.Items.Count >= 6, "tools missing");
                ToolResult R(int code, string text) => new ToolResult { Code = code, Text = text };
                var cases = new (string Id, ToolResult R, string Expect)[]
                {
                    ("sfc", R(0, "Windows Resource Protection did not find any integrity violations."), "No problems found"),
                    ("sfc", R(0, "Windows Resource Protection found corrupt files and successfully repaired them."), "Found and repaired"),
                    ("sfc", R(0, "Windows Resource Protection found corrupt files but was unable to fix some of them."), "Some files could not be repaired"),
                    ("dism-scan", R(0, "No component store corruption detected.\r\nThe operation completed successfully."), "No damage found"),
                    ("dism-scan", R(0, "The component store is repairable."), "Damage found"),
                    ("dism-restore", R(0, "The restore operation completed successfully."), "Windows image is healthy"),
                    ("dism-restore", R(unchecked((int)0x800F081F), "Error: 0x800f081f"), "could not be downloaded (0x800F081F)"),
                    ("dism-restore", R(unchecked((int)0x800F0954), "Error: 0x800f0954"), "Windows Update policy"),
                    ("dism-check", new ToolResult { Cancelled = true }, "Cancelled"),
                };
                foreach (var c in cases) { var v = Repair.Judge(c.Id, c.R); Check(v.Text.Contains(c.Expect), $"{c.Id}: '{v.Text}' should contain '{c.Expect}'"); }
                var res = new Dictionary<string, string> { ["dism-scan"] = "Oct 1 07:42 - Damage found - run Repair Windows image", ["dism-restore"] = "Oct 3 11:42 - Windows image is healthy (repaired if needed) - now run System File Checker" };
                Check(Repair.FixedLater("dism-scan", res) == "Repair Windows image Oct 3", "old damage fixed by a later repair should be marked: " + Repair.FixedLater("dism-scan", res));
                res["dism-restore"] = "Sep 29 10:00 - Windows image is healthy"; Check(Repair.FixedLater("dism-scan", res) == null, "an earlier repair doesn't fix later damage");
                Check(ConsoleTool.Format("Progress 10%\rProgress 55%\rProgress 100%\r\nDone") == "Progress 100%\r\nDone", "progress lines not collapsed");
                Check(ConsoleTool.Percent("[====  45.3%  ]") == 45, "percent not read");
                Check(ConsoleTool.Decode(System.Text.Encoding.Unicode.GetBytes("Verification 100% complete.")) == "Verification 100% complete.", "UTF-16 output not read");
            });
            await Step("Repair: Quick Windows health check", async () =>
            {
                rp.Select("dism-check"); rp.RestorePt.Checked = false; Pump(50);
                Check(rp.Run.Enabled, "Run should enable when a tool is selected");
                var sw = Stopwatch.StartNew(); await rp.RunSelected();
                Note($"    {sw.Elapsed.TotalSeconds:0}s: {rp.Status.Text}");
                foreach (var l in rp.Out.Lines.Where(l => l.Trim().Length > 0).Reverse().Take(4).Reverse()) Note("      | " + l.Trim());
                Check(rp.LastRun.Count == 1 && rp.LastRun[0].V.Level == "OK", "health check: " + rp.Status.Text);
                Check(Repair.Results().TryGetValue("dism-check", out var saved) && saved.EndsWith(rp.LastRun[0].V.Text), "result not saved");
                var row = rp.List.Items.Cast<ListViewItem>().First(i => (string)i.Tag == "dism-check"); Note("    list: " + row.SubItems[2].Text);
                Check(Directory.GetFiles(AppPaths.Logs, "Quick-Windows-health-check-*.txt").Length > 0, "log not saved");
            });
            Shot("repair");
            await Step("Repair: Cancel stops the tool", async () =>
            {
                rp.Select("dism-scan"); rp.AutoCancelSec = 8; var sw = Stopwatch.StartNew(); await rp.RunSelected(); rp.AutoCancelSec = 0;
                Note($"    {sw.Elapsed.TotalSeconds:0}s: {rp.Status.Text}");
                Check(rp.Status.Text == "Cancelled." || (rp.LastRun.Count == 1 && rp.LastRun[0].V.Level == "OK"), "unexpected: " + rp.Status.Text);
                await Task.Delay(2000);
                Check(Process.GetProcessesByName("Dism").Length == 0, "DISM still running after Cancel");
                Check(!ToolLock.Busy && rp.Recommended.Enabled, "page still busy");
            });
            string wuRoot = Path.Combine(CleanEngine.MyTemp, "CleanSweepWuTest");
            var realFolders = Repair.WuFolders; string realJournal = Repair.Journal;
            await Step("Repair: Windows Update repair, undo and delete backups (stand-in folders)", async () =>
            {
                Reset(wuRoot);
                Repair.WuFolders = new[] { Path.Combine(wuRoot, "SoftwareDistribution"), Path.Combine(wuRoot, "catroot2") };
                Repair.Journal = Path.Combine(wuRoot, "wu-repair.json"); Repair.SkipServices = true;
                foreach (var f in Repair.WuFolders) { Directory.CreateDirectory(Path.Combine(f, "Download")); File.WriteAllText(Path.Combine(f, @"Download\update.cab"), new string('x', 50000)); }
                rp.UpdateList(); Check(!rp.List.Items.Cast<ListViewItem>().Any(i => (string)i.Tag == "wu-undo"), "Undo should be hidden before a repair");
                rp.RestorePt.Checked = true;   // also tries the restore point (on a PC with System Restore off it asks, and the test answers Yes)
                await rp.Start(new[] { "wu-reset" }, "Repair Windows Update"); Note("    reset: " + rp.Status.Text);
                foreach (var l in rp.Out.Lines.Where(l => l.Trim().Length > 0).Take(6)) Note("      | " + l.Trim());
                Check(rp.LastRun.Count == 1 && rp.LastRun[0].V.Level == "Repaired", "reset failed");
                Check(Repair.WuFolders.All(f => !Directory.Exists(f)) && Repair.WuBackups().Count == 2, "folders not set aside as backups");
                Check(rp.List.Items.Cast<ListViewItem>().Any(i => (string)i.Tag == "wu-undo") && rp.List.Items.Cast<ListViewItem>().Any(i => (string)i.Tag == "wu-purge"), "Undo / Delete backups not offered");
                Directory.CreateDirectory(Repair.WuFolders[0]);   // Windows rebuilds the cache
                rp.RestorePt.Checked = false;
                await rp.Start(new[] { "wu-undo" }, "Undo"); Note("    undo: " + rp.Status.Text);
                Check(Repair.WuFolders.All(f => File.Exists(Path.Combine(f, @"Download\update.cab"))), "original folders not put back");
                Check(!File.Exists(Repair.Journal), "journal should be gone after undo");
                await rp.Start(new[] { "wu-reset" }, "Repair Windows Update");
                await rp.Start(new[] { "wu-purge" }, "Delete backups"); Note("    delete backups: " + rp.Status.Text);
                Check(Repair.WuBackups().Count == 0 && rp.Status.Text.StartsWith("Freed"), "backups not deleted");
                var pj = Path.Combine(wuRoot, "ps-journal.json");
                File.WriteAllText(pj, "{\r\n  \"Date\": \"2026-09-01T10:00:00\",\r\n  \"Renamed\": [ { \"From\": \"C:\\\\Windows\\\\SoftwareDistribution\", \"To\": \"C:\\\\Windows\\\\SoftwareDistribution.bak-20260901-100000\" } ],\r\n  \"StartTypes\": { \"wuauserv\": \"Disabled\" }\r\n}");
                Repair.Journal = pj; var j = Repair.ReadJournal();
                Check(j != null && j.Renamed.Count == 1 && j.Renamed[0]["To"].EndsWith("bak-20260901-100000") && j.StartTypes["wuauserv"] == "Disabled", "main-app journal not read");
            });
            Repair.WuFolders = realFolders; Repair.Journal = realJournal; Repair.SkipServices = false; rp.UpdateList();
            try { Directory.Delete(wuRoot, true); } catch { }
            await Step("Dashboard: Repair and Optimize tiles open their pages", () =>
            {
                form.ShowPage(dash); Pump(100);
                Check(dash.Tiles["repair"].Available && dash.Tiles["optimize"].Available, "tiles should be available now");
                var f = new Finding("Stability", Engine.Status.Problem, "x", "", "page:Repair", "Open Repair tools");
                dash.RunAction(f); Pump(50); Check(form.Current is RepairPage, "did not open Repair"); form.ShowPage(dash);

            });

            // ---------------------------------------------------------------- shortcuts
            var sp = form.Page<ShortcutsPage>();
            string scRoot = Path.Combine(Environment.GetEnvironmentVariable("PUBLIC"), "CleanSweepShortcutTest");
            await Step("Shortcuts: finds only broken shortcuts", async () =>
            {
                form.ShowPage(sp); Pump(100); Reset(scRoot);
                string target = Path.Combine(scRoot, "real.txt"); File.WriteAllText(target, "x");
                Shortcuts.Create(Path.Combine(scRoot, "Works.lnk"), target);
                Shortcuts.Create(Path.Combine(scRoot, "Broken.lnk"), Path.Combine(scRoot, @"gone\OldApp.exe"));
                Directory.CreateDirectory(Path.Combine(scRoot, "Sub")); Shortcuts.Create(Path.Combine(scRoot, @"Sub\Broken too.lnk"), Path.Combine(scRoot, "deleted.docx"));
                Shortcuts.Create(Path.Combine(scRoot, "Network.lnk"), @"\\nas\share\file.txt");
                Shortcuts.Create(Path.Combine(scRoot, "Unplugged.lnk"), @"Q:\Games\game.exe");
                var real = sp.Dirs; sp.Dirs = new List<string> { scRoot };
                await sp.ScanAsync(); sp.Dirs = real;
                Note("    " + sp.Status.Text + "  " + string.Join(" | ", sp.Found.Select(b => b.Name + " -> " + b.Target)));
                Check(sp.Found.Count == 2 && sp.Found.Any(b => b.Name == "Broken") && sp.Found.Any(b => b.Name == "Broken too"), "expected exactly the 2 broken shortcuts");
                Check(sp.List.CheckedItems.Count == 2, "broken shortcuts should be ticked");
            });
            await Step("Shortcuts: Move ticked to Recycle Bin", () =>
            {
                string p1 = Path.Combine(scRoot, "Broken.lnk"); sp.RemoveTicked(); Pump(50);
                Note("    " + sp.Status.Text);
                Check(!File.Exists(p1) && File.Exists(Path.Combine(scRoot, "Works.lnk")), "wrong shortcuts removed");
                foreach (var f in new[] { p1, Path.Combine(scRoot, @"Sub\Broken too.lnk") }) PurgeFromRecycleBin(f);
            });
            await Step("Shortcuts: real scan of Desktop and Start menu", async () =>
            {
                var sw = Stopwatch.StartNew(); await sp.ScanAsync();
                Note($"    {sw.Elapsed.TotalSeconds:0.0}s: {sp.Status.Text}");
                foreach (var b in sp.Found.Take(10)) Note($"      {b.Name}  ->  {b.Target}");
                Check(sp.Status.Text.StartsWith("Found") || sp.Status.Text.StartsWith("No broken"), "scan did not finish");
                foreach (ListViewItem i in sp.List.Items) i.Checked = false;   // the test never removes the user's own shortcuts
            });
            Shot("shortcuts");
            try { Directory.Delete(scRoot, true); } catch { }

            // ---------------------------------------------------------------- registry
            var rg = form.Page<RegistryPage>();
            string runKey = @"Software\Microsoft\Windows\CurrentVersion\Run", upKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\CleanSweepSelfTest";
            string realBackups = RegistryScan.BackupDir; RegistryScan.BackupDir = Path.Combine(outDir, "registry-backups");
            await Step("Registry: finds leftovers from removed programs", async () =>
            {
                form.ShowPage(rg); Pump(100);
                using (var k = Registry.CurrentUser.CreateSubKey(runKey)) k.SetValue("CleanSweepSelfTest", "\"C:\\Program Files\\CleanSweepGoneApp\\gone.exe\" /background");
                using (var k = Registry.CurrentUser.CreateSubKey(upKey)) { k.SetValue("DisplayName", "CleanSweep Test App (removed)"); k.SetValue("UninstallString", "C:\\Program Files\\CleanSweepGoneApp\\uninstall.exe"); }
                var sw = Stopwatch.StartNew(); await rg.ScanAsync();
                Note($"    {sw.Elapsed.TotalSeconds:0.0}s: {rg.Status.Text}");
                foreach (var f in rg.Found.OrderByDescending(f => f.Recommended).Take(12)) Note($"      [{(f.Recommended ? "x" : " ")}] {f.Issue}  {f.Location}  ->  missing: {f.Target}");
                Check(rg.Found.Any(f => f.Value == "CleanSweepSelfTest" && f.Recommended), "test startup entry not found");
                Check(rg.Found.Any(f => f.Key.EndsWith("CleanSweepSelfTest") && f.Issue.Contains("CleanSweep Test App")), "test uninstall entry not found");
                Check(rg.Found.Where(f => f.Issue.Contains("no benefit")).All(f => !f.Recommended), "no-benefit entries must not be ticked");
                // the test only ever removes its own entries
                foreach (ListViewItem i in rg.List.Items) i.Checked = ((RegIssue)i.Tag).Location.Contains("CleanSweepSelfTest");
                Check(rg.List.CheckedItems.Count == 2, "expected 2 ticked test entries");
            });
            await Step("Registry: backup, remove, restore", async () =>
            {
                rg.RestorePt.Checked = false; await rg.CleanAsync();
                Note("    " + rg.Status.Text + "  backup: " + Path.GetFileName(rg.LastBackup));
                using (var k = Registry.CurrentUser.OpenSubKey(runKey)) Check(k?.GetValue("CleanSweepSelfTest") == null, "startup entry not removed");
                using (var k = Registry.CurrentUser.OpenSubKey(upKey)) Check(k == null, "uninstall entry not removed");
                var bak = rg.LastBackup != null ? File.ReadAllText(rg.LastBackup) : "";
                Check(bak.Contains("\"CleanSweepSelfTest\"=") && bak.Contains(@"Uninstall\CleanSweepSelfTest]"), "backup missing the removed entries");
                var others = bak.Split('\n').Where(l => l.StartsWith("\"") && !l.StartsWith("\"CleanSweepSelfTest\"") && !l.StartsWith("\"DisplayName\"") && !l.StartsWith("\"UninstallString\"")).ToList();
                Check(others.Count == 0, "backup should only hold the removed entries, also has: " + string.Join(" ", others.Take(3)));
                rg.TestRestoreFile = rg.LastBackup; rg.RestoreBackup.PerformClick(); rg.TestRestoreFile = null; Pump(100);
                using (var k = Registry.CurrentUser.OpenSubKey(upKey)) Check(k != null && (string)k.GetValue("DisplayName") == "CleanSweep Test App (removed)", "restore did not put the entry back");
                Note("    restored from backup: " + rg.Status.Text);
            });
            Shot("registry");
            try { using (var k = Registry.CurrentUser.OpenSubKey(runKey, true)) k?.DeleteValue("CleanSweepSelfTest", false); Registry.CurrentUser.DeleteSubKeyTree(upKey, false); } catch { }
            RegistryScan.BackupDir = realBackups;

            // ---------------------------------------------------------------- network
            var np = form.Page<NetworkPage>();
            await Step("Network: adapters and details", () =>
            {
                form.ShowPage(np); Pump(200);
                foreach (var a in Network.Adapters()) Note($"    {(a.Up ? "up  " : "down")} {(a.Wireless ? "Wi-Fi   " : "wired   ")} {a.Name} - {a.Description} ({Network.SpeedText(a.Speed)}){(a.Gateway != null ? " gw " + a.Gateway : "")}");
                Note($"    active: {Network.ActiveKind()}  page: {np.Kind.Text}");
                foreach (var l in np.Info.Text.Split('\n')) Note("    | " + l);
                Check(np.Kind.Text == Network.ActiveKind(), "page should start on the connection Windows uses");
                Check(np.Info.Text.Length > 10 && !np.Info.Text.StartsWith("Reading"), "no connection details");
                Check(np.List.Items.Count == 6, "expected 6 fixes, found " + np.List.Items.Count);
                var ticked = np.List.CheckedItems.Cast<ListViewItem>().Select(i => (string)i.Tag).ToList();
                Check(!ticked.Contains("stack") && !ticked.Contains("wifi-power") && !ticked.Contains("eth-power"), "deep or optional fixes must not be ticked by default");
            });
            await Step("Network: connection test", async () =>
            {
                var sw = Stopwatch.StartNew(); await np.TestAsync();
                Note($"    {sw.Elapsed.TotalSeconds:0.0}s: {np.Status.Text}");
                Check(np.Before != null && np.Before.Ping != null && np.Before.Ping < 1000, "no internet response");
                Check(np.Before.Dns != null, "DNS lookup failed");
            });
            await Step("Network: fixes run in a safe order (dry run - connection not touched)", async () =>
            {
                Network.DryRun = new List<string>();
                foreach (ListViewItem i in np.List.Items) i.Checked = true; np.DnsBox.SelectedIndex = 1;
                await np.ApplyAsync(); var cmds = Network.DryRun; Network.DryRun = null; np.DnsBox.SelectedIndex = 0; np.LoadOptions();
                foreach (var c in cmds) Note("      " + (c.Length > 150 ? c.Substring(0, 150) + "..." : c));
                Note("    " + np.Status.Text);
                Check(cmds.Any(c => c.Contains("/flushdns")) && cmds.Any(c => c.Contains("autotuninglevel=normal")) && cmds.Any(c => c.Contains("winsock reset")), "commands missing");
                Check(!cmds.Any(c => System.Text.RegularExpressions.Regex.IsMatch(c, "autotuninglevel=(disabled|highlyrestricted|experimental)|chimney|rss=|congestionprovider|TcpAckFrequency|Nagle", System.Text.RegularExpressions.RegexOptions.IgnoreCase)), "no 'gaming' TCP tweaks allowed");
                int renew = cmds.FindIndex(c => c.Contains("/renew")), dns = cmds.FindIndex(c => c.Contains("Set-DnsClientServerAddress"));
                Check(dns >= 0 && renew > dns, "DNS should be set before renewing the address");
                Check(np.After != null, "no after-test");
            });
            await Step("Network: Wi-Fi and Ethernet parsing", () =>
            {
                var nets = Network.ParseNearby("SSID 1 : Home\r\n    Network type : Infrastructure\r\n    BSSID 1 : aa:bb\r\n         Signal : 88%\r\n         Band : 5 GHz\r\n         Channel : 36\r\n    BSSID 2 : cc:dd\r\n         Signal : 40%\r\n         Channel : 6\r\nSSID 2 : \r\n    BSSID 1 : ee:ff\r\n         Signal : 20%\r\n         Channel : 6\r\n");
                Check(nets.Count == 3 && nets[0].Channel == 36 && nets[0].Band == "5 GHz" && nets[2].Ssid == "(hidden)", "nearby networks not read");
                var d = Network.ParseColon("    SSID                   : Home\r\n    Signal                 : 91%\r\n    Receive rate (Mbps)    : 866.7\r\n");
                Check(d["SSID"] == "Home" && d["Receive rate (Mbps)"] == "866.7", "Wi-Fi details not read");
                Check(Network.NeedsLocation("Network shell commands need location permission to access WLAN information."), "location prompt not detected");
            });
            if (Network.Ethernet()?.Up == true)
                await Step("Network: Ethernet diagnostics (read-only)", async () =>
                {
                    np.Kind.SelectedItem = "Ethernet"; Pump(100);
                    var sw = Stopwatch.StartNew(); await np.DiagnoseAsync();
                    Note($"    {sw.Elapsed.TotalSeconds:0}s: {np.Status.Text}");
                    foreach (var r in np.LastDiag) Note($"      [{r.Status}] {r.Area}: {r.Result}");
                    Check(np.LastDiag.Count >= 8, "diagnostics incomplete");
                    if (np.OpenDialog != null) { Shot("network-diagnostics", np.OpenDialog); np.OpenDialog.Close(); }
                    np.Kind.SelectedItem = Network.ActiveKind();
                });
            Shot("network");

            await Step("Messages asked during the test", () => { foreach (var m in Msg.Log) Note("  " + m); foreach (var o in Shell.TestOpened) Note("  [would open] " + o); });
            form.ShowPage(cl); Shot("cleanup-final");
            form.ShowPage(dash); Pump(300); Shot("dashboard-final"); SetSize(new Size(960, 640)); Pump(300); dash.ScrollToTop(); Shot("dashboard-960"); SetSize(new Size(1024, 720));
        }

        // ---------------------------------------------------------------- helpers
        /// <summary>Removes a test file from the Recycle Bin again ($I file = deletion record holding the original path).</summary>
        static string PurgeFromRecycleBin(string original)
        {
            try
            {
                string bin = Path.Combine(Path.GetPathRoot(original), "$Recycle.Bin", CleanEngine.MySid);
                foreach (var i in new DirectoryInfo(bin).GetFiles("$I*"))
                {
                    var b = File.ReadAllBytes(i.FullName); if (b.Length < 28) continue;
                    string p = BitConverter.ToInt64(b, 0) >= 2 ? System.Text.Encoding.Unicode.GetString(b, 28, Math.Min(b.Length - 28, BitConverter.ToInt32(b, 24) * 2)) : System.Text.Encoding.Unicode.GetString(b, 24, Math.Min(b.Length - 24, 520));
                    if (!p.TrimEnd('\0').Equals(original, StringComparison.OrdinalIgnoreCase)) continue;
                    string r = Path.Combine(bin, "$R" + i.Name.Substring(2));
                    if (File.Exists(r)) File.Delete(r); else if (Directory.Exists(r)) Directory.Delete(r, true);
                    i.Delete(); return "yes";
                }
                return "not found";
            }
            catch (Exception e) { return "failed: " + e.Message; }
        }
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
