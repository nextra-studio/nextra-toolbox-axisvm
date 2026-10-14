using System;
using System.Collections.Generic;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace AxisBridge.Attributes
{
    /// <summary>The three geometry families AxisVM cares about.</summary>
    public enum AxGeomKind { Point, Line, Domain, Other }

    /// <summary>
    /// Shared classification of a Rhino object into the geometry kind and structural sub-type used by
    /// AxExport, AxLayerTidyUp and AxVisualize. Kept in one place so the three never drift apart.
    /// </summary>
    public static class AxClassify
    {
        public static bool HasAnyAxAttribute(RhinoObject obj)
        {
            if (obj == null) return false;
            var us = obj.Attributes.GetUserStrings();
            for (int i = 0; i < us.Count; i++)
                if (us.GetKey(i).StartsWith(AxKeys.Prefix, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        public static AxGeomKind KindOf(RhinoObject obj, IDictionary<string, string> attrs)
        {
            GeometryBase geom = obj?.Geometry;
            if (geom == null) return AxGeomKind.Other;

            if (geom is Point || (attrs != null && (attrs.ContainsKey(AxKeys.Support) || attrs.ContainsKey(AxKeys.NodalLoad))))
                return AxGeomKind.Point;

            if (geom is Brep || geom is Surface || geom is Extrusion ||
                (geom is Curve c && c.IsClosed && attrs != null && attrs.ContainsKey(AxKeys.FeType)))
                return AxGeomKind.Domain;

            if (geom is Curve) return AxGeomKind.Line;

            return AxGeomKind.Other;
        }

        /// <summary>Sub-layer name for a line, from Ax_Type.</summary>
        public static string LineTypeName(IDictionary<string, string> attrs)
        {
            string t = Get(attrs, AxKeys.Type);
            if (string.IsNullOrEmpty(t)) return "Untyped";
            switch (t.Trim().ToLowerInvariant())
            {
                case "beam": return "Beam";
                case "rib": return "Rib";
                case "truss": return "Truss";
                case "spring": return "Spring";
                case "rigidlink":
                case "rigid link": return "RigidLink";
                case "nodelink":
                case "node link": return "NodeLink";
                case "edgehinge":
                case "edge hinge": return "EdgeHinge";
                case "simple": return "Simple";
                default: return Capitalize(t.Trim());
            }
        }

        /// <summary>Sub-layer name for a domain, from Ax_FEType.</summary>
        public static string DomainTypeName(IDictionary<string, string> attrs)
        {
            string t = Get(attrs, AxKeys.FeType);
            if (string.IsNullOrEmpty(t)) return "Untyped";
            string k = t.Trim().ToLowerInvariant();
            if (k.Contains("oneway") || k.Contains("one-way")) return "OneWay";
            if (k.Contains("loadpanel") || k.Contains("load panel") || k == "panel") return "LoadPanel";
            if (k.Contains("plate")) return "Plate";
            if (k.Contains("membrane")) return "Membrane";
            if (k.Contains("shell")) return "Shell";
            return Capitalize(t.Trim());
        }

        /// <summary>Sub-layer name for a point, from which attributes it carries.</summary>
        public static string PointTypeName(IDictionary<string, string> attrs)
        {
            if (attrs != null && attrs.ContainsKey(AxKeys.Support)) return "Support";
            if (attrs != null && attrs.ContainsKey(AxKeys.NodalLoad)) return "Load";
            return "Plain";
        }

        /// <summary>World-space anchor for a tag on this object.</summary>
        public static Point3d AnchorOf(RhinoObject obj)
        {
            GeometryBase geom = obj?.Geometry;
            switch (geom)
            {
                case Point p:
                    return p.Location;
                case Curve c:
                    return c.PointAt(c.Domain.Mid);
                case Brep b:
                {
                    var amp = AreaMassProperties.Compute(b);
                    return amp != null ? amp.Centroid : b.GetBoundingBox(true).Center;
                }
                case Extrusion e:
                {
                    Brep eb = e.ToBrep();
                    var amp = eb != null ? AreaMassProperties.Compute(eb) : null;
                    return amp != null ? amp.Centroid : e.GetBoundingBox(true).Center;
                }
                case Surface s:
                {
                    Brep sb = s.ToBrep();
                    var amp = sb != null ? AreaMassProperties.Compute(sb) : null;
                    return amp != null ? amp.Centroid : s.GetBoundingBox(true).Center;
                }
                default:
                    return geom?.GetBoundingBox(true).Center ?? Point3d.Origin;
            }
        }

        private static string Get(IDictionary<string, string> attrs, string key)
        {
            if (attrs == null) return null;
            return attrs.TryGetValue(key, out string v) ? v : null;
        }

        private static string Capitalize(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            return char.ToUpperInvariant(s[0]) + s.Substring(1);
        }
    }
}
