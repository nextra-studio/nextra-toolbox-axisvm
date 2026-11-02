using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace AxisVM_plugin.Core
{
    /// <summary>
    /// The signature of a Rhino object in the link: everything an import builds it from, as one canonical text - its
    /// geometry in metres (0.1 mm) and its Ax_* attributes. Two equal signatures mean the import would build the same
    /// thing, so "Load from Rhino" with "only changes" leaves that object alone (see LinkDiff).
    ///
    ///   "L|line"    + "\n" + "x,y,z;x,y,z"          (curves: start, [arc middle,] ... end; polylines every vertex)
    ///   "D|surface" + "\n" + "x,y,z;...;x,y,z"      (outer loop; each hole on a further "H " line)
    ///   "P|point"   + "\n" + "x,y,z"
    /// followed by one "key=value" line per attribute (sorted, Ax_Guid and empty values left out).
    ///
    /// Signatures are computed from the JSON entry itself - the one Rhino sent, or the one AxisVM sent for an element
    /// drawn in AxisVM (which is what Rhino echoes back). Rhino's AxLink stores Ax_Thickness and the "at=x,y,z" of point
    /// loads in its model unit, so those two are converted to metres here; everything else is compared as written.
    /// </summary>
    public static class LinkSignature
    {
        /// <summary>The import settings a signature is only valid under: the same text read with other units builds something else.</summary>
        public static string Settings(AxUnits units, double toleranceM)
        {
            return string.Format(CultureInfo.InvariantCulture, "length {0:R}; force {1}; tol {2:R}", units.LengthToM, units.ForceSystem, toleranceM);
        }

        /// <summary>Signature of every point, curve and surface of a Rhino section, by guid.</summary>
        public static Dictionary<string, string> All(JsonValue rhinoPart, double lengthToM)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (rhinoPart == null) return map;
            foreach (string list in new[] { "points", "lines", "domains" })
            {
                JsonValue arr = rhinoPart[list];
                for (int i = 0; arr != null && i < arr.Count; i++)
                {
                    JsonValue e = arr.At(i);
                    string guid = LinkFile.Get(e, "guid");
                    if (guid.Length > 0) map[guid] = Of(e, list, lengthToM);
                }
            }
            return map;
        }

        /// <summary>The signature of one entry of the "points", "lines" or "domains" list, lengths times <paramref name="lengthToM"/>.</summary>
        public static string Of(JsonValue e, string list, double lengthToM)
        {
            StringBuilder sb = new StringBuilder();
            if (list == "points")
            {
                sb.Append("P|point\n");
                Pt(sb, e["xyz"], lengthToM);
            }
            else if (list == "lines")
            {
                string geom = LinkFile.Get(e, "geom");
                sb.Append("L|").Append(geom.Length == 0 ? "line" : geom.ToLowerInvariant()).Append('\n');
                JsonValue pts = e["points"];
                if (pts != null && pts.Type == JsonValue.JsonType.Array && pts.Count >= 2) Loop(sb, pts, lengthToM);
                else
                {
                    Pt(sb, e["start"], lengthToM);
                    if (e["arcMid"] != null) { sb.Append(';'); Pt(sb, e["arcMid"], lengthToM); }
                    sb.Append(';');
                    Pt(sb, e["end"], lengthToM);
                }
            }
            else
            {
                sb.Append("D|surface\n");
                Loop(sb, e["contour"], lengthToM);
                JsonValue holes = e["holes"];
                for (int h = 0; holes != null && h < holes.Count; h++)
                {
                    sb.Append("\nH ");
                    Loop(sb, holes.At(h), lengthToM);
                }
            }

            JsonValue attrs = e["attrs"];
            if (attrs != null && attrs.ObjectValue != null)
            {
                List<string> lines = new List<string>();
                foreach (KeyValuePair<string, JsonValue> kv in attrs.ObjectValue)
                {
                    if (string.Equals(kv.Key, AxKeys.Guid, StringComparison.OrdinalIgnoreCase)) continue;
                    string v = (kv.Value == null ? "" : kv.Value.AsString("")).Trim();
                    if (v.Length == 0) continue;
                    if (string.Equals(kv.Key, AxKeys.Thickness, StringComparison.OrdinalIgnoreCase)) v = Scaled(v, lengthToM);
                    else if (string.Equals(AxKeys.BaseKey(kv.Key), AxKeys.PointLoad, StringComparison.OrdinalIgnoreCase)) v = ScaledAt(v, lengthToM);
                    lines.Add(kv.Key.ToLowerInvariant() + "=" + v);
                }
                lines.Sort(StringComparer.Ordinal);
                foreach (string l in lines) sb.Append('\n').Append(l);
            }
            return sb.ToString();
        }

        /// <summary>
        /// The geometry a signature describes, as polylines in metres: a curve's vertices, a surface's outer loop and each
        /// hole (closed: the first point repeated at the end), a point as a one-point list. Empty when unreadable.
        /// </summary>
        public static List<List<double[]>> Geometry(string sig)
        {
            var result = new List<List<double[]>>();
            if (string.IsNullOrEmpty(sig)) return result;
            string[] lines = sig.Split('\n');
            if (lines.Length < 2) return result;
            bool surface = lines[0].StartsWith("D|", StringComparison.Ordinal);
            for (int i = 1; i < lines.Length; i++)
            {
                string l = lines[i];
                if (i > 1 && !(surface && l.StartsWith("H ", StringComparison.Ordinal))) break;
                if (l.StartsWith("H ", StringComparison.Ordinal)) l = l.Substring(2);
                List<double[]> poly = new List<double[]>();
                foreach (string p in l.Split(';'))
                {
                    string[] c = p.Split(',');
                    double x, y, z;
                    if (c.Length == 3 && double.TryParse(c[0], NumberStyles.Float, CultureInfo.InvariantCulture, out x)
                        && double.TryParse(c[1], NumberStyles.Float, CultureInfo.InvariantCulture, out y)
                        && double.TryParse(c[2], NumberStyles.Float, CultureInfo.InvariantCulture, out z))
                        poly.Add(new[] { x, y, z });
                }
                if (surface && poly.Count > 2) poly.Add(poly[0]);
                if (poly.Count > 0) result.Add(poly);
            }
            return result;
        }

        private static void Pt(StringBuilder sb, JsonValue p, double k)
        {
            if (p == null || p.Count < 3) { sb.Append('?'); return; }
            sb.Append(string.Format(CultureInfo.InvariantCulture, "{0:0.0000},{1:0.0000},{2:0.0000}",
                Clean(p.At(0).AsDouble(0) * k), Clean(p.At(1).AsDouble(0) * k), Clean(p.At(2).AsDouble(0) * k)));
        }

        private static void Loop(StringBuilder sb, JsonValue pts, double k)
        {
            for (int i = 0; pts != null && i < pts.Count; i++)
            {
                if (i > 0) sb.Append(';');
                Pt(sb, pts.At(i), k);
            }
        }

        /// <summary>-0.0000 and 0.0000 are one value.</summary>
        private static double Clean(double v)
        {
            double r = Math.Round(v, 4);
            return r == 0 ? 0 : r;
        }

        private static string Scaled(string v, double k)
        {
            double d;
            if (!double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return v;
            return (d * k).ToString("0.######", CultureInfo.InvariantCulture);
        }

        /// <summary>"... at=x,y,z ..." of a point load with its numbers in metres; "at=ratio:..." stays as written.</summary>
        private static string ScaledAt(string v, double k)
        {
            int i = v.IndexOf("at=", StringComparison.OrdinalIgnoreCase);
            if (i < 0) return v;
            int end = v.IndexOf(';', i);
            string at = (end < 0 ? v.Substring(i + 3) : v.Substring(i + 3, end - i - 3)).Trim();
            if (at.IndexOf("ratio", StringComparison.OrdinalIgnoreCase) >= 0) return v;
            string[] parts = at.Split(',');
            for (int p = 0; p < parts.Length; p++) parts[p] = Scaled(parts[p].Trim(), k);
            return v.Substring(0, i) + "at=" + string.Join(",", parts) + (end < 0 ? "" : v.Substring(end));
        }
    }
}
