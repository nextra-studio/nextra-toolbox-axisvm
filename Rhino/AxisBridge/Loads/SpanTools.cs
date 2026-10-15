using System;
using System.Collections.Generic;
using System.Globalization;
using AxisBridge.Export;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace AxisBridge.Loads
{
    /// <summary>
    /// One-way load panel span direction helpers. The "auto" rule here is the same as in the AxisVM
    /// importer (AxisModelBuilder.ShortSpanAngle), so the preview arrow shows exactly what AxisVM gets.
    /// </summary>
    public static class SpanTools
    {
        /// <summary>Ax_SpanDirection value for "Auto - longer span" (the shorter span turned 90 deg).</summary>
        public const string AutoLong = "autolong";

        /// <summary>True for the spellings of <see cref="AutoLong"/>. "long" alone is a legacy value (along the first edge).</summary>
        public static bool IsAutoLong(string value)
        {
            string k = (value ?? "").Trim().ToLowerInvariant().Replace(" ", "").Replace("-", "").Replace("_", "");
            return k == AutoLong || k == "longer" || k == "autolonger";
        }

        /// <summary>
        /// Direction of the shorter side of the smallest-area rectangle around the outline, projected on
        /// the global XY plane. Works for any outline (L-shapes, trapezoids...), not only rectangles.
        /// </summary>
        public static bool ShortSpan(IList<Point3d> rawOutline, out Vector3d dir)
        {
            dir = Vector3d.XAxis;
            if (rawOutline == null || rawOutline.Count < 3) return false;

            // The minimum rectangle has a side on a CONVEX HULL edge (for a concave panel that need not
            // be an outline edge). Same rule as the importer (AxisModelBuilder.ShortSpanAngle).
            List<Point3d> outline = ConvexHullXY(rawOutline);
            if (outline.Count < 3) return false;

            double bestArea = double.MaxValue;
            bool found = false;
            for (int i = 0; i < outline.Count; i++)
            {
                Point3d a = outline[i], b = outline[(i + 1) % outline.Count];
                double dx = b.X - a.X, dy = b.Y - a.Y;
                double len = Math.Sqrt(dx * dx + dy * dy);
                if (len < 1e-9) continue;
                double ux = dx / len, uy = dy / len;

                double minU = double.MaxValue, maxU = double.MinValue, minV = double.MaxValue, maxV = double.MinValue;
                foreach (Point3d p in outline)
                {
                    double pu = p.X * ux + p.Y * uy;
                    double pv = -p.X * uy + p.Y * ux;
                    minU = Math.Min(minU, pu); maxU = Math.Max(maxU, pu);
                    minV = Math.Min(minV, pv); maxV = Math.Max(maxV, pv);
                }
                double wu = maxU - minU, wv = maxV - minV;
                if (wu * wv >= bestArea - 1e-12) continue;
                bestArea = wu * wv;
                found = true;
                dir = wu <= wv ? new Vector3d(ux, uy, 0) : new Vector3d(-uy, ux, 0);
            }
            return found && bestArea > 1e-12;
        }

        /// <summary>Andrew's monotone chain on the XY projection.</summary>
        public static List<Point3d> ConvexHullXY(IList<Point3d> pts)
        {
            var p = new List<Point3d>(pts);
            p.Sort((a, b) => a.X != b.X ? a.X.CompareTo(b.X) : a.Y.CompareTo(b.Y));
            if (p.Count < 3) return p;

            Func<Point3d, Point3d, Point3d, double> cross = (o, a, b) => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);
            var h = new List<Point3d>();
            for (int i = 0; i < p.Count; i++)
            {
                while (h.Count >= 2 && cross(h[h.Count - 2], h[h.Count - 1], p[i]) <= 1e-12) h.RemoveAt(h.Count - 1);
                h.Add(p[i]);
            }
            int lower = h.Count + 1;
            for (int i = p.Count - 2; i >= 0; i--)
            {
                while (h.Count >= lower && cross(h[h.Count - 2], h[h.Count - 1], p[i]) <= 1e-12) h.RemoveAt(h.Count - 1);
                h.Add(p[i]);
            }
            h.RemoveAt(h.Count - 1);
            return h;
        }

        /// <summary>
        /// The span direction an Ax_SpanDirection value means for this outline: "" / auto = shorter
        /// span, "autolong" = longer span, "x", "y", or a typed vector. Returns false when the text
        /// cannot be read.
        /// </summary>
        public static bool Resolve(string value, IList<Point3d> outline, out Vector3d dir)
        {
            dir = Vector3d.XAxis;
            string k = (value ?? "").Trim().ToLowerInvariant();
            if (k.Length == 0 || k == "auto" || k == "0" || k == "-") return ShortSpan(outline, out dir);
            if (IsAutoLong(k))
            {
                if (!ShortSpan(outline, out dir)) return false;
                dir = new Vector3d(-dir.Y, dir.X, 0);
                return true;
            }
            if (k == "x") { dir = Vector3d.XAxis; return true; }
            if (k == "y") { dir = Vector3d.YAxis; return true; }

            string[] parts = k.Split(',');
            if (parts.Length != 3) return false;
            double[] v = new double[3];
            for (int i = 0; i < 3; i++)
                if (!double.TryParse(parts[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v[i])) return false;
            dir = new Vector3d(v[0], v[1], 0);
            return dir.Unitize();
        }

        /// <summary>Outer boundaries (one per face) of a surface-like object, for previews.</summary>
        public static List<List<Point3d>> Outlines(RhinoObject obj, double tol)
        {
            var result = new List<List<Point3d>>();
            List<FlattenedFace> faces;
            List<string> warnings;
            if (obj?.Geometry == null || !GeometryFlattener.TryFlattenDomain(obj.Geometry, tol, out faces, out warnings)) return result;
            foreach (FlattenedFace f in faces)
            {
                var pts = new List<Point3d>();
                foreach (double[] p in f.Outer) pts.Add(new Point3d(p[0], p[1], p[2]));
                if (pts.Count >= 3) result.Add(pts);
            }
            return result;
        }

        /// <summary>"x,y,z" text for a picked direction, rounded for readability.</summary>
        public static string VectorText(Vector3d v)
        {
            v.Z = 0;
            if (!v.Unitize()) return "";
            return string.Format(CultureInfo.InvariantCulture, "{0:0.####},{1:0.####},0", v.X, v.Y);
        }
    }
}
