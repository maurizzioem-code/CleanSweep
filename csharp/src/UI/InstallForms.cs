using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using CleanSweep.Engine;

namespace CleanSweep.UI
{
    /// <summary>Install / update window. Shown when CleanSweep is started from Downloads, or from Settings.</summary>
    public class InstallForm : Form
    {
        public readonly CsCheckBox StartMenu = new CsCheckBox("Add CleanSweep to the Start menu", true),
            Desktop = new CsCheckBox("Add a Desktop shortcut", true),
            StartAfter = new CsCheckBox("Open CleanSweep when it's done", true),
            DontAsk = new CsCheckBox("Don't ask again when I run it without installing");
        public readonly CsButton Install = new CsButton("Install", true) { MinimumSize = new Size(120, 34), Margin = new Padding(8, 0, 0, 0) };
        public readonly CsButton Later = new CsButton("Run without installing") { MinimumSize = new Size(100, 34), Margin = new Padding(8, 0, 0, 0) };
        public readonly Label Result = new Label { AutoSize = true, MaximumSize = new Size(540, 0), Margin = new Padding(0, 10, 0, 0), UseMnemonic = false };
        public bool Installed { get; private set; }
        public bool FromSettings;

        static Label L(string t, Font f, Color c, int top = 0) => new Label { Text = t, Font = f, ForeColor = c, AutoSize = true, MaximumSize = new Size(540, 0), Margin = new Padding(0, top, 0, 4), UseMnemonic = false };

        public InstallForm(bool fromSettings = false)
        {
            FromSettings = fromSettings;
            bool update = Installer.IsInstalled && !Installer.RunningInstalled;
            var iv = Installer.InstalledVersion;
            Text = update ? "Update CleanSweep" : "Install CleanSweep"; Size = new Size(620, 470); MinimumSize = new Size(560, 400);
            StartPosition = FormStartPosition.CenterScreen; MaximizeBox = false; MinimizeBox = false; Icon = Program.AppIcon; ShowInTaskbar = !fromSettings;
            var body = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(22, 18, 22, 8) };
            body.Controls.Add(L(Text, Theme.DisplayFont(15), Theme.Text));
            string what = update
                ? (Installer.Newer ? $"Installed: {Installer.VersionLabel(iv)}. This copy is {Installer.VersionLabel(Installer.MyVersion)}." : $"Installed: {Installer.VersionLabel(iv)}. This copy is the same version or older - installing it again repairs the shortcuts.")
                : $"CleanSweep {Installer.VersionLabel(Installer.MyVersion)} will be installed for your account in:";
            body.Controls.Add(L(what, Theme.Body, Theme.Sub, 4));
            body.Controls.Add(L(Installer.Dir, Theme.UiFont(9.5f, FontStyle.Bold), Theme.Text));
            body.Controls.Add(L("Nothing is added to Program Files, startup, services or drivers. CleanSweep will be listed in Settings > Apps, so you can uninstall it like any other app. Your settings and history are kept.", Theme.Body, Theme.Sub, 6));
            if (Installer.PowerShellEdition)
                body.Controls.Add(L("The PowerShell edition of CleanSweep is on this PC too. Its files, settings and schedule stay as they are; its shortcuts will open this version instead, and uninstalling this version puts them back.", Theme.Body, Theme.Warn, 6));
            foreach (var c in new[] { StartMenu, Desktop, StartAfter }) { c.Margin = new Padding(0, c == StartMenu ? 12 : 2, 0, 2); body.Controls.Add(c); }
            if (update)
            {
                // keep what the user chose last time
                StartMenu.Checked = System.IO.File.Exists(Installer.StartMenuLink) && Is(Installer.StartMenuLink);
                Desktop.Checked = System.IO.File.Exists(Installer.DesktopLink) && Is(Installer.DesktopLink);
                if (!StartMenu.Checked && !Desktop.Checked) StartMenu.Checked = true;
                Install.Text = "Update";
            }
            if (!fromSettings) { DontAsk.Margin = new Padding(0, 14, 0, 2); DontAsk.ForeColor = Theme.Sub; body.Controls.Add(DontAsk); }
            body.Controls.Add(Result);

            var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 58, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(16, 10, 16, 10), BackColor = Theme.Side };
            if (fromSettings) Later.Text = "Cancel";
            bar.Controls.AddRange(new Control[] { Install, Later });
            Controls.Add(body); Controls.Add(bar);
            AcceptButton = Install; CancelButton = Later;
            Install.Click += (s, e) => DoInstall();
            Later.Click += (s, e) => { if (DontAsk.Checked) { Settings.Set("InstallPrompt", "never"); Settings.Save(); } DialogResult = DialogResult.Cancel; if (!Msg.Test) Close(); };
            Theme.DarkDialog(this);
            Result.ForeColor = Theme.Ok; DontAsk.ForeColor = Theme.Sub;
        }
        static bool Is(string lnk) => string.Equals(Shortcuts.Target(lnk), Installer.Exe, StringComparison.OrdinalIgnoreCase);

        public void DoInstall()
        {
            Cursor = Cursors.WaitCursor; Install.Enabled = Later.Enabled = false;
            try
            {
                var log = Installer.Install(new Installer.Options { StartMenu = StartMenu.Checked, Desktop = Desktop.Checked },
                    n => Msg.Confirm("CleanSweep is open. Close it so it can be updated?", Text));
                Installed = true;
                Result.ForeColor = Theme.Ok;
                Result.Text = (Install.Text == "Update" ? "Updated. " : "Installed. ") + (StartMenu.Checked ? "Find it in the Start menu as \"CleanSweep\"." : "");
                Settings.Remove("InstallPrompt"); Settings.Save();
                DialogResult = DialogResult.OK;
                if (!Msg.Test) Close();
            }
            catch (OperationCanceledException) { Result.ForeColor = Theme.Warn; Result.Text = "Nothing was changed - close CleanSweep first, then try again."; }
            catch (Exception e) { Result.ForeColor = Theme.Bad; Result.Text = "Could not install: " + e.Message; Trace.Write("Install failed: " + e); }
            finally { Cursor = Cursors.Default; Install.Enabled = Later.Enabled = true; }
        }
    }

    /// <summary>Confirmation before uninstalling (from Settings > Apps or CleanSweep's own Settings page).</summary>
    public class UninstallForm : Form
    {
        public readonly CsCheckBox RemoveData = new CsCheckBox("Also remove my settings, history, logs and the automatic cleanup schedule");
        public readonly CsButton Go = new CsButton("Uninstall", true) { MinimumSize = new Size(120, 34), Margin = new Padding(8, 0, 0, 0) };
        public readonly CsButton Cancel = new CsButton("Cancel") { MinimumSize = new Size(100, 34), Margin = new Padding(8, 0, 0, 0), DialogResult = DialogResult.Cancel };
        static Label L(string t, Font f, Color c, int top = 0) => new Label { Text = t, Font = f, ForeColor = c, AutoSize = true, MaximumSize = new Size(500, 0), Margin = new Padding(0, top, 0, 4), UseMnemonic = false };

        public UninstallForm()
        {
            Text = "Uninstall CleanSweep"; Size = new Size(580, 340); MinimumSize = new Size(520, 300);
            StartPosition = FormStartPosition.CenterScreen; MaximizeBox = false; MinimizeBox = false; Icon = Program.AppIcon;
            var body = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(22, 18, 22, 8) };
            body.Controls.Add(L("Uninstall CleanSweep?", Theme.DisplayFont(15), Theme.Text));
            body.Controls.Add(L("Removes the app from " + Installer.Dir + ", its Start menu and Desktop shortcuts and its entry in Settings > Apps.", Theme.Body, Theme.Sub, 4));
            if (Installer.PowerShellEdition)
                body.Controls.Add(L("The PowerShell edition stays installed and gets its shortcuts back. Settings it shares are kept.", Theme.Body, Theme.Warn, 4));
            RemoveData.Margin = new Padding(0, 12, 0, 2); body.Controls.Add(RemoveData);
            var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 58, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(16, 10, 16, 10), BackColor = Theme.Side };
            bar.Controls.AddRange(new Control[] { Go, Cancel });
            Controls.Add(body); Controls.Add(bar);
            AcceptButton = Go; CancelButton = Cancel;
            Go.Click += (s, e) => { DialogResult = DialogResult.OK; if (!Msg.Test) Close(); };
            Theme.DarkDialog(this);
        }
    }
}
