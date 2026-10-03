using System.Drawing;
using System.Windows.Forms;
using CleanSweep.UI;

namespace CleanSweep.Pages
{
    /// <summary>Settings / about. Grows as more of CleanSweep moves to C#.</summary>
    public class AboutPage : Page
    {
        public AboutPage() : base("Settings", Glyph.Settings)
        {
            var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
            Label L(string t, Font f = null, Color? c = null, int top = 0) => new Label
            {
                AutoSize = true, MaximumSize = new Size(680, 0), Text = t, Font = f ?? Theme.Body, ForeColor = c ?? Theme.Text,
                Margin = new Padding(0, top, 0, 6), UseMnemonic = false
            };
            flow.Controls.Add(L("CleanSweep " + Program.VersionText, Theme.Title));
            flow.Controls.Add(L("C# edition (preview). This is the new native version of CleanSweep, being rebuilt page by page. " +
                                "It is faster and stays responsive while it works.", null, Theme.Sub));
            flow.Controls.Add(L("Ready in this preview", Theme.Section, null, 18));
            flow.Controls.Add(L("- Dashboard: health score and history, recommendations, live hardware monitor, Quick clean and Restore point"));
            flow.Controls.Add(L("- Cleanup: junk and temp files, other drives, old Recycle Bin items, files that can't be deleted, ignore list"));
            flow.Controls.Add(L("Still in the main CleanSweep app for now", Theme.Section, null, 18));
            flow.Controls.Add(L("- Automatic cleanup, Drives, Repair, Broken Shortcuts, Network Optimizer, Registry cleaner, updates", null, Theme.Sub));
            flow.Controls.Add(L("Settings", Theme.Section, null, 18));
            flow.Controls.Add(L("Shared with the main app: " + AppPaths.Settings, null, Theme.Sub));
            flow.Controls.Add(L("Your ignore list, file age and Recycle Bin choices are the same in both editions.", null, Theme.Sub));
            Controls.Add(flow);
        }
    }
}
