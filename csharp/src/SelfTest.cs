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

            await Step("Messages asked during the test", () => { foreach (var m in Msg.Log) Note("  " + m); });
            form.ShowPage(cl); Shot("cleanup-final");
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
