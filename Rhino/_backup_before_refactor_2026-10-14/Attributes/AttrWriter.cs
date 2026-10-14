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
        public static void SetAttributes(RhinoDoc doc, IEnumerable<RhinoObject> objects, Dictionary<string, string> attributes)
        {
            if (doc == null || objects == null || attributes == null) return;

            foreach (RhinoObject obj in objects)
            {
                if (obj == null) continue;

                // Stamp GUID
                obj.Attributes.SetUserString(AxKeys.Guid, obj.Id.ToString());

                // Set or remove user strings
                foreach (KeyValuePair<string, string> kv in attributes)
                {
                    if (string.IsNullOrEmpty(kv.Value) || kv.Value.Trim() == "-")
                    {
                        obj.Attributes.DeleteUserString(kv.Key);
                    }
                    else
                    {
                        obj.Attributes.SetUserString(kv.Key, kv.Value.Trim());
                    }
                }

                obj.CommitChanges();
            }

            doc.Views.Redraw();
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

                var userStrings = obj.Attributes.GetUserStrings();
                List<string> toDelete = new List<string>();

                for (int i = 0; i < userStrings.Count; i++)
                {
                    string key = userStrings.GetKey(i);
                    if (key.StartsWith(AxKeys.Prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        toDelete.Add(key);
                    }
                }

                foreach (string key in toDelete)
                {
                    obj.Attributes.DeleteUserString(key);
                }

                obj.CommitChanges();
            }

            doc.Views.Redraw();
        }
    }
}
