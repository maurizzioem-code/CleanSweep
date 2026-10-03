using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using CleanSweep.Engine;
using CleanSweep.UI;

namespace CleanSweep.Pages
{
    /// <summary>
    /// Dashboard: health score, recommendations, one-click actions and a live hardware monitor.
    /// Report-first: checks only read the system. Nothing changes unless you click an action, and anything that
    /// changes the PC still asks first on its own page.
    /// </summary>
    public class DashboardPage : Page
    {
        readonly Panel scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(0, 0, 8, 0) };
        public readonly Label Hello, Machine;
        public readonly ScoreGauge Gauge = new ScoreGauge { Dock = DockStyle.Left, Width = 170 };
        public readonly Label Grade, Trend, Status;
        public readonly CsButton Run = new CsButton("Run health check", true) { MinimumSize = new Size(150, 34), Margin = new Padding(0) };
        public readonly CsButton SaveReport = new CsButton("Save report") { MinimumSize = new Size(110, 34), Dock = DockStyle.Right, Enabled = false, Margin = new Padding(0) };
        public readonly CsButton DoAction = new CsButton("Do suggested action", true) { MinimumSize = new Size(170, 34), Dock = DockStyle.Right, Enabled = false, Margin = new Padding(0) };
        public readonly Label Advice;
        public readonly CsListView List = new CsListView { Dock = DockStyle.Fill, MultiSelect = false };
        public readonly Dictionary<string, ActionTile> Tiles = new Dictionary<string, ActionTile>();
        public readonly Dictionary<string, HwCard> Hw = new Dictionary<string, HwCard>();
        readonly TableLayoutPanel row1 = new TableLayoutPanel { Dock = DockStyle.Top, Height = 250, Margin = new Padding(0), Padding = new Padding(0) };
        readonly TableLayoutPanel grid = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(0, 4, 0, 0) };
        readonly Card health = new Card(), acts = new Card();
        bool? stacked; int tileCols;

        public List<Finding> Findings { get; private set; } = new List<Finding>();
        public bool Checking { get; private set; }
        public Task Running { get; private set; } = Task.CompletedTask;
        public HwStatic Static { get; private set; } = new HwStatic();
        HardwareMonitor monitor; readonly Timer liveTimer = new Timer { Interval = 1000 };
        HwSample last = new HwSample(); List<string> temps;

        static readonly string[] TileOrder = { "clean", "space", "repair", "optimize", "network", "restore" };

        Label L(string text, Font font, Color color, int height, DockStyle dock = DockStyle.Top) =>
            new Label { Text = text, Font = font, ForeColor = color, Dock = dock, Height = height, AutoEllipsis = true, UseMnemonic = false };
        static Panel Spacer(int h) => new Panel { Dock = DockStyle.Top, Height = h };

        public DashboardPage() : base("Dashboard", Glyph.Home)
        {
            Controls.Add(scroll);
            int hour = DateTime.Now.Hour;
            Hello = L(hour < 12 ? "Good morning" : hour < 18 ? "Good afternoon" : "Good evening", Theme.DisplayFont(20), Theme.Text, 38);
            Machine = L(Environment.MachineName, Theme.UiFont(9.5f), Theme.Sub, 22);
            var hdr = new Panel { Dock = DockStyle.Top, Height = 64 }; Ui.Stack(hdr, Hello, Machine);

            // ---- health score card
            Grade = L("Not checked yet", Theme.DisplayFont(16), Theme.Text, 34);
            Trend = L("", Theme.UiFont(9.5f), Theme.Sub, 40); Trend.AutoEllipsis = false;
            Status = L("Run a health check to see your score.", Theme.UiFont(9.5f), Theme.Sub, 62); Status.AutoEllipsis = false;
            var btns = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 44, WrapContents = false }; btns.Controls.Add(Run);
            var info = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 18, 0, 0) };
            Ui.Stack(info, Grade, Trend, Status, btns);
            health.Controls.Add(info); health.Controls.Add(Gauge);

            // ---- one-click actions
            void Tile(string key, string glyph, string title, string sub) { var t = new ActionTile(key, glyph, title, sub); t.Click += async (s, e) => await QuickAction(key); Tiles[key] = t; }
            string sys = AppPaths.SystemDrive;
            Tile("clean", UI.Glyph.Clean, "Quick clean", "Scan and remove junk on " + sys);
            Tile("space", UI.Glyph.Space, "Free up space", "Find large files on " + sys);
            Tile("repair", UI.Glyph.Repair, "Repair Windows", "DISM + System File Checker");
            Tile("optimize", UI.Glyph.Speed, "Optimize drives", "TRIM SSDs, defragment HDDs");
            Tile("network", UI.Glyph.Net, "Network check", "Test speed, latency and DNS");
            Tile("restore", UI.Glyph.Restore, "Restore point", "Create a safety snapshot");
            Ui.Stack(acts, L("One-click actions", Theme.UiFont(11, FontStyle.Bold), Theme.Text, 28), grid);
            acts.Resize += (s, e) => UpdateTileLayout();
            row1.Resize += (s, e) => UpdateRow1();
            UpdateRow1(); UpdateTileLayout();

            // ---- hardware monitor
            var row2 = new TableLayoutPanel { Dock = DockStyle.Top, Height = 356, ColumnCount = 3, RowCount = 2 };
            for (int i = 0; i < 3; i++) row2.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
            for (int i = 0; i < 2; i++) row2.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            void AddHw(string key, string glyph, string title, string color, double max, int col, int row)
            { var c = new HwCard(title, glyph, ColorTranslator.FromHtml(color), max); Hw[key] = c; row2.Controls.Add(c, col, row); }
            AddHw("cpu", UI.Glyph.Cpu, "Processor", "#60CDFF", 100, 0, 0);
            AddHw("ram", UI.Glyph.Memory, "Memory", "#C3A6FF", 100, 1, 0);
            AddHw("gpu", UI.Glyph.Gpu, "Graphics", "#FF9EC4", 100, 2, 0);
            AddHw("disk", UI.Glyph.Disk, "Disk", "#FFC66D", 100, 0, 1);
            AddHw("net", UI.Glyph.Net, "Network", "#6CCB5F", 0, 1, 1);
            AddHw("bat", UI.Glyph.Battery, "Battery and temperatures", "#9FE6A0", 100, 2, 1);

            // ---- recommendations
            List.AddColumns(("Status", 84), ("Area", 116), ("Finding", 440), ("Suggested action", 180));
            // Finding column takes the spare width so there is no sideways scrolling
            List.ClientSizeChanged += (s, e) => { int w = List.ClientSize.Width - 84 - 116 - 180 - 2; if (w > 120 && List.Columns.Count == 4) List.Columns[2].Width = w; };
            var rec = new Card { Dock = DockStyle.Top, Height = 340, Padding = new Padding(4, 4, 4, 10), Margin = new Padding(0) };
            var recBottom = new Panel { Dock = DockStyle.Bottom, Height = 50, Padding = new Padding(12, 6, 12, 0) };
            Advice = L("Select a finding to see what to do.", Theme.UiFont(9.5f), Theme.Sub, 0, DockStyle.Fill); Advice.AutoEllipsis = false;
            recBottom.Controls.Add(Advice); recBottom.Controls.Add(DoAction); recBottom.Controls.Add(new Panel { Dock = DockStyle.Right, Width = 8 }); recBottom.Controls.Add(SaveReport);
            rec.Controls.Add(List); rec.Controls.Add(recBottom);

            Ui.Stack(scroll, hdr, row1, Spacer(8), L("Hardware monitor", Theme.UiFont(11, FontStyle.Bold), Theme.Text, 30), row2, Spacer(8),
                     L("Recommendations", Theme.UiFont(11, FontStyle.Bold), Theme.Text, 30), rec, Spacer(12));

            Run.Click += async (s, e) => await RunCheckAsync();
            List.SelectedIndexChanged += (s, e) => ShowSelected();
            DoAction.Click += (s, e) => { if (List.SelectedItems.Count > 0) RunAction((Finding)List.SelectedItems[0].Tag); };
            List.DoubleClick += (s, e) => { if (List.SelectedItems.Count > 0) RunAction((Finding)List.SelectedItems[0].Tag); };
            SaveReport.Click += (s, e) => AskSaveReport();
            ShowHistory();
            HandleCreated += (s, e) => StartLive();
        }

        // ---------------------------------------------------------------- responsive layout
        // Wide windows: score and actions side by side. Narrow (laptop) windows: stacked, so tiles keep readable titles.
        void UpdateRow1()
        {
            bool stack = row1.Width < 980; if (stack == stacked) return; stacked = stack;
            row1.SuspendLayout(); row1.Controls.Clear(); row1.ColumnStyles.Clear(); row1.RowStyles.Clear();
            if (stack)
            {
                row1.ColumnCount = 1; row1.RowCount = 2; row1.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                row1.RowStyles.Add(new RowStyle(SizeType.Absolute, 236)); row1.RowStyles.Add(new RowStyle(SizeType.Absolute, 250));
                row1.Controls.Add(health, 0, 0); row1.Controls.Add(acts, 0, 1); row1.Height = 486;
            }
            else
            {
                row1.ColumnCount = 2; row1.RowCount = 1;
                row1.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 380)); row1.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                row1.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                row1.Controls.Add(health, 0, 0); row1.Controls.Add(acts, 1, 0); row1.Height = 250;
            }
            health.Dock = acts.Dock = DockStyle.Fill;
            row1.ResumeLayout();
        }
        void UpdateTileLayout()
        {
            int cols = acts.Width < 560 ? 2 : 3; if (cols == tileCols) return; tileCols = cols; int rows = (6 + cols - 1) / cols;
            grid.SuspendLayout(); grid.Controls.Clear(); grid.ColumnStyles.Clear(); grid.RowStyles.Clear(); grid.ColumnCount = cols; grid.RowCount = rows;
            for (int i = 0; i < cols; i++) grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / cols));
            for (int i = 0; i < rows; i++) grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100f / rows));
            for (int i = 0; i < 6; i++) grid.Controls.Add(Tiles[TileOrder[i]], i % cols, i / cols);
            grid.ResumeLayout();
        }

        // ---------------------------------------------------------------- health check
        public Task RunCheckAsync() => Running = DoCheck();
        async Task DoCheck()
        {
            if (Checking) return;
            Checking = true; Run.Enabled = false; DoAction.Enabled = false; var f = FindForm(); if (f != null) f.Cursor = Cursors.AppStarting;
            var rows = new List<Finding>();
            foreach (var (name, check) in Health.Checks)
            {
                Status.Text = $"Checking {name.ToLower()}...";
                rows.AddRange(await Task.Run(() => Health.RunCheck(name, check)));
            }
            Findings = rows;
            int score = Health.Score(rows); ShowScore(score); ShowFindings(rows);
            await Task.Run(() => Health.SaveHistory(score, rows)); ShowHistory();
            int p = rows.Count(r => r.Status == Engine.Status.Problem), w = rows.Count(r => r.Status == Engine.Status.Warning);
            Status.Text = p + w > 0 ? $"{p} problem(s) and {w} recommendation(s) - see Recommendations below." : "Everything looks good. Nothing needs doing.";
            Run.Enabled = true; SaveReport.Enabled = true; Checking = false; if (f != null) f.Cursor = Cursors.Default;
        }

        void ShowScore(int s) { Gauge.Score = s; Grade.Text = Health.Grade(s); Grade.ForeColor = ScoreGauge.GradeColor(s); }

        static Color StatusColor(Engine.Status s) => s == Engine.Status.Problem ? Theme.Bad : s == Engine.Status.Warning ? Theme.Warn : s == Engine.Status.OK ? Theme.Ok : Theme.Info;
        readonly Font bold = Theme.UiFont(10, FontStyle.Bold);
        void ShowFindings(List<Finding> rows)
        {
            List.BeginUpdate(); List.Items.Clear();
            foreach (var r in rows.OrderBy(r => r.Status).ThenBy(r => r.Area))
            {
                var (_, text) = Resolve(r);
                var it = new ListViewItem(r.Status.ToString()) { UseItemStyleForSubItems = false, ForeColor = StatusColor(r.Status), Font = bold, Tag = r };
                it.SubItems.Add(r.Area).ForeColor = Theme.Text; it.SubItems.Add(r.Text).ForeColor = Theme.Text;
                it.SubItems.Add(text).ForeColor = Theme.Accent;
                foreach (ListViewItem.ListViewSubItem si in it.SubItems) if (si != it.SubItems[0]) si.Font = Theme.Body;
                List.Items.Add(it);
            }
            List.EndUpdate();
        }
        void ShowSelected()
        {
            if (List.SelectedItems.Count == 0) { DoAction.Enabled = false; return; }
            var r = (Finding)List.SelectedItems[0].Tag; var (action, text) = Resolve(r);
            Advice.Text = r.Advice.Length > 0 ? r.Advice : r.Status == Engine.Status.OK ? "No action needed." : "";
            DoAction.Text = text.Length > 0 ? text : "Do suggested action"; DoAction.Enabled = action.Length > 0;
        }

        public void ShowHistory()
        {
            var h = Health.LoadHistory();
            if (h.Count >= 2)
            {
                int diff = h[h.Count - 1].Score - h[h.Count - 2].Score;
                Trend.Text = $"{(diff > 0 ? "Up " + diff : diff < 0 ? "Down " + (-diff) : "No change")} since {h[h.Count - 2].Date:MMM d}  \u00B7  {h.Count} checks recorded";
            }
            else if (h.Count == 1) Trend.Text = "First check - history starts now";
        }

        // ---------------------------------------------------------------- actions (only navigate or open Windows' own tools)
        /// <summary>The action to use: a CleanSweep page if this edition has it, otherwise the Windows equivalent.</summary>
        public (string Action, string Text) Resolve(Finding r)
        {
            if (r.Action.StartsWith("page:") && Program.Form?.Pages.Any(p => p.Title == r.Action.Substring(5)) != true && r.FallbackAction.Length > 0)
                return (r.FallbackAction, r.FallbackText);
            return (r.Action, r.ActionText);
        }
        public static readonly List<string> Opened = new List<string>();   // what actions opened (self-test)

        public void RunAction(Finding r) => InvokeAction(Resolve(r).Action);
        public void InvokeAction(string action)
        {
            if (string.IsNullOrEmpty(action)) return;
            Opened.Add(action);
            if (action.StartsWith("page:"))
            {
                var p = Program.Form?.Pages.FirstOrDefault(x => x.Title == action.Substring(5));
                if (p != null) Program.Form.ShowPage(p);
                else Msg.Show("That part of CleanSweep comes to the C# edition in a later phase. For now, use it in the main CleanSweep app.", "CleanSweep", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else if (action.StartsWith("settings:")) Shell.Open(action.Substring(9));
            else if (action.StartsWith("run:")) { var parts = action.Substring(4).Split(new[] { ' ' }, 2); Shell.Open(parts[0], parts.Length > 1 ? parts[1] : null); }
            else if (action == "battery") BatteryReport();
        }
        void BatteryReport()
        {
            string f = Path.Combine(Path.GetTempPath(), "CleanSweep battery report.html");
            try
            {
                var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("powercfg.exe", $"/batteryreport /output \"{f}\"") { CreateNoWindow = true, UseShellExecute = false });
                p.WaitForExit(30000);
            }
            catch { }
            if (File.Exists(f)) Shell.Open(f); else Msg.Show("Windows could not create a battery report.", "CleanSweep", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        public string ComputerLine => $"{Static.Computer} ({Static.Model}, {Static.Os})";
        public void WriteReport(string path) => File.WriteAllText(path, Health.Report(Findings, ComputerLine), new System.Text.UTF8Encoding(true));
        void AskSaveReport()
        {
            using (var sd = new SaveFileDialog { Filter = "Text file (*.txt)|*.txt", FileName = "CleanSweep health report.txt" })
                if (sd.ShowDialog(FindForm()) == DialogResult.OK) WriteReport(sd.FileName);
        }

        // ---------------------------------------------------------------- one-click actions
        /// <summary>Each action shows what it does on the matching page; anything that changes the PC still asks first.</summary>
        public Task QuickAction(string key) => Running = DoQuick(key);
        async Task DoQuick(string key)
        {
            var tile = Tiles[key];
            if (!tile.Available)
            {
                Msg.Show($"\"{tile.Title}\" comes to the C# edition with its page in a later phase. For now, use it in the main CleanSweep app.", "CleanSweep", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            switch (key)
            {
                case "clean":
                    var cl = Program.Form.Page<CleanupPage>(); if (cl == null || cl.Busy) return;
                    Program.Form.ShowPage(cl); cl.TickDefaults();
                    await cl.ScanAsync(); if (cl.Clean.Enabled) await cl.CleanAsync();
                    break;
                case "restore":
                    tile.Sub = "Creating restore point..."; var f = FindForm(); if (f != null) f.Cursor = Cursors.WaitCursor;
                    bool ok = await Task.Run(() => RestorePoint.Create("CleanSweep - manual restore point"));
                    if (f != null) f.Cursor = Cursors.Default;
                    tile.Sub = ok ? "Created " + DateTime.Now.ToString("MMM d, h:mm tt") : "Not available (System Restore is off)";
                    if (!ok) Msg.Show("A restore point could not be created. System Restore may be turned off - turn it on in Control Panel > System > System Protection.", "CleanSweep", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    break;
            }
        }
        /// <summary>Marks tiles whose page isn't in this edition yet.</summary>
        public void UpdateTileAvailability()
        {
            var have = new HashSet<string>(Program.Form?.Pages.Select(p => p.Title) ?? Enumerable.Empty<string>());
            var needs = new Dictionary<string, string> { ["clean"] = "Cleanup", ["space"] = "Drives", ["repair"] = "Repair", ["optimize"] = "Drives", ["network"] = "Network Optimizer" };
            foreach (var t in Tiles.Values)
            {
                bool ok = !needs.TryGetValue(t.Key, out var page) || have.Contains(page);
                t.Available = ok; t.Sub = ok ? t.DefaultSub : "Coming in a later phase";
            }
        }

        // ---------------------------------------------------------------- live hardware monitor
        void StartLive()
        {
            if (monitor != null) return;
            Task.Run(() => HwStatic.Read()).ContinueWith(t =>
            {
                if (IsDisposed) return; Static = t.Result;
                Machine.Text = string.Join("  \u00B7  ", new[] { Static.Computer, Static.Model, Static.Os }.Where(x => x.Length > 0));
                Apply(last);
            }, TaskScheduler.FromCurrentSynchronizationContext());
            monitor = new HardwareMonitor();
            monitor.Sampled += s => { try { if (!IsDisposed && IsHandleCreated) BeginInvoke(new Action(() => Apply(s))); } catch { } };
            monitor.Start();
            liveTimer.Tick += (s, e) => { var f = FindForm(); monitor.Active = Visible && f != null && f.WindowState != FormWindowState.Minimized; };
            liveTimer.Start();
            Disposed += (s, e) => { monitor.Dispose(); liveTimer.Stop(); };
        }
        public int Samples { get; private set; }

        void Apply(HwSample d)
        {
            last = d; if (d.Temps != null) temps = d.Temps; Samples++;
            string dot = "  \u00B7  "; string sys = AppPaths.SystemDrive;
            if (d.Cpu != null)
            {
                var c = Hw["cpu"]; c.Value = d.Cpu + "%";
                string ghz = d.CpuPerf > 0 && Static.MaxMhz > 0 ? $"{dot}{Static.MaxMhz * d.CpuPerf.Value / 100.0 / 1000:N2} GHz" : "";
                c.Sub = $"{Static.Cpu}\n{Static.Cores}{ghz}"; c.Push(d.Cpu.Value);
            }
            if (d.RamTotal > 0)
            {
                double used = d.RamTotal - d.RamFree; int pct = (int)(used / d.RamTotal * 100); var c = Hw["ram"];
                c.Value = pct + "%"; c.Sub = $"{Fmt.Size((long)used)} of {Fmt.Size((long)d.RamTotal)} in use\nCommitted {Fmt.Size((long)d.Commit)}" + (Static.RamInfo.Length > 0 ? dot + Static.RamInfo : ""); c.Push(pct);
            }
            { var c = Hw["gpu"]; if (d.Gpu != null) { c.Value = d.Gpu + "%"; c.Push(d.Gpu.Value); } else c.Value = "n/a"; c.Sub = $"{Static.Gpu}\n{(d.GpuMem > 0 ? "Video memory " + Fmt.Size((long)d.GpuMem.Value) + dot : "")}{Static.GpuDriver}"; }
            if (d.DiskBusy != null)
            {
                var c = Hw["disk"]; c.Value = d.DiskBusy + "%";
                c.Sub = $"Read {Fmt.Size((long)d.DiskRead)}/s{dot}Write {Fmt.Size((long)d.DiskWrite)}/s\n{sys} {Fmt.Size((long)d.SysFree)} free of {Fmt.Size((long)d.SysSize)}"; c.Push(d.DiskBusy.Value);
            }
            if (d.NetDown != null)
            {
                var c = Hw["net"]; double down = d.NetDown.Value * 8 / (1 << 20), up = (d.NetUp ?? 0) * 8 / (1 << 20);
                c.Value = $"{down:N1} Mbps"; c.Sub = $"Upload {up:N1} Mbps\nAll network adapters"; c.Push(down);
            }
            string tempTxt = temps != null && temps.Count > 0 ? string.Join(dot, temps.Take(3)) : "No temperature sensors reported";
            var b = Hw["bat"];
            if (d.Battery != null)
            {
                b.Title = "Battery and temperatures"; b.Value = d.Battery + "%";
                string state = d.OnAC ? "Plugged in" : d.BatteryMinutes != null ? $"{d.BatteryMinutes / 60}h {d.BatteryMinutes % 60:00}m left" : "On battery";
                b.Sub = state + "\n" + tempTxt; b.Push(d.Battery.Value);
            }
            else
            {
                b.Title = "System and temperatures";
                if (Static.Boot != null) { var up = DateTime.Now - Static.Boot.Value; b.Value = $"{up.Days}d {up.Hours}h"; }
                b.Sub = "Uptime (desktop - no battery)\n" + tempTxt;
            }
        }

        public override void OnShown()
        {
            UpdateTileAvailability();
            // Windows scrolls a panel to whichever control has focus; start at the top with the Run button focused
            Run.Focus(); ScrollToTop();
            if (IsHandleCreated) BeginInvoke(new Action(ScrollToTop));
        }
        public void ScrollTo(Control c) { var p = scroll.PointToClient(c.PointToScreen(Point.Empty)); scroll.AutoScrollPosition = new Point(0, p.Y - scroll.AutoScrollPosition.Y - 40); }
        public void ScrollToTop() => scroll.AutoScrollPosition = new Point(0, 0);
        public Control RecommendationsCard => List.Parent;
        public bool Stacked => stacked == true;
        public int TileColumns => tileCols;
    }
}
