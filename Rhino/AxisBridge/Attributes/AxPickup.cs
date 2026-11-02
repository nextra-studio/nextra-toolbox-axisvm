using System;
using System.Collections.Generic;
using Rhino;
using Rhino.DocObjects;

namespace AxisBridge.Attributes
{
    /// <summary>
    /// AxPickup: copy chosen Ax_* properties from one object to others of the same geometry kind (curve -> curves,
    /// surface -> surfaces, point -> points). Properties are offered in groups - element type, section, material,
    /// thickness, loads ... - and a copied group replaces the target's whole group (all loads of the source replace all
    /// loads of the target), so nothing of the old value is left mixed in.
    /// </summary>
    public static class AxPickup
    {
        public class Group
        {
            public string Name;
            /// <summary>Exact keys, and repeatable families (Ax_DistLoad, Ax_DistLoad.1 ...) by base key.</summary>
            public string[] Keys;
            public string[] Families = new string[0];

            public bool Owns(string key)
            {
                string b = AxKeys.BaseKey(key);
                foreach (string f in Families) if (string.Equals(f, b, StringComparison.OrdinalIgnoreCase)) return true;
                foreach (string k in Keys) if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase)) return true;
                return false;
            }
        }

        private static Group G(string name, string[] keys, params string[] families)
        {
            return new Group { Name = name, Keys = keys, Families = families };
        }

        /// <summary>The property groups that make sense for a geometry kind, in display order.</summary>
        public static List<Group> GroupsFor(AxGeomKind kind)
        {
            switch (kind)
            {
                case AxGeomKind.Line:
                    return new List<Group>
                    {
                        G("Element type", new[] { AxKeys.Type }),
                        G("Cross-section", new[] { AxKeys.Section }),
                        G("Material", new[] { AxKeys.Material }),
                        G("End releases", new[] { AxKeys.Release }),
                        G("Eccentricity", new[] { AxKeys.Eccentricity }),
                        G("Local axis", new[] { AxKeys.LocalAxis }),
                        G("Loads", new string[0], AxKeys.DistLoad, AxKeys.PointLoad),
                        G("Line support", new[] { AxKeys.Support }),
                        G("Spring / link stiffness", new[] { AxKeys.Stiffness, AxKeys.CoupledDof }),
                        G("Edge hinge slabs", new[] { AxKeys.HingeDomains }),
                        G("Rigid diaphragm", new[] { AxKeys.Diaphragm }),
                        G("Parts", new[] { AxKeys.Parts }),
                    };
                case AxGeomKind.Domain:
                    return new List<Group>
                    {
                        G("Element type", new[] { AxKeys.FeType }),
                        G("Thickness", new[] { AxKeys.Thickness }),
                        G("Material", new[] { AxKeys.Material }),
                        G("Local axis", new[] { AxKeys.LocalAxis }),
                        G("Loads", new string[0], AxKeys.DistLoad, AxKeys.PointLoad),
                        G("Span direction (one-way panel)", new[] { AxKeys.SpanDirection, AxKeys.SpanTolerance, AxKeys.EdgeSpacing }),
                        G("Load targets (two-way panel)", new[] { AxKeys.LoadTargets }),
                        G("Rigid diaphragm", new[] { AxKeys.Diaphragm }),
                        G("Parts", new[] { AxKeys.Parts }),
                    };
                case AxGeomKind.Point:
                    return new List<Group>
                    {
                        G("Support", new[] { AxKeys.Support }),
                        G("Nodal loads", new string[0], AxKeys.NodalLoad),
                        G("Parts", new[] { AxKeys.Parts }),
                    };
                default:
                    return new List<Group>();
            }
        }

        /// <summary>The source's values in a group, key -> value (empty when the source has none).</summary>
        public static Dictionary<string, string> ValuesOf(Dictionary<string, string> attrs, Group g)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, string> kv in attrs)
                if (g.Owns(kv.Key) && !string.IsNullOrWhiteSpace(kv.Value)) d[kv.Key] = kv.Value;
            return d;
        }

        /// <summary>One line for the dialog: "IPE 300", "3 loads: Dead, Live, Snow", "(none - clears the target)".</summary>
        public static string Describe(Dictionary<string, string> values, Group g)
        {
            if (values.Count == 0) return "(none - copying clears it on the targets)";
            if (g.Families.Length > 0)
            {
                var cases = new List<string>();
                foreach (string v in values.Values)
                {
                    string lc = Field(v, "LC");
                    if (lc != null && !cases.Contains(lc)) cases.Add(lc);
                }
                return values.Count + " load(s)" + (cases.Count > 0 ? ": " + string.Join(", ", cases) : "");
            }
            var parts = new List<string>();
            foreach (string k in g.Keys)
            {
                string v;
                if (values.TryGetValue(k, out v)) parts.Add(g.Keys.Length > 1 ? k.Replace(AxKeys.Prefix, "") + "=" + v : v);
            }
            string s = string.Join("  ", parts);
            return s.Length > 70 ? s.Substring(0, 70) + "..." : s;
        }

        private static string Field(string v, string name)
        {
            foreach (string f in v.Split(';'))
            {
                int eq = f.IndexOf('=');
                if (eq > 0 && string.Equals(f.Substring(0, eq).Trim(), name, StringComparison.OrdinalIgnoreCase)) return f.Substring(eq + 1).Trim();
            }
            return null;
        }

        /// <summary>
        /// Copies the chosen groups of the source to every target: each group is cleared on the target, then the source's
        /// keys of that group are written; Ax_Guid is re-stamped with the target's own id. Returns the objects changed.
        /// </summary>
        public static int Apply(RhinoDoc doc, RhinoObject source, IEnumerable<RhinoObject> targets, List<Group> groups)
        {
            Dictionary<string, string> src = AttrReader.GetAxAttributes(source);
            int changed = 0;
            foreach (RhinoObject t in targets)
            {
                if (t == null || t.Id == source.Id) continue;
                ObjectAttributes a = t.Attributes.Duplicate();
                var existing = a.GetUserStrings();
                foreach (string k in existing.AllKeys)
                    if (k != null && groups.Exists(g => g.Owns(k))) a.DeleteUserString(k);
                foreach (Group g in groups)
                    foreach (KeyValuePair<string, string> kv in ValuesOf(src, g)) a.SetUserString(kv.Key, kv.Value);
                a.SetUserString(AxKeys.Guid, t.Id.ToString());
                if (doc.Objects.ModifyAttributes(t, a, true)) changed++;
            }
            return changed;
        }
    }
}
