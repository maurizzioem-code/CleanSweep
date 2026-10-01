// CleanSweep.exe - installs/updates the CleanSweep scripts and starts the app with no console window.
using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using System.Security.Principal;

[assembly: AssemblyTitle("CleanSweep")]
[assembly: AssemblyProduct("CleanSweep")]
[assembly: AssemblyDescription("PC cleaner and network optimizer")]
[assembly: AssemblyCompany("CleanSweep")]
[assembly: AssemblyVersion(CleanSweep.Launcher.FileVersion)]
[assembly: AssemblyFileVersion(CleanSweep.Launcher.FileVersion)]

namespace CleanSweep {
  static class Launcher {
    public const string FileVersion = "__FILEVERSION__";
    const string AppVersion = "__APPVERSION__";

    static Version ParseVer(string s) { Version v; return Version.TryParse(s, out v) ? v : new Version(0, 0); }

    static string InstalledVersion(string ps1) {
      if (!File.Exists(ps1)) return null;
      var m = Regex.Match(File.ReadAllText(ps1), "^\\$Version = \"([^\"]+)\"", RegexOptions.Multiline);
      return m.Success ? m.Groups[1].Value : null;
    }

    static void MakeShortcut(string lnk, string target, string dir, string icon) {
      try {
        Type t = Type.GetTypeFromProgID("WScript.Shell");
        object sh = Activator.CreateInstance(t);
        object sc = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, sh, new object[] { lnk });
        Type st = sc.GetType();
        st.InvokeMember("TargetPath", BindingFlags.SetProperty, null, sc, new object[] { target });
        st.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, sc, new object[] { dir });
        st.InvokeMember("IconLocation", BindingFlags.SetProperty, null, sc, new object[] { icon + ",0" });
        st.InvokeMember("Description", BindingFlags.SetProperty, null, sc, new object[] { "CleanSweep" });
        st.InvokeMember("Save", BindingFlags.InvokeMethod, null, sc, null);
      } catch { }
    }

    [STAThread]
    static int Main(string[] args) {
      try {
        // Cleaning system folders needs admin rights; ask for them if the manifest didn't
        if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator)) {
          var up = new ProcessStartInfo(Assembly.GetExecutingAssembly().Location) { UseShellExecute = true, Verb = "runas" };
          try { Process.Start(up); } catch { }   // user pressed No on the admin prompt
          return 0;
        }
        string appDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CleanSweep");
        Directory.CreateDirectory(appDir);
        string ps1 = Path.Combine(appDir, "CleanSweep.ps1");
        string self = Assembly.GetExecutingAssembly().Location;
        string installedExe = Path.Combine(appDir, "CleanSweep.exe");

        // 1. Unpack the bundled app if it is missing or newer than what is installed
        string have = InstalledVersion(ps1);
        if (have == null || ParseVer(AppVersion) > ParseVer(have)) {
          using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("app.zip"))
          using (var zip = new ZipArchive(s, ZipArchiveMode.Read)) {
            foreach (var e in zip.Entries) {
              if (string.IsNullOrEmpty(e.Name)) continue;
              string rel = e.FullName.Replace('/', '\\');
              int cut = rel.IndexOf('\\'); if (cut >= 0) rel = rel.Substring(cut + 1);   // drop top "CleanSweep\" folder
              if (rel.Equals("settings.json", StringComparison.OrdinalIgnoreCase)) continue;
              string dest = Path.Combine(appDir, rel);
              Directory.CreateDirectory(Path.GetDirectoryName(dest));
              e.ExtractToFile(dest, true);
            }
          }
        }

        // 2. Keep a copy of this exe in the app folder and point the shortcuts at it
        if (!string.Equals(Path.GetFullPath(self), Path.GetFullPath(installedExe), StringComparison.OrdinalIgnoreCase)) {
          bool copy = !File.Exists(installedExe) || ParseVer(FileVersion) >= ParseVer(FileVersionInfo.GetVersionInfo(installedExe).FileVersion ?? "0.0");
          if (copy) { try { File.Copy(self, installedExe, true); } catch { } }
          string icon = Path.Combine(appDir, "CleanSweep.ico");
          string desk = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
          string menu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "CleanSweep.lnk");
          MakeShortcut(Path.Combine(desk, "CleanSweep.lnk"), installedExe, appDir, icon);
          MakeShortcut(menu, installedExe, appDir, icon);
        }

        // 3. Start the app (already elevated via the exe manifest) with no console window
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell\\v1.0\\powershell.exe"),
          "-NoProfile -ExecutionPolicy Bypass -STA -WindowStyle Hidden -File \"" + ps1 + "\"");
        psi.UseShellExecute = false; psi.CreateNoWindow = true; psi.WorkingDirectory = appDir;
        Process.Start(psi);
        return 0;
      } catch (Exception ex) {
        MessageBox.Show("CleanSweep could not start:\n\n" + ex.Message, "CleanSweep", MessageBoxButtons.OK, MessageBoxIcon.Error);
        return 1;
      }
    }
  }
}
