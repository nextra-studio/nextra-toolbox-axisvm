using System;
using System.Collections.Generic;
using Rhino.DocObjects;

namespace AxisBridge.Attributes
{
    /// <summary>How one attribute looks across the whole selection.</summary>
    public enum FieldStateKind
    {
        /// <summary>No selected object has the key - the dialog shows (and writes) its default.</summary>
        AbsentAll,
        /// <summary>Every selected object has the same value.</summary>
        Same,
        /// <summary>The objects disagree (or some have it and some do not) - shown as "varies", kept unless edited.</summary>
        Varies
    }

    public struct FieldState
    {
        public FieldStateKind Kind;
        public string Value;   // only meaningful for Same

        public static FieldState Absent { get { return new FieldState { Kind = FieldStateKind.AbsentAll }; } }
        public static FieldState Mixed { get { return new FieldState { Kind = FieldStateKind.Varies }; } }
        public static FieldState Of(string v) { return new FieldState { Kind = FieldStateKind.Same, Value = v }; }
    }

    /// <summary>
    /// Summary of the Ax_* attributes over a multi-selection. The dialogs are built from this, so
    /// selecting ten beams with different sections shows "varies" for the section instead of
    /// silently showing (and later writing) the first beam's value onto all ten.
    /// </summary>
    public class SelectionState
    {
        public int Count;
        public readonly Dictionary<string, FieldState> Scalars = new Dictionary<string, FieldState>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Load lists per family key; Varies when the objects carry different load lists.</summary>
        public readonly Dictionary<string, FieldState> FamilyStates = new Dictionary<string, FieldState>(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, List<string>> Families = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Every load case name used by any selected object's loads (for the load case dropdowns).</summary>
        public readonly List<string> UsedLoadCases = new List<string>();

        public FieldState Get(string key)
        {
            FieldState s;
            return Scalars.TryGetValue(key, out s) ? s : FieldState.Absent;
        }

        public FieldState GetFamilyState(string baseKey)
        {
            FieldState s;
            return FamilyStates.TryGetValue(baseKey, out s) ? s : FieldState.Absent;
        }

        public List<string> GetFamily(string baseKey)
        {
            List<string> l;
            return Families.TryGetValue(baseKey, out l) ? l : new List<string>();
        }

        public static SelectionState From(IList<RhinoObject> objects, IEnumerable<string> scalarKeys, IEnumerable<string> familyKeys)
        {
            var state = new SelectionState { Count = objects == null ? 0 : objects.Count };
            var all = new List<Dictionary<string, string>>();
            if (objects != null)
                foreach (RhinoObject o in objects) all.Add(AttrReader.GetAxAttributes(o));

            foreach (string key in scalarKeys)
            {
                bool first = true;
                string common = null;
                bool varies = false;

                foreach (var attrs in all)
                {
                    string v;
                    attrs.TryGetValue(key, out v);
                    v = Clean(v);

                    if (first) { common = v; first = false; continue; }
                    if (!string.Equals(common, v, StringComparison.Ordinal)) { varies = true; break; }
                }

                if (varies) state.Scalars[key] = FieldState.Mixed;
                else if (common == null) state.Scalars[key] = FieldState.Absent;
                else state.Scalars[key] = FieldState.Of(common);
            }

            var cases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string fam in familyKeys)
            {
                List<string> common = null;
                bool varies = false;

                foreach (var attrs in all)
                {
                    List<string> list = AttrReader.GetFamily(attrs, fam);
                    foreach (string s in list)
                    {
                        string lc;
                        if (AttrReader.ParseFields(s).TryGetValue("LC", out lc) && lc.Length > 0 && cases.Add(lc))
                            state.UsedLoadCases.Add(lc);
                    }

                    if (common == null) { common = list; continue; }
                    if (!SameList(common, list)) varies = true;
                }

                if (common == null) common = new List<string>();
                if (varies)
                {
                    state.FamilyStates[fam] = FieldState.Mixed;
                    state.Families[fam] = new List<string>();
                }
                else
                {
                    state.FamilyStates[fam] = common.Count == 0 ? FieldState.Absent : FieldState.Of("");
                    state.Families[fam] = common;
                }
            }

            return state;
        }

        private static string Clean(string v)
        {
            if (v == null) return null;
            v = v.Trim();
            return v.Length == 0 || v == "-" ? null : v;
        }

        private static bool SameList(List<string> a, List<string> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
                if (!string.Equals(a[i], b[i], StringComparison.Ordinal)) return false;
            return true;
        }
    }

    /// <summary>
    /// What a dialog wants changed. Only keys the user actually touched are in here, so editing the
    /// material of a mixed selection leaves every other attribute of every object alone.
    /// </summary>
    public class AttrEdit
    {
        /// <summary>key -> new value; null or "" deletes the key.</summary>
        public readonly Dictionary<string, string> Scalars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>family base key -> complete replacement list (empty list removes all loads of that family).</summary>
        public readonly Dictionary<string, List<string>> Families = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        public bool IsEmpty { get { return Scalars.Count == 0 && Families.Count == 0; } }
    }
}
