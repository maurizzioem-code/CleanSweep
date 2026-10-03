using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Linq;
using System.Windows.Forms;

namespace CleanSweep.UI
{
    /// <summary>Health score ring (270 degrees), coloured by grade.</summary>
    public class ScoreGauge : Control
    {
        int? score;
        public int? Score { get => score; set { score = value; Invalidate(); } }
        readonly Font num = Theme.DisplayFont(34), small = Theme.UiFont(9);

        public ScoreGauge() { SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true); BackColor = Color.Transparent; }

        public static Color GradeColor(int s) => s >= 90 ? Theme.Ok : s >= 75 ? ColorTranslator.FromHtml("#9BDB4D") : s >= 50 ? Theme.Warn : Theme.Bad;

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            float sz = Math.Min(Width, Height) - 24; if (sz < 20) return;
            var r = new RectangleF(10, (Height - sz) / 2, sz, sz);
            using (var pen = new Pen(Theme.Track, 12) { StartCap = LineCap.Round, EndCap = LineCap.Round }) g.DrawArc(pen, r, 135, 270);
            if (score > 0) using (var pen = new Pen(GradeColor(score.Value), 12) { StartCap = LineCap.Round, EndCap = LineCap.Round }) g.DrawArc(pen, r, 135, 270f * score.Value / 100);
            using (var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            {
                using (var b = new SolidBrush(Theme.Text)) g.DrawString(score?.ToString() ?? "--", num, b, new RectangleF(r.X, r.Y - 6, r.Width, r.Height), fmt);
                using (var b = new SolidBrush(Theme.Sub)) g.DrawString("HEALTH SCORE", small, b, new RectangleF(r.X, r.Y + r.Height * 0.30f, r.Width, r.Height), fmt);
            }
        }
    }

    /// <summary>One-click action tile: icon, title and a short line of text. Painted in one piece so hover and click are simple.</summary>
    public class ActionTile : Control
    {
        public string Key, Glyph, Title; string sub;
        public string Sub { get => sub; set { sub = value; Invalidate(); } }
        public string DefaultSub;
        bool available = true, hover;
        public bool Available { get => available; set { available = value; Invalidate(); } }
        static readonly Font IconF = Theme.IconFont(16), TitleF = Theme.UiFont(10, FontStyle.Bold), SubF = Theme.UiFont(8.5f);

        public ActionTile(string key, string glyph, string title, string subText)
        {
            Key = key; Glyph = glyph; Title = title; sub = DefaultSub = subText; Text = title;
            Dock = DockStyle.Fill; Margin = new Padding(0, 0, 8, 8); Cursor = Cursors.Hand;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            AccessibleRole = AccessibleRole.PushButton; AccessibleName = title;
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias; g.Clear(Parent?.BackColor ?? Theme.Card);
            using (var p = Theme.RoundRect(new Rectangle(0, 0, Width, Height), 6)) using (var b = new SolidBrush(hover && available ? Theme.TileHover : Theme.Tile)) g.FillPath(b, p);
            var fg = available ? Theme.Text : Theme.Faint;
            TextRenderer.DrawText(g, Glyph, IconF, new Rectangle(12, 0, 34, Height), available ? Theme.Accent : Theme.Faint, TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
            int x = 46, w = Math.Max(0, Width - x - 8);
            int th = TitleF.Height, top = Math.Max(6, (Height - th - 2 * SubF.Height - 4) / 2);
            TextRenderer.DrawText(g, Title, TitleF, new Rectangle(x, top, w, th + 2), fg, TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(g, sub ?? "", SubF, new Rectangle(x, top + th + 4, w, Height - top - th - 8), Theme.Sub, TextFormatFlags.Left | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
        public void PerformClick() => OnClick(EventArgs.Empty);
    }

    /// <summary>Hardware monitor card: title, big value, two lines of detail and a 60-point sparkline.</summary>
    public class HwCard : Card
    {
        public readonly string Glyph; public readonly Color Color; public readonly double Max;
        string title, value = "--", sub = "";
        public string Title { get => title; set { title = value; Invalidate(); } }
        public string Value { get => value; set { this.value = value; Invalidate(); } }
        public string Sub { get => sub; set { sub = value; Invalidate(); } }
        public readonly List<double> Data = new List<double>();
        static readonly Font IconF = Theme.IconFont(11), TitleF = Theme.UiFont(9.5f, FontStyle.Bold), ValueF = Theme.DisplayFont(20), SubF = Theme.UiFont(8.5f);
        static readonly Color Grid = ColorTranslator.FromHtml("#363636");

        public HwCard(string title, string glyph, Color color, double max)
        {
            this.title = title; Glyph = glyph; Color = color; Max = max; Dock = DockStyle.Fill; Padding = new Padding(16, 12, 16, 10);
            SetStyle(ControlStyles.ResizeRedraw, true);
        }
        public void Push(double v) { Data.Add(v); while (Data.Count > 60) Data.RemoveAt(0); Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; int x = Padding.Left, w = Width - Padding.Horizontal, y = Padding.Top;
            TextRenderer.DrawText(g, Glyph, IconF, new Rectangle(x, y, 24, 24), Color, TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
            TextRenderer.DrawText(g, title, TitleF, new Rectangle(x + 24, y, w - 24, 24), Theme.Sub, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
            y += 24;
            TextRenderer.DrawText(g, value, ValueF, new Rectangle(x, y, w, 40), Theme.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
            y += 40;
            var lines = (sub ?? "").Split('\n');
            for (int i = 0; i < Math.Min(2, lines.Length); i++)
                TextRenderer.DrawText(g, lines[i], SubF, new Rectangle(x, y + i * (SubF.Height + 2), w, SubF.Height + 2), Theme.Sub, TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            y += 2 * (SubF.Height + 2) + 4;
            var area = new Rectangle(x, y, w, Height - Padding.Bottom - y);
            if (area.Width < 10 || area.Height < 10) return;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int h = area.Height - 2;
            using (var gp = new Pen(Grid, 1)) { g.DrawLine(gp, area.X, area.Y + h, area.Right, area.Y + h); g.DrawLine(gp, area.X, area.Y + h / 2, area.Right, area.Y + h / 2); }
            if (Data.Count < 2) return;
            double max = Max > 0 ? Max : Math.Max(1, Data.Max() * 1.2);
            float step = area.Width / 59f; int off = 60 - Data.Count;
            var pts = Data.Select((d, i) => new PointF(area.X + (off + i) * step, area.Y + h - (float)Math.Min(h, d / max * h))).ToArray();
            using (var path = new GraphicsPath())
            {
                path.AddLines(pts); path.AddLine(pts[pts.Length - 1].X, area.Y + h, pts[0].X, area.Y + h); path.CloseFigure();
                using (var fill = new SolidBrush(Color.FromArgb(50, Color))) g.FillPath(fill, path);
            }
            using (var pen = new Pen(Color, 2)) g.DrawLines(pen, pts);
        }
    }
}
