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

        public static string GetAttribute(RhinoObject obj, string key)
        {
            if (obj == null || string.IsNullOrEmpty(key)) return null;
            string val = obj.Attributes.GetUserString(key);
            return string.IsNullOrEmpty(val) ? null : val.Trim();
        }

        public static List<string> GetAttributeList(RhinoObject obj, string baseKey)
        {
            List<string> list = new List<string>();
            if (obj == null || string.IsNullOrEmpty(baseKey)) return list;

            string single = obj.Attributes.GetUserString(baseKey);
            if (!string.IsNullOrEmpty(single)) list.Add(single.Trim());

            var userStrings = obj.Attributes.GetUserStrings();
            string prefix = baseKey + ".";
            for (int i = 0; i < userStrings.Count; i++)
            {
                string k = userStrings.GetKey(i);
                if (k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    string v = userStrings.Get(k);
                    if (!string.IsNullOrEmpty(v)) list.Add(v.Trim());
                }
            }

            return list;
        }

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
