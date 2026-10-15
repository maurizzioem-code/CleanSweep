using System;
using System.Drawing;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using CleanSweep.Pages;
using CleanSweep.UI;

namespace CleanSweep
{
    static class Program
    {
        public static string VersionText => Engine.Installer.VersionLabel(Engine.Installer.MyVersion);
        public static Icon AppIcon;
        public static MainForm Form;

        [STAThread]
        static int Main(string[] args)
        {
            // Automatic cleanup (started by Task Scheduler or "Run now"): no window at all
            if (args.Length > 0 && args[0] == "--autoclean")
            {
                Settings.Load();
                var r = Engine.AutoClean.RunOnce(args.Length > 1 ? args[1] : "Scheduled");
                return r == null ? 1 : 0;
            }
            // Installed-copy check used by the self-test: write the version and (optionally) stay running for a while
            if (args.Length > 1 && args[0] == "--ping")
            {
                System.IO.File.WriteAllText(args[1], Engine.Installer.MyVersion + "\r\n" + Application.ExecutablePath);
                if (args.Length > 3 && args[2] == "--wait") Thread.Sleep(int.Parse(args[3]) * 1000);
                return 0;
            }
            // Signature checks used by the build's signing test: --verify-update <file> <version> <result file>
            if (args.Length > 3 && args[0] == "--verify-update")
            {
                var u = new Engine.UpdateInfo { Version = Version.Parse(args[2]), Sha256 = Engine.Updater.Sha256(args[1]) };
                string why = Engine.Updater.Verify(args[1], u);
                System.IO.File.WriteAllText(args[3], (why ?? "OK") + "\r\nthis copy: " + Engine.Signing.Check(Application.ExecutablePath) + "\r\nupdate: " + Engine.Signing.Check(args[1]));
                return why == null ? 0 : 3;
            }
            bool quiet = Array.IndexOf(args, "--quiet") >= 0;
            int sb = Array.IndexOf(args, "--sandbox"); if (sb >= 0 && sb + 1 < args.Length) Engine.Installer.UseSandbox(args[sb + 1]);
            if (args.Length > 0 && (args[0] == "--install" || args[0] == "--uninstall"))
            {
                Startup();
                Settings.Load();
                try
                {
                    if (args[0] == "--install")
                    {
                        // an update: wait until the old window has closed so its exe can be replaced
                        int wp = Array.IndexOf(args, "--wait-pid");
                        if (wp >= 0 && wp + 1 < args.Length && int.TryParse(args[wp + 1], out int pid))
                            try { using (var old = System.Diagnostics.Process.GetProcessById(pid)) old.WaitForExit(60000); } catch { }
                        if (quiet)
                        {
                            var opt = Array.IndexOf(args, "--keep-shortcuts") >= 0 && Engine.Installer.IsInstalled ? Engine.Installer.Options.Current()
                                : new Engine.Installer.Options { StartMenu = Array.IndexOf(args, "--no-startmenu") < 0, Desktop = Array.IndexOf(args, "--no-desktop") < 0 };
                            Engine.Installer.Install(opt);
                            // the PowerShell edition's last update runs "--install --quiet --retire-powershell"
                            if (Array.IndexOf(args, "--retire-powershell") >= 0 && Engine.PsEdition.Present)
                                try { Engine.PsEdition.Retire(n => true); }
                                catch (Exception x) { Trace.Write("Retire failed (the PowerShell edition was left as it is): " + x); }
                            if (Array.IndexOf(args, "--no-start") < 0) StartInstalled();
                            return 0;
                        }
                        using (var f = new InstallForm()) if (f.ShowDialog() == DialogResult.OK && f.StartAfter.Checked) StartInstalled();
                        return 0;
                    }
                    bool remove = Array.IndexOf(args, "--remove-data") >= 0;
                    if (!quiet)
                        using (var f = new UninstallForm()) { if (f.ShowDialog() != DialogResult.OK) return 1; remove = f.RemoveData.Checked; }
                    var log = Engine.Installer.Uninstall(remove);
                    if (!quiet) Msg.Show("CleanSweep was uninstalled.\n\n- " + string.Join("\n- ", log), "CleanSweep", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return 0;
                }
                catch (Exception e)
                {
                    Trace.Write("Install/uninstall failed: " + e);
                    if (!quiet) Msg.Show("Something went wrong:\n\n" + e.Message, "CleanSweep", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return 2;
                }
            }
            string testOut = null;
            for (int i = 0; i < args.Length; i++) if (args[i] == "--selftest" && i + 1 < args.Length) testOut = args[i + 1];

            // One window at a time (the self-test runs on its own)
            Startup();
            Msg.Test = testOut != null;
            Settings.Load();

            // Started from Downloads (not the installed copy): offer to install or update - never during tests
            if (testOut == null && args.Length == 0 && !Engine.Installer.RunningInstalled)
            {
                bool ask = !Engine.Installer.IsInstalled ? Settings.GetString("InstallPrompt") != "never" : Engine.Installer.Newer;
                if (ask)
                    using (var f = new InstallForm())
                        if (f.ShowDialog() == DialogResult.OK)
                        {
                            if (f.StartAfter.Checked) { StartInstalled(); }
                            return 0;
                        }
            }

            if (testOut == null)
            {
                Engine.Updater.CleanDownloads();
                // keep the automatic cleanup's own copy in step with this version after an update
                try { var st = Engine.AutoClean.State(); if (st.Exists && !st.RunsPowerShellEdition && Engine.Installer.RunningInstalled) Engine.AutoClean.InstallRunner(); } catch { }
            }

            using (var single = new Mutex(true, testOut == null ? "CleanSweep.CSharp.Window" : "CleanSweep.CSharp.Test", out bool first))
            {
                if (!first) { Msg.Show("CleanSweep is already open."); return 0; }
                Form = new MainForm();
                var dash = new DashboardPage();
                Form.Build(new Page[] { dash, new CleanupPage(), new LargeFilesPage(), new DrivesPage(), new RepairPage(), new ShortcutsPage(), new NetworkPage(), new RegistryPage(), new AboutPage() });
                Form.FormClosing += (s, e) =>
                {
                    if (!Pages.ToolLock.Busy || Msg.Test || e.CloseReason != CloseReason.UserClosing) return;
                    if (Msg.Show($"\"{Pages.ToolLock.Running}\" is still running. Stop it and close CleanSweep?", "CleanSweep", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) e.Cancel = true;
                    else { Form.Page<DrivesPage>()?.Stop.PerformClick(); Form.Page<RepairPage>()?.Stop.PerformClick(); }
                };
                // First health check runs automatically when the window opens (read-only)
                if (testOut == null) Form.Shown += async (s, e) =>
                {
                    if (Engine.Updater.Due) _ = System.Threading.Tasks.Task.Run(() => { try { Engine.Updater.Check(); } catch (Exception x) { Trace.Write("Update check failed: " + x.Message); } });
                    await dash.RunCheckAsync();
                };

                if (testOut != null) return SelfTest.Run(Form, testOut);
                Application.Run(Form);
                return 0;
            }
        }

        static bool started;
        static void Startup()
        {
            if (started) return; started = true;
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.ThreadException += (s, e) => Crash(e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (s, e) => Crash(e.ExceptionObject as Exception);
            Native.DarkApp();
            try { AppIcon = new Icon(Assembly.GetExecutingAssembly().GetManifestResourceStream("CleanSweep.ico")); } catch { AppIcon = SystemIcons.Application; }
        }
        /// <summary>Opens the installed copy and lets this one exit.</summary>
        public static void StartInstalled()
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Engine.Installer.Exe) { UseShellExecute = true, WorkingDirectory = Engine.Installer.Dir })?.Dispose(); }
            catch (Exception e) { Trace.Write("Could not start installed copy: " + e.Message); }
        }

        static void Crash(Exception e)
        {
            Trace.Write("ERROR: " + e);
            if (Msg.Test) { Console.Error.WriteLine("UNHANDLED: " + e); return; }
            Msg.Show("CleanSweep hit an error:\n\n" + e?.Message + "\n\nDetails were saved to " + Trace.File, "CleanSweep", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
