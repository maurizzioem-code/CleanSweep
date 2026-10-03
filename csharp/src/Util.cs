using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;

namespace CleanSweep
{
    /// <summary>Where CleanSweep keeps its files. Same folder as the PowerShell edition so settings carry over.</summary>
    public static class AppPaths
    {
        public static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CleanSweep");
        public static readonly string Settings = Path.Combine(Dir, "settings.json");
        public static readonly string Logs = Path.Combine(Dir, "logs");
        public static string SystemDrive => Environment.GetEnvironmentVariable("SystemDrive")?.TrimEnd('\\') ?? "C:";
    }

    public static class Fmt
    {
        /// <summary>Human-readable size: 1.23 GB, 45.6 MB, 789 KB.</summary>
        public static string Size(long b)
        {
            if (b >= 1L << 30) return (b / (double)(1L << 30)).ToString("N2") + " GB";
            if (b >= 1L << 20) return (b / (double)(1L << 20)).ToString("N1") + " MB";
            return (b / 1024.0).ToString("N0") + " KB";
        }
        public static string Count(long n) => n.ToString("N0");
        public static string Files(long n) => n == 1 ? "1 file" : n.ToString("N0") + " files";
    }

    /// <summary>All message boxes go through here so the self-test can answer them and record what was asked.</summary>
    public static class Msg
    {
        public static bool Test;
        public static DialogResult TestAnswer = DialogResult.Yes;
        public static readonly List<string> Log = new List<string>();

        public static DialogResult Show(string text, string caption = "CleanSweep",
            MessageBoxButtons buttons = MessageBoxButtons.OK, MessageBoxIcon icon = MessageBoxIcon.None)
        {
            if (Test)
            {
                Log.Add("[" + caption + "] " + text.Replace("\r", " ").Replace("\n", " "));
                if (buttons == MessageBoxButtons.OK) return DialogResult.OK;
                return TestAnswer;
            }
            return MessageBox.Show(Form.ActiveForm, text, caption, buttons, icon);
        }
        public static bool Confirm(string text, string caption = "Confirm") =>
            Show(text, caption, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes;
    }

    public static class Shell
    {
        public static void Open(string target, string args = null)
        {
            try { Process.Start(new ProcessStartInfo(target, args ?? "") { UseShellExecute = true }); } catch { }
        }
        public static void ShowInExplorer(string path) => Open("explorer.exe", "/select,\"" + path + "\"");
    }

    /// <summary>Simple text log for diagnostics (logs\app.log).</summary>
    public static class Trace
    {
        static readonly object Gate = new object();
        public static string File = Path.Combine(AppPaths.Logs, "app.log");
        public static void Write(string text)
        {
            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(File));
                    System.IO.File.AppendAllText(File, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + text + Environment.NewLine);
                }
            }
            catch { }
        }
    }
}
