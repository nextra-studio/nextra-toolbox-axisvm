using System;
using System.Collections.Generic;
using Rhino;
using Rhino.DocObjects;

namespace AxisBridge.Attributes
{
    /// <summary>
    /// Writes user text attributes to Rhino objects and stamps Ax_Guid so the object's GUID
    /// is explicitly visible in the Properties panel and persisted with the model.
    /// </summary>
    public static class AttrWriter
    {
        /// <summary>
        /// Applies an edit to every object:
        ///   1. touched scalar keys are set (or deleted when empty),
        ///   2. touched load families are replaced as a whole (Ax_DistLoad, Ax_DistLoad.1, ...),
        ///   3. attributes that no longer fit the object's (possibly new) type are removed,
        ///   4. Ax_Guid is re-stamped.
        /// Untouched keys keep each object's own value - that is what makes multi-select editing safe.
        /// </summary>
        public static int ApplyEdit(RhinoDoc doc, IEnumerable<RhinoObject> objects, AttrEdit edit, AxGeomKind kind)
        {
            if (doc == null || objects == null || edit == null) return 0;
            int changed = 0;

            foreach (RhinoObject obj in objects)
            {
                if (obj == null) continue;
                ObjectAttributes a = obj.Attributes;

                // 1. Scalars
                foreach (KeyValuePair<string, string> kv in edit.Scalars)
                {
                    string v = kv.Value == null ? "" : kv.Value.Trim();
                    if (v.Length == 0 || v == "-") a.DeleteUserString(kv.Key);
                    else a.SetUserString(kv.Key, v);
                }

                // 2. Families: drop every existing member, then write the new list densely.
                foreach (KeyValuePair<string, List<string>> fam in edit.Families)
                {
                    foreach (string key in KeysOf(a))
                        if (string.Equals(AxKeys.BaseKey(key), fam.Key, StringComparison.OrdinalIgnoreCase))
                            a.DeleteUserString(key);

                    int i = 0;
                    foreach (string v in fam.Value)
                    {
                        if (string.IsNullOrWhiteSpace(v)) continue;
                        a.SetUserString(AxKeys.FamilyKey(fam.Key, i), v.Trim());
                        i++;
                    }
                }

                // 3. Remove what no longer applies to this object's type.
                CleanUp(a, kind);

                // 4. Stamp GUID
                a.SetUserString(AxKeys.Guid, obj.Id.ToString());

                obj.CommitChanges();
                changed++;
            }

            doc.Views.Redraw();
            return changed;
        }

        /// <summary>Removes Ax_* keys that do not belong on this kind/type of element.</summary>
        private static void CleanUp(ObjectAttributes a, AxGeomKind kind)
        {
            string lineType = a.GetUserString(AxKeys.Type);
            string feType = a.GetUserString(AxKeys.FeType);

            foreach (string key in KeysOf(a))
            {
                if (!key.StartsWith(AxKeys.Prefix, StringComparison.OrdinalIgnoreCase)) continue;
                string b = AxKeys.BaseKey(key);

                bool keep;
                switch (kind)
                {
                    case AxGeomKind.Line: keep = AxSchema.LineKeyApplies(lineType, b); break;
                    case AxGeomKind.Domain: keep = AxSchema.SurfaceKeyApplies(feType, b); break;
                    case AxGeomKind.Point: keep = AxSchema.PointKeyApplies(b); break;
                    default: keep = true; break;
                }

                if (!keep) a.DeleteUserString(key);
            }
        }

        private static List<string> KeysOf(ObjectAttributes a)
        {
            var list = new List<string>();
            var us = a.GetUserStrings();
            for (int i = 0; i < us.Count; i++) list.Add(us.GetKey(i));
            return list;
        }

        /// <summary>
        /// Re-stamps Ax_Guid = obj.Id on every object that carries any Ax_* user string. A copy/pasted
        /// object keeps the source object's Ax_Guid until this runs, so two objects can otherwise claim
        /// the same id. Returns (stamped, ofWhichWereStale).
        /// </summary>
        public static void RestampGuids(RhinoDoc doc, IEnumerable<RhinoObject> objects, out int stamped, out int stale)
        {
            stamped = 0;
            stale = 0;
            if (doc == null || objects == null) return;

            foreach (RhinoObject obj in objects)
            {
                if (obj == null) continue;
                if (!AxClassify.HasAnyAxAttribute(obj)) continue;

                string want = obj.Id.ToString();
                string have = obj.Attributes.GetUserString(AxKeys.Guid);
                if (string.Equals(have, want, StringComparison.OrdinalIgnoreCase)) continue;

                if (!string.IsNullOrEmpty(have)) stale++;
                obj.Attributes.SetUserString(AxKeys.Guid, want);
                obj.CommitChanges();
                stamped++;
            }

            doc.Views.Redraw();
        }

        public static void ClearAxAttributes(RhinoDoc doc, IEnumerable<RhinoObject> objects)
        {
            if (doc == null || objects == null) return;

            foreach (RhinoObject obj in objects)
            {
                if (obj == null) continue;

                foreach (string key in KeysOf(obj.Attributes))
                    if (key.StartsWith(AxKeys.Prefix, StringComparison.OrdinalIgnoreCase))
                        obj.Attributes.DeleteUserString(key);

                obj.CommitChanges();
            }

            doc.Views.Redraw();
        }
    }
}
