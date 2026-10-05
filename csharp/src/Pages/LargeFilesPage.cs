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
    /// Large files: shows what takes up space so you can decide. Nothing is ticked for you, files only go to the
    /// Recycle Bin (you can restore them), and files that belong to apps, games or Windows are hidden by default -
    /// those should be removed by uninstalling the app.
    /// </summary>
    public class LargeFilesPage : Page
    {
        public readonly Label Status = Ui.Title("Find the biggest files on your drives.", 12.5f);
        public readonly Label Summary = new Label { Dock = DockStyle.Top, Height = 24, ForeColor = Theme.Sub, AutoEllipsis = true, UseMnemonic = false };
        public readonly FlowLayoutPanel DriveRow = Ui.Row();
        public readonly List<CsCheckBox> Drives = new List<CsCheckBox>();
        public readonly ComboBox MinSize = Ui.Combo(90, "100 MB", "250 MB", "500 MB", "1 GB");
        public readonly CsCheckBox HideApps = new CsCheckBox("Hide app, game and Windows files", true) { Margin = new Padding(0, 6, 0, 0) };
        public readonly CsButton Search = new CsButton("Find large files", true), Stop = new CsButton("Cancel") { Enabled = false };
        public readonly CsProgress Progress = new CsProgress { Dock = DockStyle.Top };
        public readonly CsListView List = new CsListView(true) { Dock = DockStyle.Fill };
        public readonly CsButton OpenLoc = new CsButton("Open file location") { Enabled = false }, Recycle = new CsButton("Move ticked to Recycle Bin") { Enabled = false },
            Apps = new CsButton("Uninstall apps...");
        public readonly Label Ticked = new Label { AutoSize = true, ForeColor = Theme.Sub, Margin = new Padding(8, 9, 0, 0), UseMnemonic = false };

        public List<LargeFile> Found { get; private set; } = new List<LargeFile>();
        public bool Busy { get; private set; }
        public Task Running { get; private set; } = Task.CompletedTask;
        public bool Searched { get; private set; }
        CancellationTokenSource cts;
        int sortCol = 0; bool sortDesc = true;

        public LargeFilesPage() : base("Large files", Glyph.Space)
        {
            MinSize.SelectedIndex = 0; MinSize.Margin = new Padding(0, 3, 18, 0);
            var opts = Ui.Row(); opts.Controls.AddRange(new Control[] { Ui.Text("Show files of at least"), MinSize, HideApps });
            var bar = Ui.Row(DockStyle.Top, 4); bar.Controls.AddRange(new Control[] { Search, Stop });
            var help = new Label
            {
                Dock = DockStyle.Top, Height = 40, ForeColor = Theme.Sub, UseMnemonic = false,
                Text = "Nothing is ticked for you - tick only files you are sure you don't need. They go to the Recycle Bin, so you can restore them. " +
                       "Apps and games should be removed with Uninstall apps, not by deleting their files."
            };
            List.AddColumns(("Size", 100), ("Name", 250), ("Type", 120), ("Modified", 100), ("Folder", 400));
            var bottom = Ui.Row(DockStyle.Bottom, 8); bottom.WrapContents = false;
            bottom.Controls.AddRange(new Control[] { OpenLoc, Recycle, Apps, Ticked });
            Ui.Stack(this, Status, DriveRow, opts, bar, Progress, Summary, help, bottom, List);
            LoadDrives();

            Search.Click += async (s, e) => await SearchAsync();
            Stop.Click += (s, e) => { cts?.Cancel(); Status.Text = "Cancelling..."; };
            HideApps.CheckedChanged += (s, e) => ShowList();
            List.ItemChecked += (s, e) => UpdateTicked();
            List.SelectedIndexChanged += (s, e) => OpenLoc.Enabled = List.SelectedItems.Count > 0 || List.CheckedItems.Count > 0;
            List.DoubleClick += (s, e) => { if (List.SelectedItems.Count > 0) Shell.ShowInExplorer(((LargeFile)List.SelectedItems[0].Tag).Path); };
            List.ColumnClick += (s, e) => { if (sortCol == e.Column) sortDesc = !sortDesc; else { sortCol = e.Column; sortDesc = e.Column == 0 || e.Column == 3; } ShowList(); };
            OpenLoc.Click += (s, e) =>
            {
                var it = List.SelectedItems.Count > 0 ? List.SelectedItems[0] : List.CheckedItems.Count > 0 ? List.CheckedItems[0] : null;
                if (it != null) Shell.ShowInExplorer(((LargeFile)it.Tag).Path);
            };
            Recycle.Click += (s, e) => RecycleTicked();
            Apps.Click += (s, e) => Shell.Open("ms-settings:appsfeatures");
        }

        public void LoadDrives()
        {
            DriveRow.Controls.Clear(); Drives.Clear();
            DriveRow.Controls.Add(Ui.Text("Drives"));
            foreach (var d in DriveInfo.GetDrives())
            {
                string id; long free, size;
                try { if (d.DriveType != DriveType.Fixed || !d.IsReady) continue; id = d.Name.TrimEnd('\\'); free = d.TotalFreeSpace; size = d.TotalSize; } catch { continue; }
                string label = ""; try { label = d.VolumeLabel; } catch { }
                var cb = new CsCheckBox($"{id} {(label.Length > 0 ? label + " " : "")}({Fmt.Size(free)} free of {Fmt.Size(size)})", id.Equals(AppPaths.SystemDrive, StringComparison.OrdinalIgnoreCase))
                { Tag = id + "\\", Margin = new Padding(0, 6, 18, 0) };
                Drives.Add(cb); DriveRow.Controls.Add(cb);
            }
        }

        public long MinBytes => LargeFileSearch.Sizes[Math.Max(0, MinSize.SelectedIndex)];

        /// <summary>Searches the ticked drives (or the given folders - used by the self-test).</summary>
        public Task SearchAsync(IEnumerable<string> roots = null) => Running = DoSearch(roots);
        async Task DoSearch(IEnumerable<string> roots)
        {
            if (Busy) return;
            var list = (roots ?? Drives.Where(d => d.Checked).Select(d => (string)d.Tag)).ToList();
            if (list.Count == 0) { Msg.Show("Tick at least one drive to search.", "CleanSweep", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            SetBusy(true); cts = new CancellationTokenSource(); var search = new LargeFileSearch(); var sw = System.Diagnostics.Stopwatch.StartNew();
            string where = string.Join(", ", list.Select(r => r.TrimEnd('\\')));
            var timer = new System.Windows.Forms.Timer { Interval = 250 };
            int tick = 0;
            timer.Tick += (s, e) => { Status.Text = $"Looking for large files on {where}...  {Fmt.Count(search.Checked)} files checked"; Progress.Value = (tick++ * 4) % 101; };
            timer.Start();
            List<LargeFile> files = null;
            try { files = await search.Run(list, MinBytes, cts.Token); }
            catch (OperationCanceledException) { }
            finally { timer.Stop(); timer.Dispose(); SetBusy(false); Progress.Value = 0; }
            if (files == null) { Status.Text = $"Cancelled after checking {Fmt.Count(search.Checked)} files."; return; }
            Found = files; Searched = true;
            Status.Text = Found.Count == 0 ? $"No files of {MinSize.Text} or more on {where}." :
                $"Found {Fmt.Count(Found.Count, "large file")} ({Fmt.Size(Found.Sum(f => f.Size))}) on {where}  \u00B7  {Fmt.Count(search.Checked)} files checked in {sw.Elapsed.TotalSeconds:0} s";
            ShowList();
        }

        public IEnumerable<LargeFile> ShownFiles => Found.Where(f => !HideApps.Checked || !f.IsApp);

        public void ShowList()
        {
            var rows = ShownFiles;
            Func<LargeFile, object> key = sortCol == 1 ? f => f.Name : sortCol == 2 ? f => f.Kind : sortCol == 3 ? f => f.Modified : sortCol == 4 ? (Func<LargeFile, object>)(f => f.Folder) : f => f.Size;
            rows = (sortDesc ? rows.OrderByDescending(key) : rows.OrderBy(key)).Take(500).ToList();
            List.BeginUpdate(); List.Items.Clear();
            foreach (var f in rows)
            {
                var it = new ListViewItem(Fmt.Size(f.Size)) { Tag = f, UseItemStyleForSubItems = true, ForeColor = f.IsApp ? Theme.Faint : Theme.Text };
                it.SubItems.Add(f.Name); it.SubItems.Add(f.Kind); it.SubItems.Add(f.Modified.ToString("yyyy-MM-dd")); it.SubItems.Add(f.Folder);
                List.Items.Add(it);
            }
            List.EndUpdate();
            var hidden = Found.Where(f => f.IsApp).ToList();
            Summary.Text = Found.Count == 0 ? "" : LargeFileSearch.Breakdown(Found) +
                (HideApps.Checked && hidden.Count > 0 ? $"   ({Fmt.Count(hidden.Count, "app or game file")} hidden)" : "");
            UpdateTicked();
        }

        void UpdateTicked()
        {
            var t = List.CheckedItems.Cast<ListViewItem>().Select(i => (LargeFile)i.Tag).ToList();
            Recycle.Enabled = t.Count > 0 && !Busy; OpenLoc.Enabled = (t.Count > 0 || List.SelectedItems.Count > 0) && !Busy;
            Ticked.Text = t.Count == 0 ? "" : $"{Fmt.Count(t.Count, "file")} ticked, {Fmt.Size(t.Sum(f => f.Size))}";
        }

        void SetBusy(bool b)
        {
            Busy = b; Search.Enabled = !b; Stop.Enabled = b; List.Enabled = !b; MinSize.Enabled = !b; HideApps.Enabled = !b;
            foreach (var d in Drives) d.Enabled = !b;
            if (b) { Recycle.Enabled = false; OpenLoc.Enabled = false; } else UpdateTicked();
            var f = FindForm(); if (f != null) f.Cursor = b ? Cursors.AppStarting : Cursors.Default;
        }

        /// <summary>Moves the ticked files to the Recycle Bin after asking. Never deletes permanently.</summary>
        public void RecycleTicked()
        {
            var items = List.CheckedItems.Cast<ListViewItem>().ToList(); if (items.Count == 0) return;
            var files = items.Select(i => (LargeFile)i.Tag).ToList(); long sum = files.Sum(f => f.Size);
            string appNote = files.Any(f => f.IsApp) ? "\n\nSome of these belong to an app, game or Windows. Removing them can break it - uninstalling is safer." : "";
            if (!Msg.Confirm($"Move {Fmt.Count(files.Count, "file")} ({Fmt.Size(sum)}) to the Recycle Bin?\n\nYou can restore them from the Recycle Bin. The space is freed when you empty it.{appNote}")) return;
            int ok = 0; long freed = 0; var failed = new List<string>();
            foreach (var it in items)
            {
                var f = (LargeFile)it.Tag;
                try
                {
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(f.Path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                    if (!File.Exists(f.Path)) { ok++; freed += f.Size; Found.Remove(f); List.Items.Remove(it); }
                }
                catch (Exception e) { failed.Add($"{f.Name}: {CleanEngine.Describe(e, f.Path).Reason}"); }
            }
            Status.Text = $"Moved {Fmt.Count(ok, "file")} ({Fmt.Size(freed)}) to the Recycle Bin. Empty the Recycle Bin to free the space.";
            if (failed.Count > 0) Msg.Show("These could not be moved:\n\n" + string.Join("\n", failed.Take(10)), "CleanSweep", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            UpdateTicked(); Summary.Text = Found.Count == 0 ? "" : LargeFileSearch.Breakdown(Found);
        }

        public override void OnShown() { if (!Busy && !Searched) LoadDrives(); }
    }
}
