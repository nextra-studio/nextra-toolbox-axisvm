using System;
using System.Collections.Generic;
using System.Text;
using AxisBridge.Attributes;
using AxisBridge.Layers;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;

namespace AxisBridge.Commands
{
    /// <summary>
    /// Moves every Ax-tagged object into a tidy layer tree, one level per element type and one per
    /// defining property:
    ///   AxLayer :: Members :: Beam :: IPE 300 · S 355
    ///   AxLayer :: Domains :: Plate :: C30-37 · t=200
    ///   AxLayer :: LoadPanels :: OneWay
    ///   AxLayer :: Nodes :: Support · Pinned
    /// Object attributes, user strings and Ax_Guid are preserved. Layers under AxLayer left empty by the
    /// move (e.g. the older AxLine / AxDomain branches) are removed. The AxVisualize tag tree is skipped.
    /// </summary>
    public class AxLayerTidyUpCommand : Command
    {
        public override string EnglishName => "AxLayerTidyUp";

        private const string Dot = " · ";

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            if (doc == null) return Result.Failure;
            Tidy(doc, true);
            return Result.Success;
        }

        /// <summary>
        /// Sorts every Ax-tagged object into the AxLayer tree, colours the tree by element type and makes slabs and load
        /// panels see-through (AxLayerStyle). Also run after AxLink's "Load from AxisVM". Returns the number of objects moved.
        /// </summary>
        public static int Tidy(RhinoDoc doc, bool report)
        {
            var counts = new SortedDictionary<string, int>();
            int moved = 0;

            foreach (RhinoObject obj in doc.Objects.FindByFilter(new ObjectEnumeratorSettings
            {
                NormalObjects = true,
                LockedObjects = true,
                HiddenObjects = true
            }))
            {
                if (obj == null || obj.Geometry == null) continue;
                if (AxLayers.IsOnTagTree(doc, obj)) continue;

                Dictionary<string, string> attrs = AttrReader.GetAxAttributes(obj);
                if (!AxClassify.IsTagged(attrs)) continue;

                string fullPath = TargetLayer(obj, attrs);
                int li = AxLayers.EnsureLayer(doc, fullPath);
                if (li < 0) continue;

                counts.TryGetValue(fullPath, out int c);
                counts[fullPath] = c + 1;

                if (obj.Attributes.LayerIndex != li)
                {
                    ObjectAttributes a = obj.Attributes.Duplicate();
                    a.LayerIndex = li;
                    if (doc.Objects.ModifyAttributes(obj, a, true)) moved++;
                }
            }

            int pruned = AxLayers.DeleteEmptyUnder(doc, AxLayers.TidyRoot);
            AxLayerStyle.Apply(doc);
            doc.Views.Redraw();

            if (report)
            {
                RhinoApp.WriteLine($"AxLayerTidyUp: moved {moved} object(s), removed {pruned} empty layer(s).");
                foreach (var kv in counts)
                    RhinoApp.WriteLine($"  {kv.Key}  ->  {kv.Value}");
            }
            return moved;
        }

        private static string TargetLayer(RhinoObject obj, Dictionary<string, string> attrs)
        {
            string S = AxLayers.Sep;
            string root = AxLayers.TidyRoot + S;
            switch (AxClassify.KindOf(obj, attrs))
            {
                case AxGeomKind.Point:
                    return root + AxLayers.TidyPoints + S + NodeLayer(attrs);

                case AxGeomKind.Domain:
                    if (AxClassify.IsLoadPanel(attrs))
                    {
                        // Load panels are not domains in AxisVM, so they get their own branch.
                        string panel = AxClassify.DomainTypeName(attrs) == AxSchema.OneWayLoadPanel ? "OneWay" : "TwoWay";
                        return root + AxLayers.TidyPanels + S + panel;
                    }
                    return root + AxLayers.TidyDomains + S + AxClassify.DomainTypeName(attrs)
                         + S + Join(Get(attrs, AxKeys.Material), Prefixed("t=", Get(attrs, AxKeys.Thickness)), "No material");

                case AxGeomKind.Line:
                    return root + AxLayers.TidyLines + S + MemberLayer(attrs);

                default:
                    return root + "Other";
            }
        }

        /// <summary>Type, then the property that tells members of that type apart.</summary>
        private static string MemberLayer(Dictionary<string, string> attrs)
        {
            string type = AxClassify.LineTypeName(attrs);
            string S = AxLayers.Sep;
            switch (type)
            {
                case AxSchema.Beam:
                case AxSchema.Rib:
                case AxSchema.Truss:
                    return type + S + Join(Get(attrs, AxKeys.Section), Get(attrs, AxKeys.Material), "No section");
                case AxSchema.Simple:
                    return type + S + (AxClassify.SupportKindName(Get(attrs, AxKeys.Support)) ?? "No support");
                default:
                    // Springs, links and hinges are told apart by their stiffness / DOFs, which make poor
                    // layer names - one layer per type is enough.
                    return type;
            }
        }

        private static string NodeLayer(Dictionary<string, string> attrs)
        {
            string support = AxClassify.SupportKindName(Get(attrs, AxKeys.Support));
            if (support != null) return "Support" + Dot + support;
            if (AttrReader.GetFamily(attrs, AxKeys.NodalLoad).Count > 0) return "NodalLoad";
            return "Plain";
        }

        private static string Get(Dictionary<string, string> attrs, string key)
        {
            string v;
            if (!attrs.TryGetValue(key, out v) || v == null) return null;
            v = v.Trim();
            return v.Length == 0 || v == "-" ? null : v;
        }

        private static string Prefixed(string prefix, string v)
        {
            return v == null ? null : prefix + v;
        }

        /// <summary>"a · b" from the parts that are set, made safe for a layer name.</summary>
        private static string Join(string a, string b, string fallback)
        {
            var parts = new List<string>();
            if (a != null) parts.Add(a);
            if (b != null) parts.Add(b);
            return parts.Count == 0 ? fallback : SafeName(string.Join(Dot, parts.ToArray()), fallback);
        }

        /// <summary>Layer names cannot hold "::" or some punctuation; "C30/37" becomes "C30-37".</summary>
        private static string SafeName(string name, string fallback)
        {
            var sb = new StringBuilder(name.Length);
            foreach (char ch in name)
                sb.Append(ch == '/' || ch == '\\' || ch == ':' || ch == ';' || ch == '"' || ch == '[' || ch == ']' || ch == '{' || ch == '}' || ch == '(' || ch == ')' ? '-' : ch);
            string s = sb.ToString().Trim();
            if (s.Length > 60) s = s.Substring(0, 60).Trim();
            return s.Length > 0 && ModelComponent.IsValidComponentName(s) ? s : fallback;
        }
    }
}
