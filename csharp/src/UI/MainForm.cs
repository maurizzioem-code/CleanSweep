using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace CleanSweep.UI
{
    /// <summary>A page shown in the main window (one per sidebar entry).</summary>
    public class Page : Panel
    {
        public string Title { get; }
        public string Icon { get; }
        public Page(string title, string icon)
        {
            Title = title; Icon = icon; Text = title;
            Dock = DockStyle.Fill; BackColor = Theme.Bg; ForeColor = Theme.Text; Padding = new Padding(24, 18, 24, 16); Font = Theme.Body;
            DoubleBuffered = true;
        }
        /// <summary>Called every time the page is shown.</summary>
        public virtual void OnShown() { }
    }

    /// <summary>Main window: dark title bar, sidebar on the left, the selected page on the right.</summary>
    public class MainForm : Form
    {
        readonly Panel side = new Panel { Dock = DockStyle.Left, Width = 220, BackColor = Theme.Side, Padding = new Padding(0, 8, 0, 8) };
        readonly Panel host = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };
        readonly List<NavItem> nav = new List<NavItem>();
        public readonly List<Page> Pages = new List<Page>();
        public Page Current { get; private set; }
        public event Action<Page> PageChanged;

        public MainForm()
        {
            Text = "CleanSweep " + Program.VersionText + " - PC Health, Cleanup and Repair";
            BackColor = Theme.Bg; ForeColor = Theme.Text; Font = Theme.Body;
            AutoScaleMode = AutoScaleMode.None; StartPosition = FormStartPosition.CenterScreen;
            try { Icon = Program.AppIcon; } catch { }
            // Comfortable on a 1366x768 laptop, never bigger than the screen
            var wa = Screen.PrimaryScreen.WorkingArea;
            MinimumSize = new Size(Math.Min(960, wa.Width), Math.Min(640, wa.Height));
            Size = new Size(Math.Min(1200, wa.Width), Math.Min(800, wa.Height));
            Native.DarkTitleBar(this);
            DoubleBuffered = true;
        }

        /// <summary>Builds the sidebar for the given pages (in order) and shows the first one.</summary>
        public void Build(IEnumerable<Page> pages)
        {
            Pages.AddRange(pages);
            var brand = new Panel { Dock = DockStyle.Top, Height = 64, BackColor = Theme.Side };
            var logo = new PictureBox { Size = new Size(28, 28), Location = new Point(20, 18), SizeMode = PictureBoxSizeMode.StretchImage };
            try { logo.Image = new Icon(Program.AppIcon, 32, 32).ToBitmap(); } catch { }
            brand.Controls.Add(logo);
            brand.Controls.Add(new Label { Text = "CleanSweep", Location = new Point(56, 13), AutoSize = true, Font = Theme.DisplayFont(13), ForeColor = Theme.Text });
            brand.Controls.Add(new Label { Text = "Version " + Program.VersionText, Location = new Point(57, 37), AutoSize = true, Font = Theme.UiFont(8.5f), ForeColor = Theme.Sub });

            var items = Pages.Select(p => new NavItem(this, p)).ToList();
            nav.AddRange(items);
            var stack = new List<Control> { brand }; stack.AddRange(items);
            Ui.Stack(side, stack.ToArray());
            side.Controls.Add(new Label
            {
                Dock = DockStyle.Bottom, Height = 40, Text = "Monitor - Clean - Repair\nNothing changes without your click",
                ForeColor = Theme.Faint, Font = Theme.UiFont(8), Padding = new Padding(20, 0, 0, 0)
            });

            foreach (var p in Pages) { p.Visible = false; host.Controls.Add(p); }
            Controls.Add(host); Controls.Add(side);
            Theme.Apply(this);
            if (Pages.Count > 0) ShowPage(Pages[0]);
        }

        public void ShowPage(Page p)
        {
            if (p == null || p == Current) return;
            SuspendLayout();
            p.Visible = true; p.BringToFront();
            if (Current != null) Current.Visible = false;
            Current = p;
            ResumeLayout(true);
            foreach (var n in nav) n.Invalidate();
            p.OnShown();
            PageChanged?.Invoke(p);
        }
        public T Page<T>() where T : Page => Pages.OfType<T>().FirstOrDefault();

        /// <summary>Sidebar entry: icon + name, rounded highlight and accent bar when selected.</summary>
        class NavItem : Panel
        {
            readonly MainForm form; readonly Page page; bool hover;
            public NavItem(MainForm f, Page p)
            {
                form = f; page = p; Dock = DockStyle.Top; Height = 40; Cursor = Cursors.Hand; BackColor = Theme.Side; DoubleBuffered = true;
                AccessibleName = p.Title; AccessibleRole = AccessibleRole.PageTab;
            }
            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics; g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                bool sel = form.Current == page;
                if (sel || hover)
                {
                    using (var path = Theme.RoundRect(new Rectangle(8, 3, Width - 16, Height - 6), 5))
                    using (var b = new SolidBrush(sel ? Theme.Card : Theme.NavHover)) g.FillPath(b, path);
                    if (sel) using (var path = Theme.RoundRect(new Rectangle(8, 12, 3, Height - 24), 1)) using (var a = new SolidBrush(Theme.Accent)) g.FillPath(a, path);
                }
                TextRenderer.DrawText(g, page.Icon, Theme.NavIcon, new Rectangle(22, 0, 26, Height), Theme.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter);
                TextRenderer.DrawText(g, page.Title, Theme.Nav, new Rectangle(56, 0, Width - 60, Height), Theme.Text,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            }
            protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); }
            protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); }
            protected override void OnClick(EventArgs e) { form.ShowPage(page); }
        }
    }
}
