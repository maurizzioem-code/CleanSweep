using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace CleanSweep
{
    /// <summary>
    /// settings.json, shared with the PowerShell edition (same file, same key names).
    /// Unknown keys are kept as they are, so neither edition loses the other's settings.
    /// </summary>
    public static class Settings
    {
        static Dictionary<string, object> data = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
        public static string FilePath = AppPaths.Settings;

        public static void Load()
        {
            data = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!File.Exists(FilePath)) return;
                var d = Json.DeserializeObject(File.ReadAllText(FilePath)) as Dictionary<string, object>;
                if (d != null) foreach (var kv in d) data[kv.Key] = kv.Value;
            }
            catch (Exception e) { Trace.Write("Settings could not be read: " + e.Message); }
        }

        public static void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                string tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, Pretty(Json.Serialize(data)), new UTF8Encoding(false));
                if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null); else File.Move(tmp, FilePath);
            }
            catch (Exception e) { Trace.Write("Settings could not be saved: " + e.Message); }
        }

        public static bool Has(string key) => data.ContainsKey(key) && data[key] != null;
        public static object Get(string key) => data.TryGetValue(key, out var v) ? v : null;
        public static void Set(string key, object value) { data[key] = value; }
        public static void Remove(string key) { data.Remove(key); }

        public static string GetString(string key, string def = "")
        {
            var v = Get(key); return v == null ? def : (Convert.ToString(v) is string s && s.Length > 0 ? s : def);
        }
        public static int GetInt(string key, int def = 0)
        {
            var v = Get(key); if (v == null) return def;
            try { return Convert.ToInt32(v); } catch { return def; }
        }
        public static bool GetBool(string key, bool def = false)
        {
            var v = Get(key); if (v == null) return def;
            if (v is bool b) return b;
            return bool.TryParse(Convert.ToString(v), out var r) ? r : def;
        }
        public static List<string> GetList(string key)
        {
            var v = Get(key);
            if (v is string one) return one.Length > 0 ? new List<string> { one } : new List<string>();
            if (v is IEnumerable e) return e.Cast<object>().Where(x => x != null).Select(x => x.ToString()).Where(x => x.Length > 0).ToList();
            return new List<string>();
        }
        public static void SetList(string key, IEnumerable<string> list) { data[key] = list.Distinct().ToArray(); }

        // JavaScriptSerializer writes one long line; indent it so the file stays readable
        static string Pretty(string json)
        {
            var sb = new StringBuilder(); int ind = 0; bool str = false;
            for (int i = 0; i < json.Length; i++)
            {
                char c = json[i];
                if (str) { sb.Append(c); if (c == '\\' && i + 1 < json.Length) sb.Append(json[++i]); else if (c == '"') str = false; continue; }
                switch (c)
                {
                    case '"': str = true; sb.Append(c); break;
                    case '{': case '[':
                        sb.Append(c);
                        if (i + 1 < json.Length && (json[i + 1] == '}' || json[i + 1] == ']')) { sb.Append(json[++i]); break; }
                        sb.AppendLine(); sb.Append(' ', 4 * ++ind); break;
                    case '}': case ']': sb.AppendLine(); sb.Append(' ', 4 * --ind); sb.Append(c); break;
                    case ',': sb.Append(c); sb.AppendLine(); sb.Append(' ', 4 * ind); break;
                    case ':': sb.Append(": "); break;
                    default: sb.Append(c); break;
                }
            }
            return sb.ToString();
        }
    }
}
