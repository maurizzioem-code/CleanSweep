using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using CleanSweep.Engine;

namespace CleanSweep.UI
{
    /// <summary>Automatic cleanup settings: when, what, and conditions. Saving creates/updates the scheduled task; turning it off deletes it.</summary>
    public class AutoCleanForm : Form
    {
        public readonly CsCheckBox On = new CsCheckBox("Clean junk files automatically") { Font = Theme.UiFont(10, FontStyle.Bold), Margin = new Padding(0, 10, 0, 6) };
        public readonly ComboBox Freq = Ui.Combo(130, AutoCleanConfig.Freqs.Select(f => f.Text).ToArray());
        public readonly ComboBox Day = Ui.Combo(120, AutoCleanConfig.Days);
        public readonly ComboBox Time = Ui.Combo(110, Enumerable.Range(0, 48).Select(i => AutoCleanConfig.TimeText(i * 30)).ToArray());
        public readonly List<CsCheckBox> Cats = new List<CsCheckBox>();
        public readonly CsCheckBox RecycleOn = new CsCheckBox("Recycle Bin items deleted more than") { Margin = new Padding(0, 4, 6, 0) };
        public readonly ComboBox RecycleDays = Ui.Combo(64, "7", "14", "30", "60", "90");
        public readonly CsCheckBox ACOnly = new CsCheckBox("Only when plugged in (laptops)"), Idle = new CsCheckBox("Only when the PC has been idle for 10 minutes"),
            CatchUp = new CsCheckBox("If the PC was off at that time, run as soon as possible"), Notify = new CsCheckBox("Show a notification with the result");
        public readonly Label Tip = new Label { AutoSize = true, ForeColor = Theme.Warn, MaximumSize = new Size(520, 0), Margin = new Padding(0, 8, 0, 4), UseMnemonic = false };
        public readonly CsButton Save = new CsButton("Save", true) { MinimumSize = new Size(110, 34), Margin = new Padding(8, 0, 0, 0) };
        public readonly CsButton Cancel = new CsButton("Cancel") { MinimumSize = new Size(100, 34), Margin = new Padding(8, 0, 0, 0), DialogResult = DialogResult.Cancel };
        readonly Label onL = new Label { Text = "on", AutoSize = true, Margin = new Padding(0, 6, 6, 0) }, atL = new Label { Text = "at", AutoSize = true, Margin = new Padding(0, 6, 6, 0) };
        readonly FlowLayoutPanel when = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(22, 0, 0, 4) };
        readonly FlowLayoutPanel rb = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(22, 2, 0, 2) };
        public bool Saved { get; private set; }

        static Label L(string t, Font f, Color c, int top = 0) => new Label { Text = t, Font = f, ForeColor = c, AutoSize = true, MaximumSize = new Size(520, 0), Margin = new Padding(0, top, 0, 4), UseMnemonic = false };

        public AutoCleanForm(Icon icon)
        {
            Text = "Automatic cleanup"; Size = new Size(600, 700); MinimumSize = new Size(560, 560); StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false; Icon = icon;
            var cfg = AutoCleanConfig.Load(); var st = AutoClean.State();
            var body = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(20, 16, 20, 8) };
            body.Controls.Add(L("Automatic cleanup", Theme.DisplayFont(15), Theme.Text));
            body.Controls.Add(L("CleanSweep removes the junk you tick below on a schedule, even when the app is closed. Files changed in the last 24 hours, files in your ignore list and files in use are always skipped. Nothing else on your PC is changed.", Theme.UiFont(9.5f), Theme.Sub));
            On.Checked = st.Exists || !AutoCleanConfig.Saved; body.Controls.Add(On);

            Freq.SelectedIndex = Math.Max(0, Array.FindIndex(AutoCleanConfig.Freqs, f => f.Key == cfg.Freq));
            Day.SelectedIndex = Math.Max(0, Array.IndexOf(AutoCleanConfig.Days, cfg.Day));
            Time.SelectedIndex = Math.Min(47, cfg.Minutes / 30);
            foreach (var c in new[] { Freq, Day, Time }) c.Margin = new Padding(0, 0, 10, 0);
            when.Controls.AddRange(new Control[] { Freq, onL, Day, atL, Time }); body.Controls.Add(when);
            Freq.SelectedIndexChanged += (s, e) => SyncDay(); SyncDay();

            body.Controls.Add(L("What to clean", Theme.UiFont(10.5f, FontStyle.Bold), Theme.Text, 12));
            foreach (var t in CleanEngine.CleanupRows().Where(r => r.Kind != TargetKind.Recycle))
            {
                var cb = new CsCheckBox(t.Note.Length > 0 && t.Kind == TargetKind.Paths ? $"{t.Name}  ({t.Note})" : t.Name, cfg.Cats.Contains(t.Name, StringComparer.OrdinalIgnoreCase))
                { Tag = t.Name, Margin = new Padding(22, 2, 0, 2) };
                Cats.Add(cb); body.Controls.Add(cb);
            }
            RecycleOn.Checked = cfg.RecycleDays > 0;
            RecycleDays.SelectedItem = (cfg.RecycleDays > 0 ? cfg.RecycleDays : 30).ToString(); if (RecycleDays.SelectedIndex < 0) RecycleDays.SelectedIndex = 2;
            RecycleDays.Margin = new Padding(0, 0, 0, 0);
            rb.Controls.AddRange(new Control[] { RecycleOn, RecycleDays, new Label { Text = "days ago", AutoSize = true, Margin = new Padding(6, 6, 0, 0) } }); body.Controls.Add(rb);

            body.Controls.Add(L("When to run", Theme.UiFont(10.5f, FontStyle.Bold), Theme.Text, 12));
            ACOnly.Checked = cfg.ACOnly; Idle.Checked = cfg.Idle; CatchUp.Checked = cfg.CatchUp; Notify.Checked = cfg.Notify;
            foreach (var c in new[] { ACOnly, Idle, CatchUp, Notify }) { c.Margin = new Padding(22, 2, 0, 2); body.Controls.Add(c); }
            if (st.Exists && st.RunsPowerShellEdition) Tip.Text = "The schedule is currently run by the main CleanSweep app. Saving here moves it to this C# edition.";
            body.Controls.Add(Tip);

            var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 58, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(16, 10, 16, 10), BackColor = Theme.Side };
            bar.Controls.AddRange(new Control[] { Save, Cancel });
            Controls.Add(body); Controls.Add(bar);
            CancelButton = Cancel;
            On.CheckedChanged += (s, e) => Toggle(); Toggle();
            Save.Click += (s, e) => DoSave();
            Theme.DarkDialog(this); Tip.ForeColor = Theme.Warn;
            foreach (var l in new[] { onL, atL }) l.ForeColor = Theme.Sub;
        }

        void SyncDay() { bool d = Freq.SelectedIndex != 0; Day.Visible = d; onL.Visible = d; }
        void Toggle() { foreach (Control c in new Control[] { when, rb, ACOnly, Idle, CatchUp, Notify }.Concat(Cats)) c.Enabled = On.Checked; }

        public void DoSave()
        {
            var cfg = AutoCleanConfig.Load();
            cfg.Freq = AutoCleanConfig.Freqs[Freq.SelectedIndex].Key; cfg.Day = (string)Day.SelectedItem; cfg.Minutes = Time.SelectedIndex * 30;
            // keep saved categories that don't exist on this PC (e.g. a browser that isn't installed), replace the rest
            var shown = new HashSet<string>(Cats.Select(c => (string)c.Tag), StringComparer.OrdinalIgnoreCase);
            cfg.Cats = cfg.Cats.Where(c => !shown.Contains(c)).Concat(Cats.Where(c => c.Checked).Select(c => (string)c.Tag)).ToList();
            cfg.RecycleDays = RecycleOn.Checked ? int.Parse((string)RecycleDays.SelectedItem) : 0;
            cfg.ACOnly = ACOnly.Checked; cfg.Idle = Idle.Checked; cfg.CatchUp = CatchUp.Checked; cfg.Notify = Notify.Checked;
            try
            {
                if (On.Checked)
                {
                    if (!Cats.Any(c => c.Checked) && cfg.RecycleDays == 0) { Tip.Text = "Tick at least one thing to clean."; return; }
                    AutoClean.Schedule(cfg);
                }
                else { cfg.Save(); AutoClean.Unschedule(); }
            }
            catch (Exception e) { Tip.Text = "Windows could not save the schedule: " + e.Message; return; }
            Saved = true; DialogResult = DialogResult.OK; Close();
        }
    }
}
