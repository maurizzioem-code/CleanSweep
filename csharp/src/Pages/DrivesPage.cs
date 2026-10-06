using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using CleanSweep.Engine;
using CleanSweep.UI;

namespace CleanSweep.Pages
{
    /// <summary>Live output of a Windows tool (dark, monospaced, read-only).</summary>
    public class ToolOutput : TextBox
    {
        public ToolOutput()
        {
            Multiline = true; ReadOnly = true; ScrollBars = ScrollBars.Vertical; WordWrap = true; BorderStyle = BorderStyle.None;
            BackColor = Theme.Card; ForeColor = Theme.Text; Font = new Font("Consolas", 9.5f); Dock = DockStyle.Fill;
            Native.OnHandle(this, h => Native.DarkWindow(h, "DarkMode_Explorer"));
        }
        public void Line(string s) { AppendText(s + "\r\n"); }
        /// <summary>Shows <paramref name="baseText"/> followed by the tool's current output, scrolled to the end.</summary>
        public void Live(string baseText, string tool) { Text = baseText + tool; SelectionStart = TextLength; ScrollToCaret(); }
    }

    /// <summary>Only one Windows tool runs at a time (Drives and Repair share this).</summary>
    public static class ToolLock
    {
        public static string Running;
        public static bool Busy => Running != null;
        public static bool Check()
        {
            if (!Busy) return true;
            Msg.Show($"\"{Running}\" is still running. Wait for it to finish or cancel it first.", "CleanSweep", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return false;
        }
    }

    /// <summary>
    /// Drives: every volume, read-only by default. Actions use Windows' own tools (chkdsk /scan, defrag /O) on the ticked drives.
    /// Partitions are never changed; hidden Windows partitions are listed but can't be ticked.
    /// </summary>
    public class DrivesPage : Page
    {
        public readonly Label Status = Ui.Title("Tick the drives you want, then choose an action.", 12.5f);
        public readonly CsListView List = new CsListView(true) { Dock = DockStyle.Top, Height = 200, ShowItemToolTips = true };
        public readonly CsButton Junk = new CsButton("Clean junk"), Large = new CsButton("Find large files"), CheckBtn = new CsButton("Check for errors"),
            Optimize = new CsButton("Optimize", true), Stop = new CsButton("Cancel") { Enabled = false }, Reload = new CsButton("Refresh"), DiskMgmt = new CsButton("Disk Management");
        public readonly CsProgress Progress = new CsProgress { Dock = DockStyle.Top };
        public readonly ToolOutput Out = new ToolOutput();
        readonly Label help = new Label
        {
            Dock = DockStyle.Top, Height = 40, ForeColor = Theme.Sub, UseMnemonic = false,
            Text = "Check for errors scans while you keep working and changes nothing. Optimize lets Windows trim SSDs and defragment hard drives - " +
                   "the same thing its weekly maintenance does. CleanSweep never resizes, formats or deletes partitions."
        };
        public List<DriveRow> Rows { get; private set; } = new List<DriveRow>();
        public bool Busy { get; private set; }
        public Task Running { get; private set; } = Task.CompletedTask;
        /// <summary>Self-test: cancel each tool after this many seconds (0 = never).</summary>
        public int AutoCancelSec;
        CancellationTokenSource cts; bool loading, loaded;

        public DrivesPage() : base("Drives", Glyph.Drive)
        {
            List.AddColumns(("Volume", 200), ("Type", 100), ("File system", 85), ("Status", 80), ("Capacity", 90), ("Free space", 90), ("% Free", 60), ("Disk", 70));
            var bar = Ui.Row(DockStyle.Top, 8);
            bar.Controls.AddRange(new Control[] { Optimize, CheckBtn, Junk, Large, Stop, Reload, DiskMgmt });
            Ui.Stack(this, Status, List, bar, help, Progress, Ui.Spacer(6), Out);

            List.ItemCheck += (s, e) => { if (!loading && ((DriveRow)List.Items[e.Index].Tag).IsSystemPart) e.NewValue = CheckState.Unchecked; };
            Reload.Click += (s, e) => LoadList();
            DiskMgmt.Click += (s, e) => Shell.Open("diskmgmt.msc");
            Stop.Click += (s, e) => { cts?.Cancel(); Status.Text = "Cancelling..."; };
            CheckBtn.Click += async (s, e) => await RunTool("check");
            Optimize.Click += async (s, e) => await RunTool("optimize");
            Junk.Click += async (s, e) => await CleanJunk();
            Large.Click += async (s, e) => await FindLarge();
        }

        public override void OnShown() { if (!loaded && !Busy) LoadList(); }

        public void LoadList()
        {
            var prev = List.Items.Cast<ListViewItem>().Where(i => ((DriveRow)i.Tag).Letter != null).ToDictionary(i => ((DriveRow)i.Tag).Letter, i => i.Checked);
            Cursor = Cursors.WaitCursor;
            try { Rows = Drives.List(); } finally { Cursor = Cursors.Default; }
            loading = true; List.BeginUpdate(); List.Items.Clear();
            foreach (var r in Rows)
            {
                var it = new ListViewItem(r.Name) { Tag = r, UseItemStyleForSubItems = false };
                foreach (var s in new[] { r.Media, r.FS, r.Health, Fmt.Size(r.Size), r.Free != null ? Fmt.Size(r.Free.Value) : "", r.Pct != null ? r.Pct + " %" : "", r.Disk }) it.SubItems.Add(s);
                if (r.IsSystemPart) { it.ForeColor = Theme.Faint; foreach (ListViewItem.ListViewSubItem si in it.SubItems) si.ForeColor = Theme.Faint; it.ToolTipText = "Windows needs this partition. CleanSweep never changes it."; }
                else
                {
                    it.Checked = prev.TryGetValue(r.Letter, out var c) ? c : !loaded && r.IsBoot;
                    if (r.Pct != null && r.Pct < 10) { it.SubItems[5].ForeColor = Theme.Bad; it.SubItems[6].ForeColor = Theme.Bad; }
                    if (r.Health != "Healthy") it.SubItems[3].ForeColor = Theme.Bad;
                }
                List.Items.Add(it);
            }
            List.EndUpdate(); loading = false; loaded = true;
            var low = Rows.Where(r => r.Letter != null && r.Pct != null && r.Pct < 10).ToList();
            Status.Text = low.Count > 0 ? $"{string.Join(", ", low.Select(r => r.Letter))} almost full. Tick it and try Clean junk or Find large files." : "Tick the drives you want, then choose an action.";
        }

        public List<DriveRow> Ticked => List.CheckedItems.Cast<ListViewItem>().Select(i => (DriveRow)i.Tag).Where(r => r.Letter != null).ToList();
        bool NeedTicked() { if (Ticked.Count > 0) return true; Msg.Show("Tick at least one drive first.", "CleanSweep", MessageBoxButtons.OK, MessageBoxIcon.Information); return false; }

        void SetBusy(bool b, string what = null)
        {
            Busy = b; ToolLock.Running = b ? what : null;
            foreach (var c in new Control[] { Junk, Large, CheckBtn, Optimize, Reload, List }) c.Enabled = !b;
            Stop.Enabled = b; var f = FindForm(); if (f != null) f.Cursor = b ? Cursors.AppStarting : Cursors.Default;
        }

        /// <summary>"check" = chkdsk /scan (online, read-only), "optimize" = defrag /O (TRIM or defragment, whichever suits the drive).</summary>
        public Task RunTool(string kind) => Running = DoTool(kind);
        async Task DoTool(string kind)
        {
            if (Busy || !ToolLock.Check() || !NeedTicked()) return;
            if (!ConsoleTool.IsAdmin) { Msg.Show("This needs administrator rights. Start CleanSweep as administrator and try again.", "CleanSweep", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            var drives = Ticked; Out.Clear(); cts = new CancellationTokenSource(); var summary = new List<string>();
            SetBusy(true, kind == "check" ? "Check for errors" : "Optimize");
            try
            {
                foreach (var d in drives)
                {
                    if (cts.IsCancellationRequested) break;
                    Progress.Value = 0;
                    string title = kind == "check" ? $"Checking {d.Letter} for errors" : $"Optimizing {d.Letter}";
                    var (exe, args) = kind == "check" ? (ConsoleTool.SysExe("chkdsk.exe"), $"{d.Letter} /scan") : (ConsoleTool.SysExe("defrag.exe"), $"{d.Letter} /O /U /V");
                    Out.Line($">>> {title}  ({exe} {args})"); string baseText = Out.Text;
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    if (AutoCancelSec > 0) cts.CancelAfter(AutoCancelSec * 1000);
                    var timer = new System.Windows.Forms.Timer { Interval = 1000 }; timer.Tick += (s, e) => Status.Text = $"{title}... {sw.Elapsed:mm\\:ss}" + (Progress.Value > 0 ? $"  ({Progress.Value}%)" : ""); timer.Start();
                    var r = await ConsoleTool.Run(exe, args, title, (txt, pct) => { Out.Live(baseText, txt); if (pct != null) Progress.Value = pct.Value; }, cts.Token);
                    timer.Stop(); timer.Dispose();
                    string v = kind == "check" ? Drives.CheckVerdict(d.Letter, r) : Drives.OptimizeVerdict(d, r);
                    summary.Add($"{d.Letter} {v}"); Out.Line(""); Out.Line($"Result: {d.Letter} {v}"); Out.Line("");
                }
            }
            finally { SetBusy(false); }
            bool cancelled = cts.IsCancellationRequested;
            Progress.Value = cancelled ? 0 : 100;
            Status.Text = cancelled && summary.All(x => x.EndsWith("cancelled")) ? "Cancelled." : string.Join("; ", summary);
            if (kind == "optimize") LoadList();
        }

        /// <summary>Opens Cleanup with the right rows ticked: recommended junk for the Windows drive, leftover temp files for other drives.</summary>
        public async Task CleanJunk()
        {
            if (!NeedTicked()) return;
            var want = new HashSet<string>(Ticked.Select(d => d.Letter), StringComparer.OrdinalIgnoreCase);
            var cl = Program.Form.Page<CleanupPage>(); if (cl == null || cl.Busy) return;
            Program.Form.ShowPage(cl);
            foreach (var it in cl.Rows)
            {
                var t = (CleanTarget)it.Tag;
                it.Checked = t.Kind == TargetKind.Drive ? want.Contains(t.Drive) : t.Kind == TargetKind.Paths ? want.Contains(AppPaths.SystemDrive) && t.DefaultOn : false;
            }
            await cl.ScanAsync();
        }

        public async Task FindLarge()
        {
            if (!NeedTicked()) return;
            var lf = Program.Form.Page<LargeFilesPage>(); if (lf == null || lf.Busy) return;
            var want = Ticked.Select(d => d.Letter + "\\").ToList();
            Program.Form.ShowPage(lf);
            foreach (var cb in lf.Drives) cb.Checked = want.Contains((string)cb.Tag, StringComparer.OrdinalIgnoreCase);
            await lf.SearchAsync(want);
        }
    }
}
