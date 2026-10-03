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
        public const string VersionText = "5.0 preview";
        public static Icon AppIcon;
        public static MainForm Form;

        [STAThread]
        static int Main(string[] args)
        {
            string testOut = null;
            for (int i = 0; i < args.Length; i++) if (args[i] == "--selftest" && i + 1 < args.Length) testOut = args[i + 1];

            // One window at a time (the self-test runs on its own)
            using (var single = new Mutex(true, testOut == null ? "CleanSweep.CSharp.Window" : "CleanSweep.CSharp.Test", out bool first))
            {
                if (!first) { Msg.Show("CleanSweep is already open."); return 0; }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.ThreadException += (s, e) => Crash(e.Exception);
                AppDomain.CurrentDomain.UnhandledException += (s, e) => Crash(e.ExceptionObject as Exception);
                Native.DarkApp();
                try { AppIcon = new Icon(Assembly.GetExecutingAssembly().GetManifestResourceStream("CleanSweep.ico")); } catch { AppIcon = SystemIcons.Application; }

                Msg.Test = testOut != null;
                Settings.Load();
                Form = new MainForm();
                Form.Build(new Page[] { new CleanupPage(), new AboutPage() });

                if (testOut != null) return SelfTest.Run(Form, testOut);
                Application.Run(Form);
                return 0;
            }
        }

        static void Crash(Exception e)
        {
            Trace.Write("ERROR: " + e);
            if (Msg.Test) { Console.Error.WriteLine("UNHANDLED: " + e); return; }
            Msg.Show("CleanSweep hit an error:\n\n" + e?.Message + "\n\nDetails were saved to " + Trace.File, "CleanSweep", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
