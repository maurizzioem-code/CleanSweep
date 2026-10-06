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
    /// Repair: Windows' own repair tools (DISM, System File Checker, Windows Update reset). Each runs only when clicked,
    /// output is shown live and saved, and the Windows Update repair keeps backups so it can be undone.
    /// </summary>
    public class RepairPage : Page
    {
        public readonly Label Status = Ui.Title("Select a repair tool, then click Run. Not sure? Click Recommended repair.", 12.5f);
        public readonly CsListView List = new CsListView(false) { Dock = DockStyle.Top, Height = 196, MultiSelect = false };
        public readonly Label Info = Ui.Note("DISM repairs Windows' own store of system files; System File Checker then uses it to fix your installed files.");
        public readonly CsCheckBox RestorePt = new CsCheckBox("Create a System Restore point before repairs (recommended)", true) { Margin = new Padding(0, 4, 0, 0) };
        public readonly CsButton Run = new CsButton("Run selected") { Enabled = false }, Recommended = new CsButton("Recommended repair", true),
            Stop = new CsButton("Cancel") { Enabled = false }, Logs = new CsButton("Open logs"), Cbs = new CsButton("Open CBS log");
        public readonly CsProgress Progress = new CsProgress { Dock = DockStyle.Top };
        public readonly ToolOutput Out = new ToolOutput();
        readonly FlowLayoutPanel opts = Ui.Row(DockStyle.Top, 2);
        public bool Busy { get; private set; }
        public Task Running { get; private set; } = Task.CompletedTask;
        public int AutoCancelSec;   // self-test
        public readonly List<(string Id, Verdict V)> LastRun = new List<(string, Verdict)>();
        CancellationTokenSource cts;

        public RepairPage() : base("Repair", Glyph.Repair)
        {
            List.AddColumns(("Tool", 250), ("Time", 85), ("Last result", 640)); List.ShowItemToolTips = true;
            var bar = Ui.Row(DockStyle.Top, 6); bar.Controls.AddRange(new Control[] { Recommended, Run, Stop, Logs, Cbs });
            opts.Controls.Add(RestorePt);
            Ui.Stack(this, Status, List, Info, opts, bar, Progress, Ui.Spacer(6), Out);
            if (!ConsoleTool.IsAdmin) Status.Text = "Repairs need administrator rights - start CleanSweep as administrator.";

            List.SelectedIndexChanged += (s, e) =>
            {
                if (List.SelectedItems.Count == 0) { Run.Enabled = false; return; }
                Info.Text = Repair.Tool((string)List.SelectedItems[0].Tag).What; Run.Enabled = !Busy;
            };
            List.DoubleClick += async (s, e) => { if (Run.Enabled) await RunSelected(); };
            Run.Click += async (s, e) => await RunSelected();
            Recommended.Click += async (s, e) =>
            {
                if (!Msg.Confirm("Recommended repair runs, in Microsoft's suggested order:\n\n1. Repair Windows image (DISM /RestoreHealth)\n2. System File Checker (sfc /scannow)\n\nIt usually takes 20-45 minutes. You can keep using your PC. Start now?", "CleanSweep")) return;
                await Start(Repair.Recommended, "Recommended repair");
            };
            Stop.Click += (s, e) => { cts?.Cancel(); Status.Text = "Cancelling..."; };
            Logs.Click += (s, e) => { Directory.CreateDirectory(AppPaths.Logs); Shell.Open("explorer.exe", "\"" + AppPaths.Logs + "\""); };
            Cbs.Click += (s, e) =>
            {
                if (File.Exists(Repair.CbsLog)) Shell.Open("notepad.exe", "\"" + Repair.CbsLog + "\"");
                else Msg.Show("CBS.log was not found.", "CleanSweep", MessageBoxButtons.OK, MessageBoxIcon.Information);
            };
            UpdateList();
        }

        public override void OnShown() { if (!Busy) UpdateList(); }

        public void UpdateList()
        {
            string sel = List.SelectedItems.Count > 0 ? (string)List.SelectedItems[0].Tag : null;
            var res = Repair.Results();
            List.BeginUpdate(); List.Items.Clear();
            foreach (var t in Repair.Available())
            {
                var it = new ListViewItem(t.Name) { Tag = t.Id, UseItemStyleForSubItems = false };
                it.SubItems.Add(t.Time);
                var last = res.TryGetValue(t.Id, out var v) ? v : "Not run yet";
                bool warn = last.Contains("Failed") || last.Contains("could not") || last.Contains("Damage found") || last.Contains("Some files");
                // an old "damage found" that a later successful repair took care of isn't a current problem
                var fixedBy = warn ? Repair.FixedLater(t.Id, res) : null;
                if (fixedBy != null) { last += "  (fixed since: " + fixedBy + ")"; warn = false; }
                var si = it.SubItems.Add(last);
                si.ForeColor = last == "Not run yet" ? Theme.Faint : warn ? Theme.Warn : fixedBy != null ? Theme.Sub : Theme.Text;
                if (t.Id == sel) it.Selected = true;
                List.Items.Add(it);
            }
            List.EndUpdate();
        }

        public void Select(string id)
        {
            foreach (ListViewItem it in List.Items) it.Selected = (string)it.Tag == id;
            if (List.SelectedItems.Count > 0) List.SelectedItems[0].EnsureVisible();
        }

        public Task RunSelected() => List.SelectedItems.Count == 0 ? Task.CompletedTask : Start(new[] { (string)List.SelectedItems[0].Tag }, Repair.Tool((string)List.SelectedItems[0].Tag).Name);

        void SetBusy(bool b, string what = null)
        {
            Busy = b; ToolLock.Running = b ? what : null;
            Recommended.Enabled = !b; Stop.Enabled = b; List.Enabled = !b; RestorePt.Enabled = !b; Run.Enabled = !b && List.SelectedItems.Count > 0;
            var f = FindForm(); if (f != null) f.Cursor = b ? Cursors.AppStarting : Cursors.Default;
        }

        /// <summary>Restore point once per batch, only if a tool changes something.</summary>
        async Task<bool> Preflight(IEnumerable<string> ids)
        {
            if (!ConsoleTool.IsAdmin) { Msg.Show("Repairs need administrator rights. Start CleanSweep as administrator and try again.", "CleanSweep", MessageBoxButtons.OK, MessageBoxIcon.Warning); return false; }
            if (!ids.Any(id => Repair.Tool(id).Changes) || !RestorePt.Checked) return true;
            Status.Text = "Creating a restore point...";
            bool ok = await Task.Run(() => RestorePoint.Create("CleanSweep - before system repair"));
            if (ok) { Out.Line("Restore point created."); return true; }
            if (!Msg.Confirm("A restore point could not be created (" + RestorePoint.Explain().TrimEnd('.') + ").\n\nThe repair tools use Windows' own files, so this is usually fine. Continue anyway?", "CleanSweep")) { Status.Text = "Not started."; return false; }
            Out.Line("No restore point (System Restore unavailable) - continuing."); return true;
        }

        public Task Start(IEnumerable<string> ids, string label) => Running = DoStart(ids.ToList(), label);
        async Task DoStart(List<string> ids, string label)
        {
            if (Busy || !ToolLock.Check()) return;
            Out.Clear(); LastRun.Clear(); cts = new CancellationTokenSource();
            SetBusy(true, label);
            try
            {
                if (!await Preflight(ids)) return;
                foreach (var id in ids) { if (cts.IsCancellationRequested) break; LastRun.Add((id, await RunTool(id))); }
            }
            finally { SetBusy(false); UpdateList(); }
            if (LastRun.Count == 0) return;
            bool cancelled = cts.IsCancellationRequested;
            Status.Text = cancelled ? "Cancelled." : LastRun.Count > 1 ? $"{label} finished: " + string.Join("; ", LastRun.Select(r => Repair.Tool(r.Id).Name + " - " + r.V.Level)) : LastRun[0].V.Text;
            if (!cancelled && LastRun.Any(r => r.V.Text.Contains("restart"))) Msg.Show("Restart your PC to finish the repair.", "CleanSweep", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        async Task<Verdict> RunTool(string id)
        {
            var t = Repair.Tool(id); Progress.Value = 0; Verdict v;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var timer = new System.Windows.Forms.Timer { Interval = 1000 };
            timer.Tick += (s, e) => Status.Text = $"{t.Name}... {sw.Elapsed:mm\\:ss}" + (Progress.Value > 0 ? $"  ({Progress.Value}%)" : "");
            timer.Start();
            if (AutoCancelSec > 0) cts.CancelAfter(AutoCancelSec * 1000);
            try
            {
                var (exe, args) = Repair.Command(id);
                if (exe != null)
                {
                    Out.Line($">>> {t.Name}  ({exe} {args})"); string baseText = Out.Text;
                    var r = await ConsoleTool.Run(exe, args, t.Name, (txt, pct) => { Out.Live(baseText, txt); if (pct != null) Progress.Value = pct.Value; }, cts.Token);
                    v = Repair.Judge(id, r);
                }
                else
                {
                    Out.Line(">>> " + t.Name);
                    var lines = new System.Collections.Concurrent.ConcurrentQueue<string>();
                    var work = Task.Run(() => id == "wu-reset" ? Repair.WuReset(lines.Enqueue, cts.Token) : id == "wu-undo" ? Repair.WuUndo(lines.Enqueue, cts.Token) : Repair.WuPurge(lines.Enqueue, cts.Token));
                    while (!work.IsCompleted) { await Task.Delay(200); while (lines.TryDequeue(out var l)) Out.Line(l); }
                    while (lines.TryDequeue(out var l2)) Out.Line(l2);
                    v = await work;
                }
            }
            catch (Exception e) { v = new Verdict("Problem", "Could not run: " + e.Message); }
            finally { timer.Stop(); timer.Dispose(); }
            Out.Line(""); Out.Line("Result: " + v.Text); Out.Line("");
            if (v.Text != "Cancelled") Repair.SetResult(id, v.Text);
            Progress.Value = v.Text == "Cancelled" ? 0 : 100;
            return v;
        }
    }
}
