using System;
using System.Collections.Generic;
using Rhino.DocObjects;

namespace AxisBridge.Attributes
{
    /// <summary>
    /// Reads and parses Ax_* attributes from Rhino object User Text.
    /// </summary>
    public static class AttrReader
    {
        /// <summary>All Ax_* user strings of an object (case-insensitive keys).</summary>
        public static Dictionary<string, string> GetAxAttributes(RhinoObject obj)
        {
            Dictionary<string, string> dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (obj == null) return dict;

            var userStrings = obj.Attributes.GetUserStrings();
            for (int i = 0; i < userStrings.Count; i++)
            {
                string key = userStrings.GetKey(i);
                if (key.StartsWith(AxKeys.Prefix, StringComparison.OrdinalIgnoreCase))
                {
                    dict[key] = userStrings.Get(key);
                }
            }

            return dict;
        }

        /// <summary>One attribute, trimmed, or null when absent/empty.</summary>
        public static string GetAttribute(RhinoObject obj, string key)
        {
            if (obj == null || string.IsNullOrEmpty(key)) return null;
            string val = obj.Attributes.GetUserString(key);
            return string.IsNullOrEmpty(val) ? null : val.Trim();
        }

        /// <summary>Every value of a repeatable key, in suffix order (bare key first, then .1, .2, ...).</summary>
        public static List<string> GetAttributeList(RhinoObject obj, string baseKey)
        {
            return GetFamily(GetAxAttributes(obj), baseKey);
        }

        /// <summary>
        /// Every value of a repeatable key from an attribute dictionary, sorted by numeric suffix
        /// (so ".10" comes after ".9"). "-" and empty values are skipped.
        /// </summary>
        public static List<string> GetFamily(IDictionary<string, string> attrs, string baseKey)
        {
            var hits = new List<KeyValuePair<int, string>>();
            if (attrs != null)
            {
                foreach (var kv in attrs)
                {
                    if (!string.Equals(AxKeys.BaseKey(kv.Key), baseKey, StringComparison.OrdinalIgnoreCase)) continue;
                    string v = kv.Value == null ? "" : kv.Value.Trim();
                    if (v.Length == 0 || v == "-") continue;
                    hits.Add(new KeyValuePair<int, string>(AxKeys.SuffixOf(kv.Key), v));
                }
            }
            hits.Sort((a, b) => a.Key.CompareTo(b.Key));

            var list = new List<string>();
            foreach (var h in hits) list.Add(h.Value);
            return list;
        }

        /// <summary>Splits "LC=Dead; F=0,0,-10" into named fields (case-insensitive).</summary>
        public static Dictionary<string, string> ParseFields(string text)
        {
            Dictionary<string, string> dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(text)) return dict;

            string[] tokens = text.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string t in tokens)
            {
                int eq = t.IndexOf('=');
                if (eq > 0)
                {
                    string k = t.Substring(0, eq).Trim();
                    string v = t.Substring(eq + 1).Trim();
                    dict[k] = v;
                }
            }

            return dict;
        }
    }
}
