using System;
using System.Collections.Generic;
using Rhino.Geometry;

namespace AxisBridge.Export
{
    /// <summary>A curve reduced to what AxisVM can build: one straight line, one circular arc, or a polyline.</summary>
    public class FlattenedLine
    {
        public string Geom = "line";              // line | arc | polyline
        public double[] Start = new double[3];
        public double[] End = new double[3];
        public double[] ArcMid;                   // arc only
        public List<double[]> Points;             // polyline only (all vertices, start..end)
    }

    /// <summary>One planar face: an outer loop and any openings.</summary>
    public class FlattenedFace
    {
        public List<double[]> Outer = new List<double[]>();
        public List<List<double[]>> Holes = new List<List<double[]>>();
        public BrepFace Face;   // kept for closest-face tests of surface point loads
    }

    public static class GeometryFlattener
    {
        public static double[] ToArray(Point3d pt)
        {
            return new double[] { pt.X, pt.Y, pt.Z };
        }

        public static bool TryFlattenPoint(GeometryBase geom, out double[] xyz)
        {
            xyz = null;
            if (geom is Point pt)
            {
                xyz = ToArray(pt.Location);
                return true;
            }
            return false;
        }

        /// <summary>
        /// Lines and arcs stay exact. Polylines and polycurves made of straight pieces become one
        /// "polyline" member (so loads at a ratio, releases and parts refer to the whole curve, not to
        /// each piece). Anything else is segmented at the document tolerance, with a warning.
        /// </summary>
        public static bool TryFlattenCurve(Curve curve, double tolerance, out FlattenedLine line, out string warning)
        {
            line = null;
            warning = null;
            if (curve == null) return false;

            if (curve.IsLinear(tolerance) && !curve.IsClosed)
            {
                line = new FlattenedLine { Geom = "line", Start = ToArray(curve.PointAtStart), End = ToArray(curve.PointAtEnd) };
                return true;
            }

            Arc arc;
            if (curve.TryGetArc(out arc, tolerance) && !arc.IsCircle)
            {
                line = new FlattenedLine
                {
                    Geom = "arc",
                    Start = ToArray(arc.StartPoint),
                    End = ToArray(arc.EndPoint),
                    ArcMid = ToArray(arc.MidPoint)
                };
                return true;
            }

            Polyline pl;
            if (!curve.TryGetPolyline(out pl))
            {
                PolylineCurve pc = curve.ToPolyline(0, 0, Math.PI / 36.0, 0, 0, tolerance, 0, 0, true);
                pl = pc != null ? pc.ToPolyline() : null;
                warning = "a curved (non-arc) member was segmented into straight pieces at the document tolerance";
            }

            if (pl == null || pl.Count < 2) return false;
            pl.DeleteShortSegments(tolerance);
            if (pl.Count < 2) return false;

            if (pl.Count == 2)
            {
                line = new FlattenedLine { Geom = "line", Start = ToArray(pl[0]), End = ToArray(pl[1]) };
                return true;
            }

            line = new FlattenedLine { Geom = "polyline", Start = ToArray(pl[0]), End = ToArray(pl[pl.Count - 1]), Points = new List<double[]>() };
            foreach (Point3d p in pl) line.Points.Add(ToArray(p));
            return true;
        }

        /// <summary>
        /// Every face of a surface / polysurface / extrusion, or a closed planar curve, as planar loops.
        /// Openings (inner loops) are kept as holes.
        /// </summary>
        public static bool TryFlattenDomain(GeometryBase geom, double tolerance, out List<FlattenedFace> faces, out List<string> warnings)
        {
            faces = new List<FlattenedFace>();
            warnings = new List<string>();

            if (geom is Extrusion ext) geom = ext.ToBrep();
            else if (geom is Surface srf) geom = srf.ToBrep();

            if (geom is Brep brep)
            {
                foreach (BrepFace face in brep.Faces)
                {
                    if (!face.IsPlanar(tolerance * 10))
                        warnings.Add(string.Format("face {0} is not planar - AxisVM may reject it", face.FaceIndex));

                    var ff = new FlattenedFace { Face = face };
                    foreach (BrepLoop loop in face.Loops)
                    {
                        List<double[]> pts = LoopPoints(loop.To3dCurve(), tolerance);
                        if (pts == null || pts.Count < 3) continue;
                        if (loop.LoopType == BrepLoopType.Outer) ff.Outer = pts;
                        else if (loop.LoopType == BrepLoopType.Inner) ff.Holes.Add(pts);
                    }
                    if (ff.Outer.Count >= 3) faces.Add(ff);
                    else warnings.Add(string.Format("face {0} has no usable outer boundary - skipped", face.FaceIndex));
                }
                return faces.Count > 0;
            }

            if (geom is Curve crv && crv.IsClosed)
            {
                if (!crv.IsPlanar(tolerance)) warnings.Add("the closed curve is not planar - AxisVM may reject it");
                List<double[]> pts = LoopPoints(crv, tolerance);
                if (pts != null && pts.Count >= 3)
                {
                    faces.Add(new FlattenedFace { Outer = pts });
                    return true;
                }
            }

            return false;
        }

        /// <summary>A closed curve as distinct vertices (no repeated closing vertex).</summary>
        private static List<double[]> LoopPoints(Curve c, double tolerance)
        {
            if (c == null) return null;
            Polyline pl;
            if (!c.TryGetPolyline(out pl))
            {
                PolylineCurve pc = c.ToPolyline(0, 0, Math.PI / 36.0, 0, 0, tolerance, 0, 0, true);
                pl = pc != null ? pc.ToPolyline() : null;
            }
            if (pl == null) return null;
            pl.DeleteShortSegments(tolerance);
            if (pl.Count < 3) return null;

            // Collinear vertices are deliberately kept: a vertex part-way along a slab edge is usually
            // where a beam or column meets it, and AxisVM needs a node there.
            int count = pl.IsClosed ? pl.Count - 1 : pl.Count;
            var pts = new List<double[]>();
            for (int i = 0; i < count; i++) pts.Add(ToArray(pl[i]));
            return pts;
        }
    }
}
