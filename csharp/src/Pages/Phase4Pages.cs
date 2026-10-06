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
    // ======================================================================== Shortcuts
    /// <summary>Broken shortcuts on the Desktop, Start menu, Quick Launch and Send To. Removed ones go to the Recycle Bin.</summary>
    public class ShortcutsPage : Page
    {
        public readonly Label Status = Ui.Title("Click Scan to find shortcuts that point to missing programs or files.", 12.5f);
        public readonly CsListView List = new CsListView(true) { Dock = DockStyle.Fill };
        public readonly CsButton Scan = new CsButton("Scan", true), Remove = new CsButton("Move ticked to Recycle Bin") { Enabled = false },
            Stop = new CsButton("Cancel") { Enabled = false }, OpenLoc = new CsButton("Open location") { Enabled = false };
        readonly Label help = Ui.Note("Looks on your Desktop, Start menu, Quick Launch and Send To menu. Shortcuts on unplugged drives, network drives and Store apps are never counted as broken. Removed shortcuts go to the Recycle Bin.");
        public List<string> Dirs = Shortcuts.DefaultDirs();
        public List<BrokenShortcut> Found { get; private set; } = new List<BrokenShortcut>();
        public bool Busy { get; private set; }
        public Task Running { get; private set; } = Task.CompletedTask;
        CancellationTokenSource cts;

        public ShortcutsPage() : base("Shortcuts", Glyph.Link)
        {
            List.AddColumns(("Shortcut", 230), ("Location", 330), ("Missing target", 380));
            var bar = Ui.Row(DockStyle.Top, 6); bar.Controls.AddRange(new Control[] { Scan, Remove, Stop, OpenLoc });
            Ui.Stack(this, Status, help, bar, Ui.Spacer(4), List);
            Scan.Click += async (s, e) => await ScanAsync();
            Stop.Click += (s, e) => cts?.Cancel();
            Remove.Click += (s, e) => RemoveTicked();
            OpenLoc.Click += (s, e) => { if (List.SelectedItems.Count > 0) Shell.ShowInExplorer(((BrokenShortcut)List.SelectedItems[0].Tag).Path); };
            List.SelectedIndexChanged += (s, e) => OpenLoc.Enabled = List.SelectedItems.Count > 0 && !Busy;
            List.ItemChecked += (s, e) => Remove.Enabled = List.CheckedItems.Count > 0 && !Busy;
        }

        public Task ScanAsync() => Running = DoScan();
        async Task DoScan()
        {
            if (Busy) return;
            Busy = true; Scan.Enabled = false; Stop.Enabled = true; Remove.Enabled = false; List.Items.Clear(); cts = new CancellationTokenSource();
            var dirs = Dirs.ToList();
            try { Found = await Task.Run(() => Shortcuts.Scan(dirs, d => BeginInvoke((Action)(() => Status.Text = "Scanning " + d + "...")), cts.Token)); }
            catch (OperationCanceledException) { Status.Text = "Scan cancelled."; Found = new List<BrokenShortcut>(); }
            finally { Busy = false; Scan.Enabled = true; Stop.Enabled = false; }
            if (cts.IsCancellationRequested) return;
            List.BeginUpdate();
            foreach (var b in Found)
            {
                var it = new ListViewItem(b.Name) { Tag = b, Checked = true }; it.SubItems.Add(b.Folder); it.SubItems.Add(b.Target); List.Items.Add(it);
            }
            List.EndUpdate();
            Status.Text = Found.Count == 0 ? "No broken shortcuts found." : $"Found {Fmt.Count(Found.Count, "broken shortcut")}.";
            Remove.Enabled = List.CheckedItems.Count > 0;
        }

        public void RemoveTicked()
        {
            var sel = List.CheckedItems.Cast<ListViewItem>().ToList(); if (sel.Count == 0) return;
            if (!Msg.Confirm($"Move {Fmt.Count(sel.Count, "broken shortcut")} to the Recycle Bin?")) return;
            int ok = 0;
            foreach (var it in sel)
            {
                try
                {
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(((BrokenShortcut)it.Tag).Path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                    List.Items.Remove(it); Found.Remove((BrokenShortcut)it.Tag); ok++;
                }
                catch { it.SubItems[2].Text = "Could not remove (it may need administrator rights or be in use)"; }
            }
            Status.Text = $"Moved {Fmt.Count(ok, "broken shortcut")} to the Recycle Bin.";
            Remove.Enabled = List.CheckedItems.Count > 0;
        }
    }

    // ======================================================================== Registry
    /// <summary>
    /// Registry leftovers only: startup entries, app paths and uninstall entries that point to programs that are gone.
    /// Entries with no visible effect are listed but never ticked. A .reg backup is always saved first, and a restore point is offered.
    /// </summary>
    public class RegistryPage : Page
    {
        public readonly Label Status = Ui.Title("Click Scan to find registry leftovers from removed programs.", 12.5f);
        public readonly CsListView List = new CsListView(true) { Dock = DockStyle.Fill, ShowItemToolTips = true };
        public readonly CsButton Scan = new CsButton("Scan", true), Clean = new CsButton("Remove ticked") { Enabled = false }, Stop = new CsButton("Cancel") { Enabled = false },
            RestoreBackup = new CsButton("Restore a backup..."), OpenBackups = new CsButton("Open backups folder");
        public readonly CsCheckBox RestorePt = new CsCheckBox("Create a System Restore point before removing (recommended)", true) { Margin = new Padding(0, 4, 0, 0) };
        readonly Label help = Ui.Note("Registry cleaning doesn't make Windows faster. This only tidies leftovers you can notice: startup entries that fail at sign-in, " +
                                      "and \"installed\" programs in Settings that can't be uninstalled because they're already gone. A backup is saved first so anything can be put back.");
        public List<RegIssue> Found { get; private set; } = new List<RegIssue>();
        public string LastBackup { get; private set; }
        public bool Busy { get; private set; }
        public Task Running { get; private set; } = Task.CompletedTask;
        /// <summary>Self-test: pick the backup file without the Open dialog.</summary>
        public string TestRestoreFile;
        CancellationTokenSource cts;

        public RegistryPage() : base("Registry", Glyph.Registry)
        {
            List.AddColumns(("Issue", 290), ("Registry location", 400), ("Missing file", 300));
            var opts = Ui.Row(DockStyle.Top, 2); opts.Controls.Add(RestorePt);
            var bar = Ui.Row(DockStyle.Top, 6); bar.Controls.AddRange(new Control[] { Scan, Clean, Stop, RestoreBackup, OpenBackups });
            Ui.Stack(this, Status, help, opts, bar, Ui.Spacer(4), List);
            Scan.Click += async (s, e) => await ScanAsync();
            Stop.Click += (s, e) => cts?.Cancel();
            Clean.Click += async (s, e) => await CleanAsync();
            List.ItemChecked += (s, e) => Clean.Enabled = List.CheckedItems.Count > 0 && !Busy;
            OpenBackups.Click += (s, e) => { Directory.CreateDirectory(RegistryScan.BackupDir); Shell.Open("explorer.exe", "\"" + RegistryScan.BackupDir + "\""); };
            RestoreBackup.Click += (s, e) => DoRestore();
        }

        void SetBusy(bool b) { Busy = b; Scan.Enabled = !b; Stop.Enabled = b; RestoreBackup.Enabled = !b; RestorePt.Enabled = !b; Clean.Enabled = !b && List.CheckedItems.Count > 0; var f = FindForm(); if (f != null) f.Cursor = b ? Cursors.AppStarting : Cursors.Default; }

        public Task ScanAsync() => Running = DoScan();
        async Task DoScan()
        {
            if (Busy) return;
            SetBusy(true); List.Items.Clear(); Status.Text = "Scanning the registry..."; cts = new CancellationTokenSource();
            try { Found = await Task.Run(() => RegistryScan.Scan(cts.Token)); }
            catch (OperationCanceledException) { Found = new List<RegIssue>(); Status.Text = "Scan cancelled."; }
            finally { SetBusy(false); }
            if (cts.IsCancellationRequested) return;
            ShowFound();
            int rec = Found.Count(f => f.Recommended);
            Status.Text = Found.Count == 0 ? "No registry leftovers found." : rec > 0 ? $"Found {Fmt.Count(Found.Count, "entry", "entries")} - {rec} worth removing (ticked)." : $"Found {Fmt.Count(Found.Count, "harmless leftover entry", "harmless leftover entries")} - nothing needs removing.";
        }
        void ShowFound()
        {
            List.BeginUpdate(); List.Items.Clear();
            foreach (var f in Found.OrderByDescending(f => f.Recommended))
            {
                var it = new ListViewItem(f.Issue) { Tag = f, Checked = f.Recommended, ForeColor = f.Recommended ? Theme.Text : Theme.Sub, UseItemStyleForSubItems = true, ToolTipText = f.Location };
                it.SubItems.Add(f.Location); it.SubItems.Add(f.Target ?? ""); List.Items.Add(it);
            }
            List.EndUpdate(); Clean.Enabled = List.CheckedItems.Count > 0;
        }

        public Task CleanAsync() => Running = DoClean();
        async Task DoClean()
        {
            var sel = List.CheckedItems.Cast<ListViewItem>().ToList(); if (sel.Count == 0 || Busy) return;
            if (!Msg.Confirm($"Remove {Fmt.Count(sel.Count, "registry entry", "registry entries")}?\n\nA backup will be saved to:\n{RegistryScan.BackupDir}")) return;
            SetBusy(true); bool rp = false;
            try
            {
                if (RestorePt.Checked)
                {
                    Status.Text = "Creating a restore point (this can take a minute)...";
                    rp = await Task.Run(() => RestorePoint.Create("CleanSweep - before registry cleaning"));
                    if (!rp && !Msg.Confirm("No restore point was created (" + RestorePoint.Explain().TrimEnd('.') + ").\n\nContinue anyway? A registry backup file will still be saved.", "Restore point"))
                    { Status.Text = "Cancelled - nothing was removed."; return; }
                }
                Status.Text = "Backing up...";
                var items = sel.Select(i => (RegIssue)i.Tag).ToList();
                try { LastBackup = await Task.Run(() => RegistryScan.Backup(items.Select(i => i.Key))); }
                catch (Exception e) { Status.Text = "Nothing was removed: " + e.Message + "."; return; }
                int ok = 0;
                foreach (var it in sel)
                {
                    var f = (RegIssue)it.Tag;
                    try { RegistryScan.Remove(f); List.Items.Remove(it); Found.Remove(f); ok++; }
                    catch (Exception e) { it.SubItems[2].Text = "Could not remove: " + (e is UnauthorizedAccessException || e is System.Security.SecurityException ? "access denied" : e.Message); }
                }
                Status.Text = $"Removed {Fmt.Count(ok, "entry", "entries")}. Backup saved.";
                Msg.Show($"Removed {Fmt.Count(ok, "registry entry", "registry entries")}.\n\nBackup file:\n{LastBackup}" + (rp ? "\n\nA System Restore point named 'CleanSweep - before registry cleaning' was created." : ""), "CleanSweep", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            finally { SetBusy(false); }
        }

        void DoRestore()
        {
            string file = TestRestoreFile;
            if (file == null)
                using (var dlg = new OpenFileDialog { Filter = "Registry backups (*.reg)|*.reg", InitialDirectory = Directory.Exists(RegistryScan.BackupDir) ? RegistryScan.BackupDir : "" })
                { if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return; file = dlg.FileName; }
            if (!Msg.Confirm($"Put back the registry entries from\n{Path.GetFileName(file)}?")) return;
            bool ok = RegistryScan.Import(file);
            Status.Text = ok ? "Backup restored." : "Some entries could not be restored.";
            Msg.Show(Status.Text, "CleanSweep", MessageBoxButtons.OK, ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }
    }

    // ======================================================================== Network
    /// <summary>
    /// Network: measure first, then optional fixes that only reset things to Windows' defaults. Ethernet diagnostics are read-only.
    /// </summary>
    public class NetworkPage : Page
    {
        public readonly Label Status = Ui.Title("Click Test connection to measure your connection.", 12.5f);
        public readonly Label Info = Ui.Note("Reading connection details...");
        public readonly ComboBox Kind = Ui.Combo(110, "Wi-Fi", "Ethernet");
        public readonly ComboBox DnsBox = Ui.Combo(200, Network.DnsChoices);
        public readonly CsListView List = new CsListView(true) { Dock = DockStyle.Fill, ShowItemToolTips = true };
        public readonly CsButton TestBtn = new CsButton("Test connection", true), Optimize = new CsButton("Apply ticked fixes"), Stop = new CsButton("Cancel") { Enabled = false },
            NearbyBtn = new CsButton("Nearby networks"), Diag = new CsButton("Ethernet diagnostics");
        readonly Label help = Ui.Note("Nothing here is ticked unless it's safe for everyone. These only clear caches and put Windows' own defaults back - CleanSweep never applies \"gaming\" TCP tweaks or registry hacks.");
        public NetTest Before, After;
        public List<string> LastApplied = new List<string>();
        public List<DiagRow> LastDiag;
        public bool Busy { get; private set; }
        public Task Running { get; private set; } = Task.CompletedTask;
        public Form OpenDialog;   // self-test
        CancellationTokenSource cts; bool loaded;

        public NetworkPage() : base("Network", Glyph.Network)
        {
            DnsBox.SelectedIndex = 0; Kind.Margin = new Padding(0, 3, 18, 0);
            var opts = Ui.Row(); opts.Controls.AddRange(new Control[] { Ui.Pair(Ui.Text("Connection"), Kind), Ui.Pair(Ui.Text("DNS server"), DnsBox) });
            List.AddColumns(("Fix", 290), ("What it does", 640));
            var bar = Ui.Row(DockStyle.Top, 6); bar.Controls.AddRange(new Control[] { TestBtn, Optimize, Stop, NearbyBtn, Diag });
            Ui.Stack(this, Status, Info, opts, bar, help, Ui.Spacer(2), List);
            Kind.SelectedIndexChanged += (s, e) => { Before = null; LoadOptions(); RefreshInfo(); NearbyBtn.Enabled = Kind.Text == "Wi-Fi"; Diag.Enabled = Kind.Text == "Ethernet"; };
            TestBtn.Click += async (s, e) => await TestAsync();
            Optimize.Click += async (s, e) => await ApplyAsync();
            Stop.Click += (s, e) => cts?.Cancel();
            NearbyBtn.Click += (s, e) => ShowNearby();
            Diag.Click += async (s, e) => await DiagnoseAsync();
        }

        public override void OnShown()
        {
            if (loaded) return; loaded = true;
            Kind.SelectedItem = Network.ActiveKind();   // start on whichever connection Windows uses for the internet
            if (Kind.SelectedIndex < 0) Kind.SelectedIndex = 0;
        }
        public void LoadOptions()
        {
            List.BeginUpdate(); List.Items.Clear();
            foreach (var o in Network.Options(Kind.Text)) { var it = new ListViewItem(o.Name) { Tag = o.Key, Checked = o.Default, ToolTipText = o.What }; it.SubItems.Add(o.What); List.Items.Add(it); }
            List.EndUpdate();
        }
        public void RefreshInfo() { Info.Text = Network.Describe(Kind.Text); }

        void SetBusy(bool b) { Busy = b; foreach (var c in new Control[] { TestBtn, Optimize, Kind, DnsBox, List }) c.Enabled = !b; NearbyBtn.Enabled = !b && Kind.Text == "Wi-Fi"; Diag.Enabled = !b && Kind.Text == "Ethernet"; Stop.Enabled = b; var f = FindForm(); if (f != null) f.Cursor = b ? Cursors.AppStarting : Cursors.Default; }

        public Task TestAsync() => Running = DoTest();
        async Task DoTest()
        {
            if (Busy) return;
            SetBusy(true); RefreshInfo(); Status.Text = $"Testing your {Kind.Text} connection (about 10 seconds)..."; cts = new CancellationTokenSource();
            try { Before = await Task.Run(() => Network.Test(cts.Token)); Status.Text = Before.ToString(); }
            catch (OperationCanceledException) { Status.Text = "Test cancelled."; }
            finally { SetBusy(false); }
        }

        public Task ApplyAsync() => Running = DoApply();
        async Task DoApply()
        {
            var keys = List.CheckedItems.Cast<ListViewItem>().Select(i => (string)i.Tag).ToList();
            if (Busy || (keys.Count == 0 && DnsBox.SelectedIndex == 0)) return;
            if (Network.Target(Kind.Text) == null) { Msg.Show($"No {Kind.Text} adapter was found.", "CleanSweep", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            if (!Msg.Confirm($"Apply the ticked {Kind.Text} fixes?\n\nYour connection may drop for a few seconds.")) return;
            SetBusy(true); cts = new CancellationTokenSource(); string kind = Kind.Text; int dns = DnsBox.SelectedIndex; bool restart = false;
            try
            {
                LastApplied = await Task.Run(() => { var r = Network.Apply(kind, keys, dns, t => BeginInvoke((Action)(() => Status.Text = t)), out bool rs); restart = rs; return r; });
                Status.Text = $"Waiting for {kind} to reconnect...";
                if (Network.DryRun == null) await Task.Run(() => Network.WaitForInternet(cts.Token));
                RefreshInfo();
                Status.Text = "Testing the connection again...";
                After = await Task.Run(() => Network.Test(cts.Token));
                Status.Text = "After: " + After;
                string msg = "Applied:\n - " + string.Join("\n - ", LastApplied) + (Before != null ? "\n\nBefore: " + Before : "") + "\nAfter:  " + After +
                             (restart ? "\n\nRestart your PC to finish the network stack reset." : "");
                Before = After;
                Msg.Show(msg, "Network", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (OperationCanceledException) { Status.Text = "Cancelled."; }
            finally { SetBusy(false); }
        }

        public void ShowNearby()
        {
            Cursor = Cursors.WaitCursor; var nets = Network.Nearby(out bool loc); Cursor = Cursors.Default;
            if (loc) { Msg.Show(Network.LocationHelp, "Nearby networks", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            var counts = nets.GroupBy(n => n.Channel).ToDictionary(g => g.Key, g => g.Count());
            var rows = nets.OrderByDescending(n => int.TryParse(System.Text.RegularExpressions.Regex.Replace(n.Signal, @"[^\d]", ""), out int v) ? v : 0)
                           .Select(n => new[] { n.Ssid, n.Signal, n.Channel.ToString(), n.Band, counts[n.Channel].ToString() }).ToList();
            var info = Network.WifiInfo(out _);
            int.TryParse(info.TryGetValue("Channel", out var c) ? c : "", out int cur);
            string tip = nets.Count == 0 ? "No networks found. Make sure Wi-Fi is turned on." :
                cur > 0 && counts.TryGetValue(cur, out int k) && k >= 4 ? $"Your channel ({cur}) is crowded with {k} networks. If your router supports 5 GHz, connect to that network, or change the router's channel in its settings." :
                cur > 0 ? $"Your channel ({cur}) is not heavily crowded." : "";
            ShowTable("Nearby Wi-Fi networks", new[] { ("Network", 240), ("Signal", 70), ("Channel", 70), ("Band", 90), ("Networks on this channel", 170) }, rows, null, tip);
        }

        public Task DiagnoseAsync() => Running = DoDiag();
        async Task DoDiag()
        {
            if (Busy) return;
            SetBusy(true); cts = new CancellationTokenSource();
            try { LastDiag = await Task.Run(() => Network.EthernetDiagnostics(t => BeginInvoke((Action)(() => Status.Text = t)), cts.Token)); }
            catch (OperationCanceledException) { Status.Text = "Diagnostics cancelled."; return; }
            finally { SetBusy(false); }
            int p = LastDiag.Count(r => r.Status == "Problem"), w = LastDiag.Count(r => r.Status == "Warning");
            Status.Text = p + w == 0 ? "Ethernet diagnostics: everything looks healthy." : $"Ethernet diagnostics: {Fmt.Count(p, "problem")}, {Fmt.Count(w, "warning")}.";
            ShowTable("Ethernet diagnostics", new[] { ("Check", 190), ("Result", 430), ("Status", 90) },
                LastDiag.Select(r => new[] { r.Area, r.Result, r.Status }).ToList(), LastDiag.Select(r => r.Tip).ToList(),
                p + w == 0 ? "Everything looks healthy." : $"Found {Fmt.Count(p, "problem")} and {Fmt.Count(w, "warning")}. Click a row to see what to do.");
        }

        void ShowTable(string title, (string, int)[] cols, List<string[]> rows, List<string> tips, string footer)
        {
            var dlg = new Form { Text = title, Size = new Size(900, 540), StartPosition = FormStartPosition.CenterParent, Icon = Program.AppIcon, ShowInTaskbar = false, MinimizeBox = false };
            var lv = new CsListView(false) { Dock = DockStyle.Fill }; lv.AddColumns(cols);
            foreach (var r in rows)
            {
                var it = new ListViewItem(r[0]) { UseItemStyleForSubItems = false }; foreach (var v in r.Skip(1)) it.SubItems.Add(v);
                if (r.Length == 3) it.SubItems[2].ForeColor = r[2] == "OK" ? Theme.Ok : r[2] == "Warning" ? Theme.Warn : r[2] == "Problem" ? Theme.Bad : Theme.Sub;
                lv.Items.Add(it);
            }
            var tip = new Label { Dock = DockStyle.Bottom, Height = 70, Padding = new Padding(12, 8, 12, 8), Text = footer, ForeColor = Theme.Text, UseMnemonic = false, BackColor = Theme.Side };
            if (tips != null) lv.SelectedIndexChanged += (s, e) => { if (lv.SelectedIndices.Count > 0) { var t = tips[lv.SelectedIndices[0]]; tip.Text = string.IsNullOrEmpty(t) ? "No action needed." : t; } };
            dlg.Controls.Add(lv); dlg.Controls.Add(tip); Theme.DarkDialog(dlg); tip.BackColor = Theme.Side;
            OpenDialog = dlg; dlg.FormClosed += (s, e) => OpenDialog = null;
            if (Msg.Test) { dlg.Show(FindForm()); Application.DoEvents(); } else { dlg.ShowDialog(FindForm()); dlg.Dispose(); }
        }
    }
}
