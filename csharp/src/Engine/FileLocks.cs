using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace CleanSweep.Engine
{
    /// <summary>Which apps hold a file open (Windows Restart Manager, read-only query) and delete-at-restart.</summary>
    public static class FileLocks
    {
        [StructLayout(LayoutKind.Sequential)]
        struct RM_UNIQUE_PROCESS { public int dwProcessId; public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct RM_PROCESS_INFO
        {
            public RM_UNIQUE_PROCESS Process;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string strAppName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string strServiceShortName;
            public int ApplicationType; public uint AppStatus; public uint TSSessionId;
            [MarshalAs(UnmanagedType.Bool)] public bool bRestartable;
        }

        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)] static extern int RmStartSession(out uint h, int flags, StringBuilder key);
        [DllImport("rstrtmgr.dll")] static extern int RmEndSession(uint h);
        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)] static extern int RmRegisterResources(uint h, uint nFiles, string[] files, uint nApps, RM_UNIQUE_PROCESS[] apps, uint nSvc, string[] svcs);
        [DllImport("rstrtmgr.dll")] static extern int RmGetList(uint h, out uint needed, ref uint n, [In, Out] RM_PROCESS_INFO[] info, ref uint reasons);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool MoveFileEx(string from, string to, int flags);

        /// <summary>"App name (PID 1234), ..." or "" if nothing is known.</summary>
        public static string Who(string path)
        {
            var key = new StringBuilder(64);
            if (RmStartSession(out uint h, 0, key) != 0) return "";
            try
            {
                if (RmRegisterResources(h, 1, new[] { path }, 0, null, 0, null) != 0) return "";
                uint n = 0, reasons = 0;
                int r = RmGetList(h, out uint needed, ref n, null, ref reasons);
                if (r != 234 || needed == 0) return "";
                var info = new RM_PROCESS_INFO[needed]; n = needed;
                if (RmGetList(h, out needed, ref n, info, ref reasons) != 0) return "";
                var names = new List<string>();
                for (int i = 0; i < n; i++)
                {
                    string s = string.IsNullOrEmpty(info[i].strAppName) ? "Process" : info[i].strAppName;
                    s += " (PID " + info[i].Process.dwProcessId + ")";
                    if (!names.Contains(s)) names.Add(s);
                }
                return string.Join(", ", names);
            }
            finally { RmEndSession(h); }
        }

        /// <summary>Asks Windows to delete the file during the next restart, before apps can lock it (needs admin).</summary>
        public static bool DeleteAtRestart(string path) => MoveFileEx(path, null, 4 /* MOVEFILE_DELAY_UNTIL_REBOOT */);
    }
}
