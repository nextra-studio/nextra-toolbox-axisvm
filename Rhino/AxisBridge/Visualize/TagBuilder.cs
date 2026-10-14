using System;
using System.Collections.Generic;
using System.Globalization;
using AxisBridge.Attributes;
using AxisBridge.Layers;
using AxisBridge.Loads;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace AxisBridge.Visualize
{
    /// <summary>
    /// Builds and tears down the AxVisualize text-dot overlay. Each Ax_* attribute category gets its own
    /// child layer under "AxTag" so it can be shown/hidden independently. Dots sit at member centres
    /// (releases at the matching curve end), fanned out so several tags on one element stay readable.
    /// </summary>
    public static class TagBuilder
    {
        /// <summary>(child layer name, attribute key, repeatable).</summary>
        private static readonly (string Layer, string Key, bool Repeat)[] Categories =
        {
            ("Section",       AxKeys.Section,       false),
            ("Material",      AxKeys.Material,      false),
            ("Type",          AxKeys.Type,          false),
            ("Release",       AxKeys.Release,       false),
            ("Support",       AxKeys.Support,       false),
            ("NodalLoad",     AxKeys.NodalLoad,     true),
            ("PointLoad",     AxKeys.PointLoad,     true),
            ("DistLoad",      AxKeys.DistLoad,      true),
            ("Stiffness",     AxKeys.Stiffness,     false),
            ("LocalAxis",     AxKeys.LocalAxis,     false),
            ("Eccentricity",  AxKeys.Eccentricity,  false),
            ("FEType",        AxKeys.FeType,        false),
            ("Thickness",     AxKeys.Thickness,     false),
            ("SpanDirection", AxKeys.SpanDirection, false),
            ("EdgeSpacing",   AxKeys.EdgeSpacing,   false),
            ("Parts",         AxKeys.Parts,         false),
            ("HingeDomains",  AxKeys.HingeDomains,  false),
            ("CoupledDOF",    AxKeys.CoupledDof,    false),
        };

        public static bool IsOn(RhinoDoc doc)
        {
            return doc != null && doc.Layers.FindByFullPath(AxLayers.TagRoot, RhinoMath.UnsetIntIndex) >= 0;
        }

        public static int Clear(RhinoDoc doc)
        {
            int removed = AxLayers.DeleteTree(doc, AxLayers.TagRoot);
            doc?.Views.Redraw();
            return removed;
        }

        /// <summary>Rebuilds the overlay from scratch. Returns the number of dots created.</summary>
        public static int Build(RhinoDoc doc, out int categoryCount)
        {
            categoryCount = 0;
            if (doc == null) return 0;

            Clear(doc);

            AxLayers.EnsureLayer(doc, AxLayers.TagRoot, System.Drawing.Color.MediumPurple);

            double spacing = SceneSpacing(doc);
            var stack = new Dictionary<string, int>();   // rounded anchor -> dots already placed there
            var usedCategories = new HashSet<string>();
            int dots = 0;

            foreach (RhinoObject obj in doc.Objects.FindByFilter(new ObjectEnumeratorSettings
            {
                NormalObjects = true,
                LockedObjects = true
            }))
            {
                if (obj == null || obj.Geometry == null) continue;
                if (obj.Geometry is TextDot) continue;
                if (AxLayers.IsOnTagTree(doc, obj)) continue;

                Dictionary<string, string> attrs = AttrReader.GetAxAttributes(obj);
                if (attrs.Count == 0) continue;

                Point3d centre = AxClassify.AnchorOf(obj);
                Curve crv = obj.Geometry as Curve;

                for (int ci = 0; ci < Categories.Length; ci++)
                {
                    var cat = Categories[ci];

                    // Release: one dot per end, at the curve ends.
                    if (cat.Key == AxKeys.Release && crv != null)
                    {
                        string raw = AttrReader.GetAttribute(obj, AxKeys.Release);
                        if (string.IsNullOrEmpty(raw)) continue;
                        int layerS = EnsureCat(doc, cat.Layer, usedCategories, ref categoryCount);
                        AddDot(doc, layerS, "S " + ReleaseText(raw, true), crv.PointAtStart, ci, spacing, stack, obj.Id);
                        AddDot(doc, layerS, "E " + ReleaseText(raw, false), crv.PointAtEnd, ci, spacing, stack, obj.Id);
                        dots += 2;
                        continue;
                    }

                    List<string> values = cat.Repeat
                        ? AttrReader.GetAttributeList(obj, cat.Key)
                        : Single(AttrReader.GetAttribute(obj, cat.Key));
                    if (values.Count == 0) continue;

                    int layer = EnsureCat(doc, cat.Layer, usedCategories, ref categoryCount);
                    foreach (string v in values)
                    {
                        string text = cat.Repeat ? LoadLabel(cat.Key, v, obj) : Label(cat.Layer, v);
                        AddDot(doc, layer, text, centre, ci, spacing, stack, obj.Id);
                        dots++;
                    }
                }
            }

            doc.Views.Redraw();
            return dots;
        }

        // ------------------------------------------------------------------

        private static int EnsureCat(RhinoDoc doc, string name, HashSet<string> used, ref int count)
        {
            string path = AxLayers.TagRoot + AxLayers.Sep + name;
            int idx = AxLayers.EnsureLayer(doc, path);
            if (used.Add(name)) count++;
            return idx;
        }

        private static void AddDot(RhinoDoc doc, int layerIndex, string text, Point3d anchor,
            int categoryIndex, double spacing, Dictionary<string, int> stack, Guid owner)
        {
            string key = Round(anchor);
            stack.TryGetValue(key, out int n);
            stack[key] = n + 1;

            // Fan out: angle by category so a multi-attribute element spreads, radius by stack depth.
            double angle = categoryIndex * (Math.PI / 6.0);
            double radius = spacing * (1 + n);
            Point3d p = anchor + new Vector3d(radius * Math.Cos(angle), radius * Math.Sin(angle), n * spacing * 0.35);

            var dot = new TextDot(text ?? "", p);
            var oa = new ObjectAttributes { LayerIndex = layerIndex };
            oa.SetUserString("Ax_TagOf", owner.ToString());
            doc.Objects.AddTextDot(dot, oa);
        }

        /// <summary>"Dead: qz=-5" instead of the raw attribute string.</summary>
        private static string LoadLabel(string key, string value, RhinoObject obj)
        {
            bool surface = !(obj.Geometry is Curve) || obj.Attributes.GetUserString(AxKeys.FeType) != null;
            LoadKind kind;
            if (key == AxKeys.NodalLoad) kind = LoadKind.Nodal;
            else if (key == AxKeys.PointLoad) kind = surface ? LoadKind.SurfacePoint : LoadKind.MemberPoint;
            else kind = surface ? LoadKind.SurfaceArea : LoadKind.MemberDist;

            LoadRow row;
            if (!LoadRow.TryParse(kind, value, out row)) return Label("Load", value);
            string s = row.Summary(kind);
            return s.Length > 40 ? s.Substring(0, 39) + "…" : s;
        }

        private static string Label(string category, string value)
        {
            if (string.IsNullOrEmpty(value)) return category;
            switch (category)
            {
                case "Thickness": return "t=" + value;
                case "Material": return value;
                case "Section": return value;
                default: return value.Length > 24 ? value.Substring(0, 23) + "…" : value;
            }
        }

        /// <summary>Compact release glyph: translations then rotations, o = released, x = fixed.</summary>
        private static string ReleaseText(string raw, bool wantStart)
        {
            string t = raw.Trim().ToLowerInvariant();
            if (t == "fix" || t == "fixed") return "fix";
            if (t == "pin" || t == "pinned") return "pin";
            if (t == "semi-pin" || t == "semipin") return "semi-pin";

            Dictionary<string, string> f = AttrReader.ParseFields(raw);
            string list = null;
            if (f.TryGetValue(wantStart ? "start" : "end", out string side)) list = side;
            else if (f.Count == 0) list = raw;   // bare 6-number list applies to both ends
            if (string.IsNullOrEmpty(list)) return wantStart ? "rigid" : "rigid";

            string[] parts = list.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < parts.Length && i < 6; i++)
            {
                if (i == 3) sb.Append('|');
                double val;
                double.TryParse(parts[i], NumberStyles.Any, CultureInfo.InvariantCulture, out val);
                sb.Append(val <= 0 ? 'o' : (val >= 1e9 ? 'x' : 's'));
            }
            return sb.Length > 0 ? sb.ToString() : "rigid";
        }

        private static double SceneSpacing(RhinoDoc doc)
        {
            BoundingBox bb = BoundingBox.Empty;
            foreach (RhinoObject o in doc.Objects)
                if (o?.Geometry != null && !(o.Geometry is TextDot))
                    bb.Union(o.Geometry.GetBoundingBox(false));
            double diag = bb.IsValid ? bb.Diagonal.Length : 0.0;
            double s = diag * 0.01;
            if (s <= RhinoMath.ZeroTolerance) s = Math.Max(doc.ModelAbsoluteTolerance * 100.0, 0.1);
            return s;
        }

        private static string Round(Point3d p)
        {
            return string.Format(CultureInfo.InvariantCulture, "{0:F2}_{1:F2}_{2:F2}", p.X, p.Y, p.Z);
        }

        private static List<string> Single(string v)
        {
            var list = new List<string>();
            if (!string.IsNullOrEmpty(v)) list.Add(v);
            return list;
        }
    }
}
