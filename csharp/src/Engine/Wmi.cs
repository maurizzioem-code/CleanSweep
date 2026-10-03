using System;
using System.Collections.Generic;
using System.Management;

namespace CleanSweep.Engine
{
    /// <summary>Small, safe wrapper around WMI queries (read-only). Every call has a timeout and never throws for missing data.</summary>
    public static class Wmi
    {
        public static List<ManagementObject> Query(string wql, string ns = @"root\cimv2", int timeoutSec = 15)
        {
            var list = new List<ManagementObject>();
            using (var s = new ManagementObjectSearcher(new ManagementScope(ns), new ObjectQuery(wql),
                       new EnumerationOptions { Timeout = TimeSpan.FromSeconds(timeoutSec), ReturnImmediately = true, Rewindable = false }))
            {
                foreach (ManagementObject o in s.Get()) list.Add(o);
            }
            return list;
        }

        /// <summary>Like Query, but returns an empty list if the namespace or class doesn't exist on this PC.</summary>
        public static List<ManagementObject> TryQuery(string wql, string ns = @"root\cimv2", int timeoutSec = 15)
        {
            try { return Query(wql, ns, timeoutSec); } catch { return new List<ManagementObject>(); }
        }

        public static ManagementObject First(string wql, string ns = @"root\cimv2")
        {
            var l = TryQuery(wql, ns); return l.Count > 0 ? l[0] : null;
        }

        public static object Prop(ManagementBaseObject o, string name)
        {
            try { return o?[name]; } catch { return null; }
        }
        public static string Str(ManagementBaseObject o, string name) => Prop(o, name)?.ToString() ?? "";
        public static long Long(ManagementBaseObject o, string name, long def = 0)
        {
            var v = Prop(o, name); if (v == null) return def;
            try { return Convert.ToInt64(v); } catch { return def; }
        }
        public static long? NLong(ManagementBaseObject o, string name)
        {
            var v = Prop(o, name); if (v == null) return null;
            try { return Convert.ToInt64(v); } catch { return null; }
        }
        public static bool Bool(ManagementBaseObject o, string name, bool def = false)
        {
            var v = Prop(o, name); if (v == null) return def;
            try { return Convert.ToBoolean(v); } catch { return def; }
        }
        public static DateTime? Date(ManagementBaseObject o, string name)
        {
            var s = Str(o, name); if (s.Length == 0) return null;
            try { return ManagementDateTimeConverter.ToDateTime(s); } catch { return null; }
        }
    }
}
