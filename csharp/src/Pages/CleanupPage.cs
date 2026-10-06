using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using CleanSweep.Engine;
using CleanSweep.UI;

namespace CleanSweep.Pages
{
    /// <summary>
    /// Cleanup: junk and temp files in one list, using the shared cleaning engine.
    /// Files that can't be deleted are never forced: CleanSweep shows why (in use, protected) and which app holds them,
    /// and the user chooses Retry, Delete at next restart, Ignore this time, or Always ignore.
    /// </summary>
    public class CleanupPage : Page
    {
        public static readonly (string Name, double Days)[] Ages = { ("1 hour", 1 / 24.0), ("1 day", 1), ("7 days", 7), ("30 days", 30) };
        public static readonly (string Key, string Text)[] Modes = { ("Ask", "List it so I can decide"), ("Skip", "Skip it"), ("Restart", "Delete it when the PC restarts") };
        static readonly int[] RecycleDays = { 7, 14, 30, 60, 90 };

        public readonly Label Status = Ui.Title("Click Scan to find junk and temporary files.", 12.5f);
        public readonly ComboBox Age = Ui.Combo(90, Ages.Select(a => a.Name).ToArray());
        public readonly ComboBox Recycle = Ui.Combo(90, RecycleDays.Select(d => d + " days").ToArray());
        public readonly ComboBox Mode = Ui.Combo(230, Modes.Select(m => m.Text).ToArray());
        public readonly CsListView List = new CsListView(true) { Dock = DockStyle.Top, Height = 210 };
        public readonly CsButton Scan = new CsButton("Scan", true), Clean = new CsButton("Clean") { Enabled = false },
            Stop = new CsButton("Cancel") { Enabled = false }, IgnoreBtn = new CsButton("Ignore list");
        public readonly CsProgress Progress = new CsProgress { Dock = DockStyle.Top };
        public readonly Label ProbHeader = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.BottomLeft, Font = Theme.Section, ForeColor = Theme.Text, Text = "Files that could not be deleted" };
        public readonly CsListView Problems = new CsListView(true) { Dock = DockStyle.Fill };
        public readonly CsButton Retry = new CsButton("Retry"), AtRestart = new CsButton("Delete at next restart"), SkipOnce = new CsButton("Ignore this time"),
            Always = new CsButton("Always ignore  \u25BE"), OpenLoc = new CsButton("Open location"),
            TickAll = new CsButton("Tick all") { Dock = DockStyle.Right, MinimumSize = new Size(90, 30), Margin = new Padding(0) };
        public readonly ContextMenuStrip AlwaysMenu = new ContextMenuStrip { ShowImageMargin = false, BackColor = Theme.Card, ForeColor = Theme.Text, RenderMode = ToolStripRenderMode.System };
        readonly FlowLayoutPanel options = Ui.Row(), problemBar = Ui.Row(DockStyle.Bottom, 6);

        CancellationTokenSource cts;
        public bool Busy { get; private set; }
        public Task Running { get; private set; } = Task.CompletedTask;

        public CleanupPage() : base("Cleanup", Glyph.Junk)
        {
            options.Controls.AddRange(new Control[] { Ui.Pair(Ui.Text("Delete files older than"), Age), Ui.Pair(Ui.Text("Recycle Bin items older than"), Recycle), Ui.Pair(Ui.Text("If a file can't be deleted"), Mode) });
            Mode.Margin = new Padding(0, 3, 0, 0);
            List.AddColumns(("What to clean", 230), ("Files", 80), ("Size", 90), ("Skipped / note", 220), ("Folder", 380));
            var bar = Ui.Row(DockStyle.Top, 8); bar.Controls.AddRange(new Control[] { Scan, Clean, Stop, IgnoreBtn });
            var help = new Label
            {
                Dock = DockStyle.Top, Height = 22, ForeColor = Theme.Sub, AutoEllipsis = true,
                Text = "Nothing is forced. Close the app shown under \"Used by\" and click Retry, or choose another action for the ticked files."
            };
            var probTop = new Panel { Dock = DockStyle.Top, Height = 40, Padding = new Padding(0, 6, 0, 2) };
            probTop.Controls.Add(ProbHeader); probTop.Controls.Add(TickAll);
            Problems.AddColumns(("File", 230), ("Size", 80), ("Reason", 200), ("Used by", 190), ("Folder", 360));
            problemBar.Controls.AddRange(new Control[] { Retry, AtRestart, SkipOnce, Always, OpenLoc });
            Ui.Stack(this, Status, options, List, bar, Progress, probTop, help, problemBar, Problems);

            AlwaysMenu.Items.Add("This file", null, (s, e) => AddIgnore("file"));
            AlwaysMenu.Items.Add("Everything in this folder", null, (s, e) => AddIgnore("folder"));
            AlwaysMenu.Items.Add("All files of this type here", null, (s, e) => AddIgnore("ext"));

            // settings
            Age.SelectedItem = Settings.GetString("TempAge", "1 day"); if (Age.SelectedIndex < 0) Age.SelectedIndex = 1;
            int mi = Array.FindIndex(Modes, m => m.Key == Settings.GetString("TempMode", "Ask")); Mode.SelectedIndex = Math.Max(0, mi);
            Recycle.SelectedItem = Settings.GetInt("CleanRecycleDays", 30) + " days"; if (Recycle.SelectedIndex < 0) Recycle.SelectedIndex = 2;
            Age.SelectedIndexChanged += (s, e) => { Settings.Set("TempAge", Age.SelectedItem.ToString()); Settings.Save(); Clean.Enabled = false; Status.Text = "Age changed - click Scan again."; };
            Mode.SelectedIndexChanged += (s, e) => { Settings.Set("TempMode", Modes[Mode.SelectedIndex].Key); Settings.Save(); };
            Recycle.SelectedIndexChanged += (s, e) => { Settings.Set("CleanRecycleDays", RecycleDays[Recycle.SelectedIndex]); Settings.Save(); Clean.Enabled = false; };
            CleanEngine.IgnoreRules = Settings.GetList("TempIgnore"); UpdateIgnoreButton();

            Scan.Click += async (s, e) => await ScanAsync();
            Clean.Click += async (s, e) => await CleanAsync();
            Stop.Click += (s, e) => cts?.Cancel();
            IgnoreBtn.Click += (s, e) => ShowIgnoreList();
            TickAll.Click += (s, e) => { foreach (ListViewItem it in Problems.Items) it.Checked = true; };
            Retry.Click += async (s, e) => await RetryAsync();
            AtRestart.Click += (s, e) => DeleteAtRestart();
            SkipOnce.Click += (s, e) => { var t = NeedTicked(); foreach (var it in t) Problems.Items.Remove(it); UpdateProblemHeader(); if (t.Count > 0) Status.Text = $"Left {t.Count} file(s) alone this time."; };
            Always.Click += (s, e) => AlwaysMenu.Show(Always, 0, Always.Height);
            OpenLoc.Click += (s, e) => { var t = Ticked().FirstOrDefault(); if (t != null) Shell.ShowInExplorer(((CleanItem)t.Tag).FullName); };
            Problems.DoubleClick += (s, e) => { if (Problems.SelectedItems.Count > 0) Shell.ShowInExplorer(((CleanItem)Problems.SelectedItems[0].Tag).FullName); };

            LoadRows(); UpdateProblemHeader();
        }

        public double AgeDays => Ages[Math.Max(0, Age.SelectedIndex)].Days;
        public string ModeKey => Modes[Math.Max(0, Mode.SelectedIndex)].Key;
        public IEnumerable<ListViewItem> Rows => List.Items.Cast<ListViewItem>();
        public CleanTarget Target(ListViewItem i) => (CleanTarget)i.Tag;

        public void LoadRows()
        {
            List.BeginUpdate(); List.Items.Clear();
            foreach (var t in CleanEngine.CleanupRows())
            {
                var i = new ListViewItem(t.Name) { Checked = t.DefaultOn, Tag = t };
                i.SubItems.Add("-"); i.SubItems.Add("-"); i.SubItems.Add(t.Note);
                i.SubItems.Add(t.Kind == TargetKind.Recycle ? "All drives" : string.Join("; ", t.Paths.Take(2)));
                List.Items.Add(i);
            }
            List.EndUpdate();
        }
        /// <summary>Ticks the recommended rows only.</summary>
        public void TickDefaults() { foreach (var i in Rows) i.Checked = Target(i).DefaultOn; }

        void SetBusy(bool b)
        {
            Busy = b; Scan.Enabled = !b; Stop.Enabled = b; List.Enabled = !b; options.Enabled = !b; problemBar.Enabled = !b;
            if (b) Clean.Enabled = false;
            var f = FindForm(); if (f != null) f.Cursor = b ? Cursors.WaitCursor : Cursors.Default;
        }

        // ---------------------------------------------------------------- scan
        public Task ScanAsync() => Running = DoScan();
        async Task DoScan()
        {
            if (Busy) return;
            cts = new CancellationTokenSource(); var ct = cts.Token;
            DateTime cut = DateTime.Now.AddDays(-AgeDays); int recycleDays = RecycleDays[Math.Max(0, Recycle.SelectedIndex)];
            SetBusy(true); long total = 0; int count = 0;
            try
            {
                foreach (var i in Rows.ToList())
                {
                    var t = Target(i);
                    if (!i.Checked) { i.SubItems[1].Text = "-"; i.SubItems[2].Text = "-"; t.Found = new List<CleanItem>(); continue; }
                    Status.Text = $"Scanning {t.Name}...";
                    var stat = new ScanStat();
                    t.Found = await Task.Run(() =>
                    {
                        switch (t.Kind)
                        {
                            case TargetKind.Drive: return CleanEngine.DriveJunk(t.Drive, cut, stat, ct).ToList();
                            case TargetKind.Recycle: return CleanEngine.OldRecycleItems(recycleDays).ToList();
                            default: return CleanEngine.FindFiles(t.Paths, cut, stat, ct).ToList();
                        }
                    }, ct);
                    t.Size = t.Found.Sum(f => f.Length); total += t.Size; count += t.Found.Count;
                    i.SubItems[1].Text = Fmt.Count(t.Found.Count); i.SubItems[2].Text = Fmt.Size(t.Size);
                    string sk = stat.Describe(); i.SubItems[3].Text = sk.Length > 0 ? sk : t.Note;
                }
                Status.Text = $"Found {Fmt.Size(total)} in {Fmt.Files(count)} older than {Age.SelectedItem}.";
                SetBusy(false); Clean.Enabled = count > 0;
            }
            catch (OperationCanceledException) { SetBusy(false); Status.Text = "Scan cancelled."; }
            catch (Exception e) { SetBusy(false); Status.Text = "Scan failed: " + e.Message; Trace.Write("Scan: " + e); }
        }

        // ---------------------------------------------------------------- clean
        class Failure { public CleanItem Item; public Exception Error; public string Reason, Who; }

        public Task CleanAsync() => Running = DoClean();
        async Task DoClean()
        {
            if (Busy) return;
            var sel = Rows.Where(i => i.Checked && Target(i).Found.Count > 0).ToList();
            if (sel.Count == 0) return;
            int n = sel.Sum(i => Target(i).Found.Count);
            if (!Msg.Confirm($"Delete {Fmt.Files(n)} from: {string.Join(", ", sel.Select(i => i.Text))}?")) return;

            string mode = ModeKey; DateTime cut = DateTime.Now.AddDays(-AgeDays);
            cts = new CancellationTokenSource(); var ct = cts.Token;
            SetBusy(true); Problems.Items.Clear();
            long freed = 0; int done = 0, failed = 0, atBoot = 0, k = 0; bool cancelled = false;
            var failures = new List<Failure>();
            var progress = new Progress<int>(v => Progress.Value = v);
            try
            {
                foreach (var i in sel)
                {
                    var t = Target(i); Status.Text = $"Cleaning {t.Name}...";
                    await Task.Run(() =>
                    {
                        foreach (var f in t.Found)
                        {
                            if (++k % 50 == 0) { ((IProgress<int>)progress).Report((int)(100L * k / n)); ct.ThrowIfCancellationRequested(); }
                            var err = CleanEngine.TryDelete(f);
                            if (err == null) { freed += f.Length; done++; continue; }
                            failed++;
                            if (mode == "Skip") continue;
                            if (mode == "Restart" && !f.IsRecycle && FileLocks.DeleteAtRestart(f.FullName)) { atBoot++; continue; }
                            var (reason, who) = CleanEngine.Describe(err, f.FullName);
                            failures.Add(new Failure { Item = f, Error = err, Reason = reason, Who = who });
                        }
                        if (t.Kind == TargetKind.Paths && t.Name.Contains("Temp")) foreach (var p in t.Paths) CleanEngine.RemoveEmptyDirs(p, cut);
                    }, ct);
                    t.Found = new List<CleanItem>(); i.SubItems[1].Text = "Cleaned";
                }
            }
            catch (OperationCanceledException) { cancelled = true; }
            catch (Exception e) { cancelled = true; Trace.Write("Clean: " + e); }
            Problems.BeginUpdate(); foreach (var f in failures) AddProblem(f.Item, f.Reason, f.Who); Problems.EndUpdate();
            Progress.Value = 0; SetBusy(false); UpdateProblemHeader();

            string msg = cancelled ? $"Cleaning stopped. Freed {Fmt.Size(freed)} before cancelling." : $"Freed {Fmt.Size(freed)} ({Fmt.Files(done)}).";
            if (failed > 0)
            {
                if (mode == "Skip") msg += $" Skipped {failed} that could not be deleted.";
                else if (mode == "Restart") msg += $" {atBoot} will be deleted at the next restart" + (failed - atBoot > 0 ? $"; {failed - atBoot} are listed below." : ".");
                else msg += $" {failed} could not be deleted - see the list below.";
            }
            try { var d = new DriveInfo(AppPaths.SystemDrive); msg += $"  {AppPaths.SystemDrive} now has {Fmt.Size(d.AvailableFreeSpace)} free."; } catch { }
            Status.Text = msg;
            Trace.Write("Cleanup: " + msg);
        }

        void AddProblem(CleanItem f, string reason, string who)
        {
            var it = new ListViewItem(f.Name) { Tag = f, UseItemStyleForSubItems = true };
            it.SubItems.Add(Fmt.Size(f.Length)); it.SubItems.Add(reason); it.SubItems.Add(who); it.SubItems.Add(f.DirectoryName);
            if (reason == "In use") it.ForeColor = Theme.Warn;
            Problems.Items.Add(it);
        }
        public void UpdateProblemHeader()
        {
            int n = Problems.Items.Count;
            ProbHeader.Text = n > 0 ? $"Files that could not be deleted ({n})" : "Files that could not be deleted";
            foreach (var b in new Control[] { Retry, AtRestart, SkipOnce, Always, OpenLoc, TickAll }) b.Enabled = n > 0;
        }

        // ---------------------------------------------------------------- actions for files that could not be deleted
        List<ListViewItem> Ticked()
        {
            var t = Problems.CheckedItems.Cast<ListViewItem>().ToList();
            if (t.Count == 0 && Problems.SelectedItems.Count > 0) t.Add(Problems.SelectedItems[0]);
            return t;
        }
        List<ListViewItem> NeedTicked()
        {
            var t = Ticked();
            if (t.Count == 0) Msg.Show("Tick the files first (or click Tick all).", "CleanSweep", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return t;
        }

        public Task RetryAsync() => Running = DoRetry();
        async Task DoRetry()
        {
            var items = NeedTicked(); if (items.Count == 0) return;
            int ok = 0; long freed = 0;
            foreach (var it in items)
            {
                var f = (CleanItem)it.Tag;
                var err = await Task.Run(() => (!f.IsRecycle && !File.Exists(f.FullName)) ? null : CleanEngine.TryDelete(f));
                if (err == null) { freed += f.Length; Problems.Items.Remove(it); ok++; }
                else { var (reason, who) = CleanEngine.Describe(err, f.FullName); it.SubItems[2].Text = reason; it.SubItems[3].Text = who; }
            }
            UpdateProblemHeader();
            int left = items.Count - ok;
            Status.Text = $"Retry: deleted {ok} file(s), freed {Fmt.Size(freed)}." + (left > 0 ? $" {left} still can't be deleted - close the app shown, or choose another action." : "");
        }

        public void DeleteAtRestart()
        {
            var items = NeedTicked(); if (items.Count == 0) return;
            int ok = 0;
            foreach (var it in items)
            {
                if (FileLocks.DeleteAtRestart(((CleanItem)it.Tag).FullName)) { Problems.Items.Remove(it); ok++; }
                else it.SubItems[2].Text = "Windows refused to schedule this file";
            }
            UpdateProblemHeader(); Status.Text = $"{ok} file(s) will be deleted the next time you restart the PC.";
        }

        public void AddIgnore(string kind)
        {
            var items = NeedTicked(); if (items.Count == 0) return;
            var rules = items.Select(it =>
            {
                var f = (CleanItem)it.Tag; string dir = f.DirectoryName.TrimEnd('\\');
                switch (kind)
                {
                    case "folder": return "folder:" + dir + "\\";
                    case "ext": { string x = Path.GetExtension(f.Name); return x.Length > 0 ? "ext:" + dir + "\\*" + x : "file:" + f.FullName; }
                    default: return "file:" + f.FullName;
                }
            }).Distinct().ToList();
            SetIgnore(CleanEngine.IgnoreRules.Concat(rules));
            // Drop every listed file the new rules cover
            foreach (var it in Problems.Items.Cast<ListViewItem>().ToList()) if (CleanEngine.IsIgnored(((CleanItem)it.Tag).FullName)) Problems.Items.Remove(it);
            UpdateProblemHeader();
            Status.Text = $"Added {rules.Count} rule(s) to the ignore list. CleanSweep (and automatic cleanup) will leave these files alone.";
        }

        public void SetIgnore(IEnumerable<string> rules)
        {
            CleanEngine.IgnoreRules = rules.Where(r => !string.IsNullOrEmpty(r)).Distinct().ToList();
            Settings.SetList("TempIgnore", CleanEngine.IgnoreRules); Settings.Save(); UpdateIgnoreButton();
        }
        void UpdateIgnoreButton() => IgnoreBtn.Text = $"Ignore list ({CleanEngine.IgnoreRules.Count})";

        // ---------------------------------------------------------------- ignore list window
        public static string FormatRule(string r)
        {
            if (r.StartsWith("file:")) return "File:  " + r.Substring(5);
            if (r.StartsWith("folder:")) return "Folder:  " + r.Substring(7);
            if (r.StartsWith("ext:")) return "Type:  " + r.Substring(4);
            return r;
        }
        public Form IgnoreForm { get; private set; }
        public ListBox IgnoreBox { get; private set; }
        public void ShowIgnoreList()
        {
            var f = new Form { Text = "Ignore list", Size = new Size(720, 420), StartPosition = FormStartPosition.CenterParent, MinimizeBox = false, ShowInTaskbar = false, Icon = FindForm()?.Icon };
            var hdr = new Label { Dock = DockStyle.Top, Height = 44, Padding = new Padding(12, 10, 12, 0), ForeColor = Theme.Sub, Text = "CleanSweep never deletes these files, on the Cleanup page or during automatic cleanup." };
            var lb = new ListBox { Dock = DockStyle.Fill, SelectionMode = SelectionMode.MultiExtended, IntegralHeight = false };
            foreach (var r in CleanEngine.IgnoreRules) lb.Items.Add(FormatRule(r));
            var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 56, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(12, 10, 12, 10), BackColor = Theme.Side };
            var close = new CsButton("Close") { DialogResult = DialogResult.OK, MinimumSize = new Size(100, 34), Margin = new Padding(8, 0, 0, 0) };
            var rm = new CsButton("Remove selected") { MinimumSize = new Size(140, 34), Margin = new Padding(8, 0, 0, 0) };
            rm.Click += (s, e) => RemoveSelectedRules();
            bar.Controls.AddRange(new Control[] { close, rm });
            Ui.Stack(f, hdr, bar, lb);
            f.AcceptButton = close; Theme.DarkDialog(f); hdr.ForeColor = Theme.Sub;
            IgnoreForm = f; IgnoreBox = lb;
            if (Msg.Test) { f.Show(FindForm()); Application.DoEvents(); }
            else { f.ShowDialog(FindForm()); f.Dispose(); IgnoreForm = null; }
        }
        public void RemoveSelectedRules()
        {
            var idx = IgnoreBox.SelectedIndices.Cast<int>().ToList();
            var keep = CleanEngine.IgnoreRules.Where((r, n) => !idx.Contains(n)).ToList();
            SetIgnore(keep);
            IgnoreBox.Items.Clear(); foreach (var r in keep) IgnoreBox.Items.Add(FormatRule(r));
        }
    }
}
