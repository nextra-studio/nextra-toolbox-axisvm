using System;
using System.Collections.Generic;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace AxisBridge.Attributes
{
    /// <summary>The geometry families AxisVM cares about. "Domain" = any surface: a domain OR a load panel.</summary>
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

        /// <summary>True when the object carries any Ax_* key other than Ax_Guid (i.e. it was really tagged).</summary>
        public static bool IsTagged(IDictionary<string, string> attrs)
        {
            if (attrs == null) return false;
            foreach (string k in attrs.Keys)
                if (!string.Equals(k, AxKeys.Guid, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        public static AxGeomKind KindOf(RhinoObject obj, IDictionary<string, string> attrs)
        {
            GeometryBase geom = obj?.Geometry;
            if (geom == null) return AxGeomKind.Other;

            if (geom is Point) return AxGeomKind.Point;

            if (geom is Brep || geom is Surface || geom is Extrusion || geom is Mesh ||
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
            return AxSchema.NormalizeLineType(t) ?? Capitalize(t.Trim());
        }

        /// <summary>Sub-layer name for a surface, from Ax_FEType.</summary>
        public static string DomainTypeName(IDictionary<string, string> attrs)
        {
            string t = Get(attrs, AxKeys.FeType);
            if (string.IsNullOrEmpty(t)) return "Untyped";
            return AxSchema.NormalizeSurfaceType(t) ?? Capitalize(t.Trim());
        }

        /// <summary>True when the surface is a load panel (which AxisVM does not treat as a domain).</summary>
        public static bool IsLoadPanel(IDictionary<string, string> attrs)
        {
            return AxSchema.IsLoadPanel(AxSchema.NormalizeSurfaceType(Get(attrs, AxKeys.FeType)));
        }

        /// <summary>Sub-layer name for a point, from which attributes it carries.</summary>
        public static string PointTypeName(IDictionary<string, string> attrs)
        {
            bool sup = attrs != null && attrs.ContainsKey(AxKeys.Support);
            bool load = attrs != null && AttrReader.GetFamily(attrs, AxKeys.NodalLoad).Count > 0;
            if (sup && load) return "SupportAndLoad";
            if (sup) return "Support";
            if (load) return "Load";
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
                case Mesh m:
                {
                    var amp = AreaMassProperties.Compute(m);
                    return amp != null ? amp.Centroid : m.GetBoundingBox(true).Center;
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
