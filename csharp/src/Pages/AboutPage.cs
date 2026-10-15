using System;
using System.Drawing;
using System.Windows.Forms;
using CleanSweep.Engine;
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
            flow.Controls.Add(L("Updates", Theme.Section, null, 18));
            UpdStatus = L("Updates have not been checked yet.", null, null); flow.Controls.Add(UpdStatus);
            UpdNotes = L("", null, Theme.Sub); flow.Controls.Add(UpdNotes);
            AutoUpd.Checked = Updater.AutoCheck; AutoUpd.Margin = new Padding(0, 2, 0, 4); flow.Controls.Add(AutoUpd);
            AutoUpd.CheckedChanged += (s, e) => Updater.AutoCheck = AutoUpd.Checked;
            var ub = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = new Padding(0, 4, 0, 6), MaximumSize = new Size(700, 0) };
            ub.Controls.AddRange(new Control[] { CheckUpd, InstallUpd, SkipUpd });
            flow.Controls.Add(ub);
            CheckUpd.Click += async (s, e) => await CheckNowAsync(true);
            InstallUpd.Click += async (s, e) => await InstallUpdateAsync();
            SkipUpd.Click += (s, e) => { if (Updater.Available != null) { Updater.Skip(Updater.Available); ShowUpdate(); } };
            Updater.Changed += () => { if (IsHandleCreated) BeginInvoke((Action)ShowUpdate); };
            flow.Controls.Add(L("Installation", Theme.Section, null, 18));
            InstallInfo = L("", null, Theme.Sub); flow.Controls.Add(InstallInfo);
            var bar = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = new Padding(0, 4, 0, 6), MaximumSize = new Size(700, 0) };
            bar.Controls.AddRange(new Control[] { InstallBtn, UninstallBtn, OpenFolder });
            flow.Controls.Add(bar);
            InstallBtn.Click += (s, e) => ShowInstall();
            UninstallBtn.Click += (s, e) => ShowUninstall();
            OpenFolder.Click += (s, e) => Shell.Open("explorer.exe", "\"" + Installer.Dir + "\"");
            flow.Controls.Add(L("Ready in this preview", Theme.Section, null, 18));
            flow.Controls.Add(L("- Dashboard: health score and history, recommendations, live hardware monitor, Quick clean and Restore point"));
            flow.Controls.Add(L("- Shortcuts: broken shortcuts on the Desktop and Start menu (to the Recycle Bin)"));
            flow.Controls.Add(L("- Network: connection test, safe fixes that only restore Windows defaults, Wi-Fi channels, Ethernet diagnostics"));
            flow.Controls.Add(L("- Registry: leftovers from removed programs only, with a .reg backup and restore point first"));
            flow.Controls.Add(L("- Drives: every volume, check for errors (chkdsk /scan) and optimize (TRIM / defragment) with Windows' own tools"));
            flow.Controls.Add(L("- Repair: DISM, System File Checker, Windows Update repair with undo, live output and saved logs"));
            flow.Controls.Add(L("- Automatic cleanup: schedule, plugged-in/idle conditions, notification, history (shared with the main app)"));
            flow.Controls.Add(L("- Large files: biggest files by type on any drive, app and game files hidden, Recycle Bin only"));
            flow.Controls.Add(L("- Cleanup: junk and temp files, other drives, old Recycle Bin items, files that can't be deleted, ignore list"));
            flow.Controls.Add(L("- Installer: per-user install with Start menu and Desktop shortcuts, listed in Settings > Apps"));
            flow.Controls.Add(L("- Updates: checks GitHub once a day, verifies each download's fingerprint before installing it"));
            flow.Controls.Add(L("Settings", Theme.Section, null, 18));
            flow.Controls.Add(L("Shared with the main app: " + AppPaths.Settings, null, Theme.Sub));
            flow.Controls.Add(L("Your ignore list, file age and Recycle Bin choices are the same in both editions.", null, Theme.Sub));
            Controls.Add(flow);
            UpdateInstallInfo();
            ShowUpdate();
        }

        public Label UpdStatus, UpdNotes;
        public readonly CsCheckBox AutoUpd = new CsCheckBox("Check for updates automatically (once a day, when CleanSweep opens)");
        public readonly CsButton CheckUpd = new CsButton("Check now"), InstallUpd = new CsButton("Install update", true) { Visible = false }, SkipUpd = new CsButton("Skip this version") { Visible = false };
        public bool UpdBusy { get; private set; }
        /// <summary>Self-test: the update is downloaded and checked, then handed to this instead of restarting CleanSweep.</summary>
        public Action<string> TestHandOver;

        public void ShowUpdate()
        {
            var u = Updater.Available; var last = Updater.LastCheck;
            if (!UpdBusy)
                UpdStatus.Text = u != null ? $"Build {u.Version.Revision} is available. You have build {Updater.Current.Revision}."
                    : last != null ? $"You have the latest version (build {Updater.Current.Revision}). Last checked {last.Value:MMM d, h:mm tt}."
                    : "Updates have not been checked yet.";
            UpdStatus.ForeColor = u != null ? Theme.Accent : Theme.Text;
            UpdNotes.Text = u != null && u.Notes.Length > 0 ? "What's new: " + u.Notes : "";
            UpdNotes.Visible = UpdNotes.Text.Length > 0;
            InstallUpd.Visible = SkipUpd.Visible = u != null;
            InstallUpd.Enabled = SkipUpd.Enabled = CheckUpd.Enabled = !UpdBusy;
        }

        public async System.Threading.Tasks.Task CheckNowAsync(bool manual)
        {
            if (UpdBusy) return;
            UpdBusy = true; UpdStatus.Text = "Checking for updates..."; ShowUpdate();
            string err = null;
            try { if (manual) Settings.Remove("CsSkipVersion"); await System.Threading.Tasks.Task.Run(() => Updater.Check()); }
            catch (Exception e) { err = e is System.Net.WebException ? "Could not reach the update server. Check your internet connection." : "Could not check for updates: " + e.Message; }
            finally { UpdBusy = false; }
            ShowUpdate();
            if (err != null) { UpdStatus.Text = err; UpdStatus.ForeColor = Theme.Warn; }
        }

        public async System.Threading.Tasks.Task InstallUpdateAsync()
        {
            var u = Updater.Available; if (u == null || UpdBusy) return;
            if (!Msg.Confirm($"Install CleanSweep build {u.Version.Revision}?\n\nCleanSweep will close, update and open again. Your settings, history and schedule are kept.", "Update CleanSweep")) return;
            if (ToolLock.Busy) { Msg.Show($"\"{ToolLock.Running}\" is still running. Wait for it to finish, then install the update.", "Update CleanSweep"); return; }
            UpdBusy = true; ShowUpdate();
            try
            {
                string file = await System.Threading.Tasks.Task.Run(() => Updater.Download(u, p => BeginInvoke((Action)(() => UpdStatus.Text = $"Downloading build {u.Version.Revision}... {p}%")), default));
                UpdStatus.Text = "Download checked. Installing...";
                if (TestHandOver != null) { TestHandOver(file); return; }
                Updater.HandOver(file, true);
                Application.Exit();
            }
            catch (Exception e)
            {
                UpdStatus.Text = e is System.Net.WebException ? "The update could not be downloaded. Check your internet connection." : e.Message;
                UpdStatus.ForeColor = Theme.Bad; Trace.Write("Update failed: " + e);
            }
            finally { UpdBusy = false; InstallUpd.Enabled = SkipUpd.Enabled = CheckUpd.Enabled = true; }
        }

        public Label InstallInfo;
        public readonly CsButton InstallBtn = new CsButton("Install...", true), UninstallBtn = new CsButton("Uninstall..."), OpenFolder = new CsButton("Open app folder");
        public InstallForm OpenInstall; public UninstallForm OpenUninstall;   // self-test

        public void UpdateInstallInfo()
        {
            var iv = Installer.InstalledVersion;
            if (Installer.RunningInstalled) InstallInfo.Text = $"Installed for your account in {Installer.Dir}. Find it in the Start menu as \"CleanSweep\" and remove it in Settings > Apps, or here.";
            else if (iv != null) InstallInfo.Text = $"Installed: {Installer.VersionLabel(iv)} in {Installer.Dir}. You're running a copy from {System.IO.Path.GetDirectoryName(Installer.Source)}" + (Installer.Newer ? " that is newer - click Update to install it." : ".");
            else InstallInfo.Text = $"Not installed - running from {System.IO.Path.GetDirectoryName(Installer.Source)}. Install it to get a Start menu shortcut and an entry in Settings > Apps.";
            InstallBtn.Text = iv == null ? "Install..." : Installer.RunningInstalled ? "Repair shortcuts..." : "Update...";
            UninstallBtn.Enabled = OpenFolder.Enabled = iv != null;
        }

        public void ShowInstall()
        {
            var f = new InstallForm(true); OpenInstall = f;
            if (Msg.Test) { f.Show(FindForm()); return; }
            using (f) if (f.ShowDialog(FindForm()) == DialogResult.OK && !Installer.RunningInstalled && f.StartAfter.Checked)
                {
                    // hand over to the installed copy
                    Program.StartInstalled(); Application.Exit(); return;
                }
            OpenInstall = null; UpdateInstallInfo();
        }

        public void ShowUninstall(bool? removeData = null)
        {
            bool remove;
            if (removeData == null)
                using (var f = new UninstallForm()) { if (f.ShowDialog(FindForm()) != DialogResult.OK) return; remove = f.RemoveData.Checked; }
            else remove = removeData.Value;
            bool self = Installer.RunningInstalled;
            var log = Installer.Uninstall(remove);
            Msg.Show("CleanSweep was uninstalled.\n\n- " + string.Join("\n- ", log), "CleanSweep", MessageBoxButtons.OK, MessageBoxIcon.Information);
            if (self && !Msg.Test) { Application.Exit(); return; }
            UpdateInstallInfo();
        }
    }
}
