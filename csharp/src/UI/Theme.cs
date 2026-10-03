using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace CleanSweep.UI
{
    /// <summary>Windows 11 dark palette, fonts and icon glyphs.</summary>
    public static class Theme
    {
        static Color C(string hex) => ColorTranslator.FromHtml(hex);
        public static readonly Color Bg = C("#202020"), Side = C("#1A1A1A"), Card = C("#2B2B2B"), CardHover = C("#323232"),
            Tile = C("#333333"), TileHover = C("#3C3C3C"), Input = C("#1C1C1C"), Button = C("#2D2D2D"), ButtonHover = C("#383838"),
            ButtonDown = C("#262626"), Border = C("#3D3D3D"), Text = C("#FFFFFF"), Sub = C("#A6A6A6"), Faint = C("#6E6E6E"),
            Accent = C("#60CDFF"), AccentHover = C("#7AD6FF"), AccentDown = C("#4BB8EA"), OnAccent = C("#000000"),
            Ok = C("#6CCB5F"), Warn = C("#FCE100"), Bad = C("#FF99A4"), Info = C("#A6A6A6"), Select = C("#3A3F46"),
            NavHover = C("#262626"), Track = C("#3A3A3A");

        static readonly string[] Families = FontFamily.Families.Select(f => f.Name).ToArray();
        static string Pick(string want, string fallback) => Families.Contains(want) ? want : fallback;
        public static readonly string UiFontName = Pick("Segoe UI Variable Text", "Segoe UI");
        public static readonly string DisplayFontName = Pick("Segoe UI Variable Display", "Segoe UI");
        public static readonly string IconFontName = Pick("Segoe Fluent Icons", "Segoe MDL2 Assets");

        public static Font UiFont(float size = 10, FontStyle style = FontStyle.Regular) => new Font(UiFontName, size, style);
        public static Font DisplayFont(float size = 14, FontStyle style = FontStyle.Bold) => new Font(DisplayFontName, size, style);
        public static Font IconFont(float size = 14) => new Font(IconFontName, size);

        // Cached fonts for painting (avoid creating GDI objects on every repaint)
        public static readonly Font Body = UiFont(10), Small = UiFont(9), Header = UiFont(9, FontStyle.Bold),
            Nav = UiFont(10), NavIcon = IconFont(13), Title = DisplayFont(14), Section = UiFont(10.5f, FontStyle.Bold);

        public static GraphicsPath RoundRect(Rectangle r, int rad)
        {
            var p = new GraphicsPath(); int d = rad * 2;
            if (r.Width <= d || r.Height <= d) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d - 1, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d - 1, r.Bottom - d - 1, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d - 1, d, d, 90, 90);
            p.CloseFigure(); return p;
        }

        /// <summary>Clips a control to a rounded rectangle and keeps it that way when resized.</summary>
        public static void Round(Control c, int rad)
        {
            void Update() { if (c.Width > 0 && c.Height > 0) using (var p = RoundRect(new Rectangle(0, 0, c.Width, c.Height), rad)) c.Region = new Region(p); }
            Update(); c.Resize += (s, e) => Update();
        }

        public static void DoubleBuffer(Control c) =>
            typeof(Control).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.SetValue(c, true, null);

        /// <summary>Checkmark box in the Windows 11 style, shared by check boxes and list rows.</summary>
        public static void DrawCheck(Graphics g, Rectangle box, bool on, bool enabled)
        {
            var old = g.SmoothingMode; g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = RoundRect(box, 3))
            {
                if (on)
                {
                    using (var b = new SolidBrush(enabled ? Accent : Faint)) g.FillPath(b, path);
                    using (var pen = new Pen(OnAccent, 1.8f))
                        g.DrawLines(pen, new[] { new PointF(box.X + 3.5f, box.Y + 7.5f), new PointF(box.X + 6.5f, box.Y + 10.5f), new PointF(box.X + 11.5f, box.Y + 4.5f) });
                }
                else using (var pen = new Pen(enabled ? Sub : Faint, 1)) g.DrawPath(pen, path);
            }
            g.SmoothingMode = old;
        }

        /// <summary>Dark styling for standard controls that aren't CleanSweep's own (combo boxes, text boxes, scroll panels).</summary>
        public static void Apply(Control root)
        {
            foreach (Control c in root.Controls)
            {
                switch (c)
                {
                    case ComboBox cb:
                        cb.FlatStyle = FlatStyle.Flat; cb.BackColor = Button; cb.ForeColor = Text;
                        Native.OnHandle(cb, h => Native.DarkWindow(h, "DarkMode_CFD")); break;
                    case TextBox tb:
                        tb.BackColor = Input; tb.ForeColor = Text; tb.BorderStyle = BorderStyle.FixedSingle;
                        Native.OnHandle(tb, h => Native.DarkWindow(h, "DarkMode_Explorer")); break;
                    case ListBox lb:
                        lb.BackColor = Input; lb.ForeColor = Text; lb.BorderStyle = BorderStyle.None;
                        Native.OnHandle(lb, h => Native.DarkWindow(h, "DarkMode_Explorer")); break;
                    case ScrollableControl sc when sc.AutoScroll:
                        Native.OnHandle(sc, h => Native.DarkWindow(h, "DarkMode_Explorer")); break;
                }
                if (c.HasChildren && !(c is ListView)) Apply(c);
            }
        }

        /// <summary>Dark title bar and controls for a pop-up window.</summary>
        public static void DarkDialog(Form f)
        {
            f.BackColor = Bg; f.ForeColor = Text; f.Font = Body;
            Native.DarkTitleBar(f); Apply(f);
        }
    }

    /// <summary>Icon glyphs from Segoe Fluent Icons / MDL2 Assets.</summary>
    public static class Glyph
    {
        public const string Home = "\uE80F", Junk = "\uE74D", Drive = "\uEDA2", Registry = "\uE8F1", Link = "\uE71B", Network = "\uE968",
            Repair = "\uE90F", Update = "\uE895", Cpu = "\uE950", Memory = "\uE964", Gpu = "\uE7F4", Disk = "\uEDA2", Net = "\uE701",
            Battery = "\uE83F", Pc = "\uE7F8", Clean = "\uE74D", Space = "\uE8B7", Speed = "\uEC4A", Shield = "\uE83D", Restore = "\uE777",
            Heart = "\uE95E", Report = "\uE9F9", Settings = "\uE713";
    }

    /// <summary>Windows calls for dark mode (title bar, scrollbars, headers).</summary>
    public static class Native
    {
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int val, int size);
        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] public static extern int SetWindowTheme(IntPtr h, string app, string id);
        [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
        // Undocumented but stable since Windows 10 1809: lets standard controls (scrollbars, menus) use dark mode
        [DllImport("uxtheme.dll", EntryPoint = "#135")] static extern int SetPreferredAppMode(int mode);
        [DllImport("uxtheme.dll", EntryPoint = "#133")] static extern bool AllowDarkModeForWindow(IntPtr h, bool allow);
        [DllImport("uxtheme.dll", EntryPoint = "#136")] static extern void FlushMenuThemes();

        public static void DarkApp() { try { SetPreferredAppMode(2); FlushMenuThemes(); } catch { } }
        public static void DarkWindow(IntPtr h, string theme) { try { AllowDarkModeForWindow(h, true); } catch { } try { SetWindowTheme(h, theme, null); } catch { } }

        public static void OnHandle(Control c, Action<IntPtr> a)
        {
            if (c.IsHandleCreated) { try { a(c.Handle); } catch { } return; }
            c.HandleCreated += (s, e) => { try { a(c.Handle); } catch { } };
        }

        public static void DarkTitleBar(Form f) => OnHandle(f, h =>
        {
            int one = 1; DwmSetWindowAttribute(h, 20, ref one, 4); DwmSetWindowAttribute(h, 19, ref one, 4);
            int cap = ColorTranslator.ToWin32(Theme.Side); DwmSetWindowAttribute(h, 35, ref cap, 4);   // caption colour (Windows 11)
        });
    }
}
