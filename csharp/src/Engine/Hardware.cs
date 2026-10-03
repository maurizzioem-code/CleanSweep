using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32;

namespace CleanSweep.Engine
{
    /// <summary>One reading of the live hardware counters. Null means "not available on this PC".</summary>
    public class HwSample
    {
        public int? Cpu, CpuPerf, DiskBusy, Gpu, Battery, BatteryMinutes;
        public double RamTotal, RamFree, Commit, DiskRead, DiskWrite, SysFree, SysSize;
        public double? NetDown, NetUp, GpuMem;
        public bool OnAC;
        public List<string> Temps;   // only every ~30 s
    }

    /// <summary>Details that don't change while CleanSweep runs.</summary>
    public class HwStatic
    {
        public string Cpu = "", Cores = "", Gpu = "", GpuDriver = "", RamInfo = "", Os = "Windows", Model = "", Computer = Environment.MachineName;
        public int MaxMhz;
        public DateTime? Boot;

        public static HwStatic Read()
        {
            var s = new HwStatic();
            try
            {
                var p = Wmi.First("SELECT Name, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed FROM Win32_Processor");
                s.Cpu = Regex.Replace(Regex.Replace(Wmi.Str(p, "Name"), @"\(R\)|\(TM\)|CPU|Processor|\s+@.*$", ""), @"\s+", " ").Trim();
                s.Cores = $"{Wmi.Long(p, "NumberOfCores")} cores, {Wmi.Long(p, "NumberOfLogicalProcessors")} threads"; s.MaxMhz = (int)Wmi.Long(p, "MaxClockSpeed");
            }
            catch { }
            try
            {
                var g = Wmi.TryQuery("SELECT Name, DriverVersion FROM Win32_VideoController").Where(v => !Regex.IsMatch(Wmi.Str(v, "Name"), "Basic Display|Remote|Hyper-V|Mirror")).ToList();
                s.Gpu = g.Count > 0 ? Wmi.Str(g[0], "Name") : "Basic display adapter"; s.GpuDriver = g.Count > 0 ? "Driver " + Wmi.Str(g[0], "DriverVersion") : "";
            }
            catch { }
            try
            {
                var m = Wmi.TryQuery("SELECT Speed FROM Win32_PhysicalMemory"); long spd = m.Select(x => Wmi.Long(x, "Speed")).DefaultIfEmpty(0).Max();
                s.RamInfo = $"{m.Count} module(s)" + (spd > 0 ? $", {spd} MT/s" : "");
            }
            catch { }
            try
            {
                var os = Wmi.First("SELECT Caption, LastBootUpTime FROM Win32_OperatingSystem");
                string cap = Regex.Replace(Wmi.Str(os, "Caption"), "^Microsoft ", "");
                string dv = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion")?.GetValue("DisplayVersion") as string ?? "";
                s.Os = (cap + " " + dv).Trim(); s.Boot = Wmi.Date(os, "LastBootUpTime");
            }
            catch { }
            try
            {
                var cs = Wmi.First("SELECT Manufacturer, Model FROM Win32_ComputerSystem");
                s.Model = (Wmi.Str(cs, "Manufacturer") + " " + Wmi.Str(cs, "Model")).Replace("System manufacturer System Product Name", "PC").Trim();
            }
            catch { }
            return s;
        }
    }

    /// <summary>
    /// Background sampler: reads counters off the UI thread every 1.5 s so the window never stutters.
    /// Pauses itself while the Dashboard isn't visible (set Active).
    /// </summary>
    public class HardwareMonitor : IDisposable
    {
        public volatile bool Active = true;
        public event Action<HwSample> Sampled;
        readonly Thread thread; volatile bool run = true; int tick;
        public int IntervalMs = 1500;

        public HardwareMonitor()
        {
            thread = new Thread(Loop) { IsBackground = true, Name = "CleanSweep hardware monitor" };
            thread.SetApartmentState(ApartmentState.MTA);
        }
        public void Start() => thread.Start();
        public void Dispose() { run = false; }

        void Loop()
        {
            while (run)
            {
                if (Active)
                {
                    try { Sampled?.Invoke(Sample(tick++)); } catch (Exception e) { Trace.Write("Hardware sample: " + e.Message); }
                }
                for (int i = 0; i < IntervalMs / 100 && run; i++) Thread.Sleep(100);
            }
        }

        public static HwSample Sample(int tick)
        {
            var r = new HwSample();
            try { r.Cpu = (int)Wmi.Long(Wmi.Query("SELECT PercentProcessorTime FROM Win32_PerfFormattedData_PerfOS_Processor WHERE Name='_Total'")[0], "PercentProcessorTime"); } catch { }
            try { r.CpuPerf = (int)Wmi.Long(Wmi.Query("SELECT PercentProcessorPerformance FROM Win32_PerfFormattedData_Counters_ProcessorInformation WHERE Name='_Total'")[0], "PercentProcessorPerformance"); } catch { }
            try
            {
                var m = Health.MemoryStatus(); r.RamTotal = m.ullTotalPhys; r.RamFree = m.ullAvailPhys;
                r.Commit = m.ullTotalPageFile - m.ullAvailPageFile;
            }
            catch { }
            try
            {
                var d = Wmi.Query("SELECT PercentIdleTime, DiskReadBytesPersec, DiskWriteBytesPersec FROM Win32_PerfFormattedData_PerfDisk_PhysicalDisk WHERE Name='_Total'")[0];
                r.DiskBusy = (int)Math.Max(0, Math.Min(100, 100 - Wmi.Long(d, "PercentIdleTime"))); r.DiskRead = Wmi.Long(d, "DiskReadBytesPersec"); r.DiskWrite = Wmi.Long(d, "DiskWriteBytesPersec");
            }
            catch { }
            try { var c = new DriveInfo(AppPaths.SystemDrive); r.SysFree = c.AvailableFreeSpace; r.SysSize = c.TotalSize; } catch { }
            try
            {
                var n = Wmi.Query("SELECT Name, BytesReceivedPersec, BytesSentPersec FROM Win32_PerfFormattedData_Tcpip_NetworkInterface")
                    .Where(x => !Regex.IsMatch(Wmi.Str(x, "Name"), "isatap|Teredo|Loopback|vEthernet", RegexOptions.IgnoreCase)).ToList();
                r.NetDown = n.Sum(x => (double)Wmi.Long(x, "BytesReceivedPersec")); r.NetUp = n.Sum(x => (double)Wmi.Long(x, "BytesSentPersec"));
            }
            catch { }
            try
            {
                var g = Wmi.Query("SELECT UtilizationPercentage FROM Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine WHERE Name LIKE '%engtype_3D%'");
                if (g.Count > 0) r.Gpu = (int)Math.Min(100, g.Sum(x => Wmi.Long(x, "UtilizationPercentage")));
            }
            catch { }
            try
            {
                var gm = Wmi.Query("SELECT DedicatedUsage FROM Win32_PerfFormattedData_GPUPerformanceCounters_GPUAdapterMemory");
                if (gm.Count > 0) r.GpuMem = gm.Sum(x => (double)Wmi.Long(x, "DedicatedUsage"));
            }
            catch { }
            try
            {
                var b = Wmi.First("SELECT EstimatedChargeRemaining, BatteryStatus, EstimatedRunTime FROM Win32_Battery");
                if (b != null)
                {
                    r.Battery = (int)Wmi.Long(b, "EstimatedChargeRemaining"); long st = Wmi.Long(b, "BatteryStatus");
                    r.OnAC = st == 2 || (st >= 6 && st <= 9);
                    long rt = Wmi.Long(b, "EstimatedRunTime"); if (rt > 0 && rt < 71582788) r.BatteryMinutes = (int)rt;
                }
            }
            catch { }
            // Temperatures every ~30 s (drive sensors are slow to read)
            if (tick % 20 == 0) r.Temps = Temperatures();
            return r;
        }

        public static List<string> Temperatures()
        {
            var temps = new List<string>();
            foreach (var s in Wmi.TryQuery("SELECT Name, SensorType, Value FROM Sensor", @"root\LibreHardwareMonitor", 5)
                         .Where(x => Wmi.Str(x, "SensorType") == "Temperature" && Regex.IsMatch(Wmi.Str(x, "Name"), @"CPU Package|Core \(Tctl|GPU Core")))
                temps.Add($"{(Wmi.Str(s, "Name").Contains("GPU") ? "GPU" : "CPU")} {Convert.ToDouble(Wmi.Prop(s, "Value")):N0} \u00B0C");
            if (!temps.Any(t => t.StartsWith("CPU")))
            {
                var tz = Wmi.TryQuery("SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature", @"root\wmi", 5)
                    .Select(x => Wmi.Long(x, "CurrentTemperature") / 10.0 - 273.15).Where(t => t > 15 && t < 110).ToList();
                if (tz.Count > 0) temps.Add($"CPU zone {tz.Max():N0} \u00B0C");
            }
            foreach (var pd in Wmi.TryQuery("SELECT * FROM MSFT_PhysicalDisk", @"root\Microsoft\Windows\Storage", 5))
            {
                try { foreach (ManagementBaseObject rc in pd.GetRelated("MSFT_StorageReliabilityCounter")) { long t = Wmi.Long(rc, "Temperature"); if (t > 0) temps.Add($"Drive {Wmi.Str(pd, "DeviceId")} {t} \u00B0C"); break; } } catch { }
            }
            return temps;
        }
    }

    /// <summary>System Restore points (Windows desktop editions only).</summary>
    public static class RestorePoint
    {
        const string Key = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore";

        static long LastSequence() => Wmi.TryQuery("SELECT SequenceNumber FROM SystemRestore", @"root\default").Select(o => Wmi.Long(o, "SequenceNumber")).DefaultIfEmpty(0).Max();

        /// <summary>Creates a restore point. Returns false if System Restore is off or unavailable.</summary>
        public static bool Create(string description)
        {
            ManagementClass sr;
            try { sr = new ManagementClass(new ManagementScope(@"root\default"), new ManagementPath("SystemRestore"), null); sr.Get(); }
            catch { return false; }   // not available (e.g. Windows Server)
            long before = LastSequence();
            object old = null; RegistryKey k = null;
            try
            {
                // Windows normally allows only one restore point per 24 hours; lift that limit just for this call
                k = Registry.LocalMachine.OpenSubKey(Key, true); old = k?.GetValue("SystemRestorePointCreationFrequency");
                k?.SetValue("SystemRestorePointCreationFrequency", 0, RegistryValueKind.DWord);
                var inp = sr.GetMethodParameters("CreateRestorePoint");
                inp["Description"] = description; inp["RestorePointType"] = 12 /* MODIFY_SETTINGS */; inp["EventType"] = 100 /* BEGIN_SYSTEM_CHANGE */;
                sr.InvokeMethod("CreateRestorePoint", inp, null);
            }
            catch (Exception e) { Trace.Write("Restore point: " + e.Message); }
            finally
            {
                try { if (old == null) k?.DeleteValue("SystemRestorePointCreationFrequency", false); else k?.SetValue("SystemRestorePointCreationFrequency", old, RegistryValueKind.DWord); } catch { }
                k?.Dispose();
            }
            long after = LastSequence();
            return after > 0 && after != before;
        }
    }
}
