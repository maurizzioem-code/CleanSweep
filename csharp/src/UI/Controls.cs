using System;
using System.Drawing;
using System.Windows.Forms;

namespace CleanSweep.UI
{
    /// <summary>Flat, rounded Windows 11 button. Primary buttons use the accent colour.</summary>
    public class CsButton : Button
    {
        bool primary;
        public bool Primary { get => primary; set { primary = value; Recolor(); } }

        public CsButton(string text, bool primary = false)
        {
            Text = text; FlatStyle = FlatStyle.Flat; UseVisualStyleBackColor = false; Cursor = Cursors.Hand;
            Font = Theme.Body; AutoSize = true; MinimumSize = new Size(110, 36); Margin = new Padding(0, 0, 8, 6);
            UseMnemonic = false; this.primary = primary; Recolor();
            Theme.Round(this, 4);
        }
        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Recolor(); }

        void Recolor()
        {
            if (primary && Enabled)
            {
                BackColor = Theme.Accent; ForeColor = Theme.OnAccent; FlatAppearance.BorderColor = Theme.Accent;
                FlatAppearance.MouseOverBackColor = Theme.AccentHover; FlatAppearance.MouseDownBackColor = Theme.AccentDown;
            }
            else
            {
                BackColor = Theme.Button; ForeColor = Enabled ? Theme.Text : Theme.Faint; FlatAppearance.BorderColor = Theme.Border;
                FlatAppearance.MouseOverBackColor = Theme.ButtonHover; FlatAppearance.MouseDownBackColor = Theme.ButtonDown;
            }
        }

        // The standard disabled text is drawn etched/grey on light; draw our own so it stays readable on dark
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (!Enabled)
            {
                using (var b = new SolidBrush(BackColor)) e.Graphics.FillRectangle(b, 1, 1, Width - 2, Height - 2);
                TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, Theme.Faint,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            }
        }
    }

    /// <summary>Check box with a dark Windows 11 style box.</summary>
    public class CsCheckBox : CheckBox
    {
        public CsCheckBox(string text, bool on = false)
        {
            Text = text; Checked = on; AutoSize = true; Cursor = Cursors.Hand; ForeColor = Theme.Text; Font = Theme.Body;
            UseMnemonic = false; SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        }
        public override Size GetPreferredSize(Size proposed)
        {
            var t = TextRenderer.MeasureText(Text, Font, new Size(MaximumSize.Width > 0 ? MaximumSize.Width - 24 : int.MaxValue, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
            return new Size(t.Width + 26, Math.Max(22, t.Height + 4));
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.Clear(Parent?.BackColor ?? Theme.Bg);
            int y = (Height - 16) / 2;
            Theme.DrawCheck(g, new Rectangle(0, y, 15, 15), Checked, Enabled);
            TextRenderer.DrawText(g, Text, Font, new Rectangle(24, 0, Width - 24, Height), Enabled ? ForeColor : Theme.Faint,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
            if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(g, new Rectangle(22, 1, Width - 23, Height - 2), Theme.Sub, Theme.Bg);
        }
        protected override void OnCheckedChanged(EventArgs e) { base.OnCheckedChanged(e); Invalidate(); }
        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }
    }

    /// <summary>Details list with dark rows, headers and check boxes.</summary>
    public class CsListView : ListView
    {
        public CsListView(bool checkBoxes = false)
        {
            View = View.Details; FullRowSelect = true; HideSelection = false; CheckBoxes = checkBoxes;
            BackColor = Theme.Card; ForeColor = Theme.Text; BorderStyle = BorderStyle.None; OwnerDraw = true; Font = Theme.Body;
            DoubleBuffered = true;
            Native.OnHandle(this, h =>
            {
                Native.DarkWindow(h, "DarkMode_Explorer");
                var hdr = Native.SendMessage(h, 0x101F, IntPtr.Zero, IntPtr.Zero);   // LVM_GETHEADER
                if (hdr != IntPtr.Zero) Native.DarkWindow(hdr, "DarkMode_ItemsView");
            });
        }
        public void AddColumns(params (string Text, int Width)[] cols) { foreach (var c in cols) Columns.Add(c.Text, c.Width); }

        protected override void OnDrawColumnHeader(DrawListViewColumnHeaderEventArgs e)
        {
            using (var b = new SolidBrush(Theme.Bg)) e.Graphics.FillRectangle(b, e.Bounds);
            var r = e.Bounds; r.X += 6; r.Width -= 6;
            TextRenderer.DrawText(e.Graphics, e.Header.Text, Theme.Header, r, Theme.Sub, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
        }
        protected override void OnDrawItem(DrawListViewItemEventArgs e) { }   // rows are painted cell by cell
        protected override void OnDrawSubItem(DrawListViewSubItemEventArgs e)
        {
            var g = e.Graphics; var item = e.Item;
            using (var b = new SolidBrush(item.Selected ? Theme.Select : BackColor)) g.FillRectangle(b, e.Bounds);
            var r = e.Bounds; int x = r.X + 6;
            if (e.ColumnIndex == 0 && CheckBoxes)
            {
                Theme.DrawCheck(g, new Rectangle(r.X + 4, r.Y + (r.Height - 15) / 2, 15, 15), item.Checked, Enabled);
                x = r.X + 26;
            }
            bool own = e.ColumnIndex == 0 || item.UseItemStyleForSubItems;
            var col = own ? item.ForeColor : e.SubItem.ForeColor;
            if (col.IsEmpty || col == SystemColors.WindowText) col = Theme.Text;
            if (!Enabled) col = Theme.Faint;
            var tr = new Rectangle(x, r.Y, Math.Max(0, r.Right - x - 4), r.Height);
            TextRenderer.DrawText(g, e.SubItem.Text, own ? item.Font : e.SubItem.Font, tr, col,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
        protected override void OnItemChecked(ItemCheckedEventArgs e) { base.OnItemChecked(e); Invalidate(e.Item.Bounds); }
        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }
    }

    /// <summary>Flat dark progress bar (the Windows one can't be recoloured).</summary>
    public class CsProgress : Control
    {
        int v, max = 100;
        public CsProgress() { SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true); Height = 10; }
        public int Maximum { get => max; set { max = Math.Max(1, value); Invalidate(); } }
        public int Value { get => v; set { v = Math.Max(0, Math.Min(max, value)); Invalidate(); } }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.Clear(Parent?.BackColor ?? Theme.Bg);
            int h = Math.Min(4, Height), y = (Height - h) / 2;
            using (var b = new SolidBrush(Theme.Track)) g.FillRectangle(b, 0, y, Width, h);
            int w = (int)((long)Width * v / max);
            if (w > 0) using (var b = new SolidBrush(Theme.Accent)) g.FillRectangle(b, 0, y, w, h);
        }
    }

    /// <summary>Rounded card panel.</summary>
    public class Card : Panel
    {
        public Card() { BackColor = Theme.Card; Padding = new Padding(16, 14, 16, 14); Margin = new Padding(0, 0, 12, 12); Theme.Round(this, 8); DoubleBuffered = true; }
    }

    /// <summary>Small factory helpers so pages read like a layout description.</summary>
    public static class Ui
    {
        public static Label Title(string text, float size = 14) => new Label
        {
            Dock = DockStyle.Top, Height = 40, TextAlign = ContentAlignment.MiddleLeft, Text = text, Font = size == 14 ? Theme.Title : Theme.DisplayFont(size),
            ForeColor = Theme.Text, AutoEllipsis = true, UseMnemonic = false
        };
        public static Label Text(string text, bool sub = false) => new Label
        {
            AutoSize = true, Text = text, ForeColor = sub ? Theme.Sub : Theme.Text, Font = Theme.Body, UseMnemonic = false, Margin = new Padding(0, 7, 6, 0)
        };
        public static ComboBox Combo(int width, params string[] items)
        {
            var c = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = width, Margin = new Padding(0, 3, 18, 0), Font = Theme.Body };
            c.Items.AddRange(items); return c;
        }
        public static FlowLayoutPanel Row(DockStyle dock = DockStyle.Top, int top = 0) => new FlowLayoutPanel
        {
            Dock = dock, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, Padding = new Padding(0, top, 0, 6)
        };
        /// <summary>Adds controls so they stack top-to-bottom in the order given (WinForms docks the last-added first).</summary>
        public static void Stack(Control parent, params Control[] topToBottom)
        {
            for (int i = topToBottom.Length - 1; i >= 0; i--) parent.Controls.Add(topToBottom[i]);
        }
    }
}
