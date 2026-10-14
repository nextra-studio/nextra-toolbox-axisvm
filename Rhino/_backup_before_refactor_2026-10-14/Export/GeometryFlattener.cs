using System;
using System.Collections.Generic;
using Rhino.Geometry;

namespace AxisBridge.Export
{
    public class FlattenedLine
    {
        public double[] Start { get; set; } = new double[3];
        public double[] End { get; set; } = new double[3];
        public string Geom { get; set; } = "line";
        public double[] ArcMid { get; set; }
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

        public static bool TryFlattenCurve(GeometryBase geom, double tolerance, out List<FlattenedLine> lines, out List<string> warnings)
        {
            lines = new List<FlattenedLine>();
            warnings = new List<string>();

            if (!(geom is Curve curve)) return false;

            if (curve is LineCurve lineCurve)
            {
                lines.Add(new FlattenedLine
                {
                    Start = ToArray(lineCurve.Line.From),
                    End = ToArray(lineCurve.Line.To),
                    Geom = "line"
                });
                return true;
            }

            if (curve is ArcCurve arcCurve)
            {
                lines.Add(new FlattenedLine
                {
                    Start = ToArray(arcCurve.Arc.StartPoint),
                    End = ToArray(arcCurve.Arc.EndPoint),
                    Geom = "arc",
                    ArcMid = ToArray(arcCurve.Arc.MidPoint)
                });
                return true;
            }

            if (curve.TryGetArc(out Arc arc, tolerance))
            {
                lines.Add(new FlattenedLine
                {
                    Start = ToArray(arc.StartPoint),
                    End = ToArray(arc.EndPoint),
                    Geom = "arc",
                    ArcMid = ToArray(arc.MidPoint)
                });
                return true;
            }

            if (curve.IsLinear(tolerance))
            {
                lines.Add(new FlattenedLine
                {
                    Start = ToArray(curve.PointAtStart),
                    End = ToArray(curve.PointAtEnd),
                    Geom = "line"
                });
                return true;
            }

            if (curve is PolylineCurve polylineCurve)
            {
                Polyline pl = polylineCurve.ToPolyline();
                for (int i = 0; i < pl.Count - 1; i++)
                {
                    lines.Add(new FlattenedLine
                    {
                        Start = ToArray(pl[i]),
                        End = ToArray(pl[i + 1]),
                        Geom = "line"
                    });
                }
                return lines.Count > 0;
            }

            // Free-form curve: polygonize with warning
            PolylineCurve polyCurve = curve.ToPolyline(0, 0, 0.1, 0, 0, tolerance, 0, 0, true);
            Polyline poly = polyCurve != null ? polyCurve.ToPolyline() : null;
            if (poly != null && poly.Count > 1)
            {
                warnings.Add("A free-form curve was segmented into straight lines at document tolerance.");
                for (int i = 0; i < poly.Count - 1; i++)
                {
                    lines.Add(new FlattenedLine
                    {
                        Start = ToArray(poly[i]),
                        End = ToArray(poly[i + 1]),
                        Geom = "line"
                    });
                }
                return true;
            }

            return false;
        }

        public static bool TryFlattenDomain(GeometryBase geom, double tolerance, out List<double[]> contour, out List<string> warnings)
        {
            contour = new List<double[]>();
            warnings = new List<string>();

            if (geom is Extrusion ext)
            {
                geom = ext.ToBrep();
            }

            if (geom is Surface srf)
            {
                geom = srf.ToBrep();
            }

            if (geom is Brep brep)
            {
                if (brep.Faces.Count > 1)
                {
                    warnings.Add("Multi-face Brep detected: only the outer perimeter boundary is exported for this domain.");
                }

                Curve[] edges = brep.DuplicateEdgeCurves(true);
                if (edges == null || edges.Length == 0) return false;

                Curve[] joined = Curve.JoinCurves(edges, tolerance);
                if (joined == null || joined.Length == 0) return false;

                // Find largest closed loop
                Curve outer = joined[0];
                if (joined.Length > 1)
                {
                    double maxLen = outer.GetLength();
                    for (int i = 1; i < joined.Length; i++)
                    {
                        double len = joined[i].GetLength();
                        if (len > maxLen) { outer = joined[i]; maxLen = len; }
                    }
                }

                if (!outer.IsClosed)
                {
                    warnings.Add("Domain boundary is not closed.");
                }

                PolylineCurve outerPolyCurve = outer.ToPolyline(0, 0, 0.1, 0, 0, tolerance, 0, 0, true);
                Polyline pl = outerPolyCurve != null ? outerPolyCurve.ToPolyline() : null;
                if (pl == null || pl.Count < 3) return false;

                int count = pl.IsClosed ? pl.Count - 1 : pl.Count;
                for (int i = 0; i < count; i++)
                {
                    contour.Add(ToArray(pl[i]));
                }
                return contour.Count >= 3;
            }

            if (geom is Curve crv && crv.IsClosed)
            {
                PolylineCurve crvPolyCurve = crv.ToPolyline(0, 0, 0.1, 0, 0, tolerance, 0, 0, true);
                Polyline pl = crvPolyCurve != null ? crvPolyCurve.ToPolyline() : null;
                if (pl != null && pl.Count >= 3)
                {
                    int count = pl.IsClosed ? pl.Count - 1 : pl.Count;
                    for (int i = 0; i < count; i++)
                    {
                        contour.Add(ToArray(pl[i]));
                    }
                    return contour.Count >= 3;
                }
            }

            return false;
        }
    }
}
