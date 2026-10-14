using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using AxisBridge.Attributes;

namespace AxisBridge.Loads
{
    /// <summary>The five kinds of load an Ax_* attribute can carry, each with its own string format.</summary>
    public enum LoadKind
    {
        /// <summary>Ax_NodalLoad on a point: "LC=Dead; F=fx,fy,fz,mx,my,mz".</summary>
        Nodal,
        /// <summary>Ax_PointLoad on a member: "LC=Dead; sys=Global; at=ratio:0.5; F=fx,fy,fz,mx,my,mz".</summary>
        MemberPoint,
        /// <summary>Ax_DistLoad on a member: "LC=Dead; sys=Global; from=ratio:0; to=ratio:1; q1=...; q2=...".</summary>
        MemberDist,
        /// <summary>Ax_DistLoad on a surface: "LC=Dead; sys=Global; q=qx,qy,qz".</summary>
        SurfaceArea,
        /// <summary>Ax_PointLoad on a surface: "LC=Dead; at=x,y,z; F=fx,fy,fz,0,0,0".</summary>
        SurfacePoint
    }

    /// <summary>
    /// One load, as edited in a load grid row. Several rows (one per load case, or several loads in
    /// the same case) become Ax_DistLoad, Ax_DistLoad.1, Ax_DistLoad.2 ... on the object.
    ///
    /// The string format is shared with the AxisVM importer (Core/AttributeParser.cs); the extra
    /// "sys=" field is optional there and defaults to Global, so older attributes still read.
    /// </summary>
    public class LoadRow
    {
        public string Case = "Dead";

        /// <summary>Global, Local or (surfaces only) Projected.</summary>
        public string System = "Global";

        /// <summary>Member loads: positions are ratios 0..1 (false) or lengths in document units (true).</summary>
        public bool ByLength;

        public double Pos = 0.5;
        public double From = 0.0;
        public double To = 1.0;

        /// <summary>Forces/moments (point loads) or start intensities (distributed loads). Always 6 slots.</summary>
        public double[] V = new double[6];

        /// <summary>End intensities of a member distributed load; null = uniform (same as V).</summary>
        public double[] V2;

        /// <summary>Surface point load location, document units.</summary>
        public double X, Y, Z;

        public LoadRow Clone()
        {
            LoadRow r = (LoadRow)MemberwiseClone();
            r.V = (double[])V.Clone();
            r.V2 = V2 == null ? null : (double[])V2.Clone();
            return r;
        }

        // ==================================================================
        // Formatting
        // ==================================================================

        public string Format(LoadKind kind)
        {
            var sb = new StringBuilder();
            sb.Append("LC=").Append(Sanitize(Case));

            switch (kind)
            {
                case LoadKind.Nodal:
                    sb.Append("; F=").Append(Nums(V, 6));
                    break;

                case LoadKind.MemberPoint:
                    sb.Append("; sys=").Append(System);
                    sb.Append("; at=").Append(PosText(Pos));
                    sb.Append("; F=").Append(Nums(V, 6));
                    break;

                case LoadKind.MemberDist:
                    sb.Append("; sys=").Append(System);
                    sb.Append("; from=").Append(PosText(From));
                    sb.Append("; to=").Append(PosText(To));
                    sb.Append("; q1=").Append(Nums(V, 3)).Append(",0,0,0");
                    if (V2 != null && !SameFirst3(V, V2))
                        sb.Append("; q2=").Append(Nums(V2, 3)).Append(",0,0,0");
                    break;

                case LoadKind.SurfaceArea:
                    sb.Append("; sys=").Append(System);
                    sb.Append("; q=").Append(Nums(V, 3));
                    break;

                case LoadKind.SurfacePoint:
                    sb.Append("; sys=").Append(System);
                    sb.Append("; at=").Append(N(X)).Append(',').Append(N(Y)).Append(',').Append(N(Z));
                    sb.Append("; F=").Append(Nums(V, 3)).Append(",0,0,0");
                    break;
            }
            return sb.ToString();
        }

        /// <summary>Short text for AxVisualize tags, e.g. "Dead: qz=-5".</summary>
        public string Summary(LoadKind kind)
        {
            string[] names = kind == LoadKind.MemberDist || kind == LoadKind.SurfaceArea
                ? new[] { "qx", "qy", "qz", "mx", "my", "mz" }
                : new[] { "Fx", "Fy", "Fz", "Mx", "My", "Mz" };

            var parts = new List<string>();
            for (int i = 0; i < 6; i++)
                if (Math.Abs(V[i]) > 1e-12) parts.Add(names[i] + "=" + N(V[i]));
            if (V2 != null && !SameFirst3(V, V2))
                parts.Add("->" + N(V2[2]));
            return Case + ": " + (parts.Count == 0 ? "0" : string.Join(" ", parts.ToArray()));
        }

        private string PosText(double v)
        {
            return (ByLength ? "len:" : "ratio:") + N(v);
        }

        /// <summary>A load case name cannot contain the field separators.</summary>
        private static string Sanitize(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "Dead";
            return s.Trim().Replace(";", " ").Replace("=", " ");
        }

        private static bool SameFirst3(double[] a, double[] b)
        {
            for (int i = 0; i < 3; i++) if (Math.Abs(a[i] - b[i]) > 1e-12) return false;
            return true;
        }

        private static string Nums(double[] v, int n)
        {
            var parts = new string[n];
            for (int i = 0; i < n; i++) parts[i] = N(i < v.Length ? v[i] : 0.0);
            return string.Join(",", parts);
        }

        public static string N(double v)
        {
            return v.ToString("0.#########", CultureInfo.InvariantCulture);
        }

        // ==================================================================
        // Parsing (tolerant: accepts every spelling the importer accepts)
        // ==================================================================

        public static bool TryParse(LoadKind kind, string text, out LoadRow row)
        {
            row = new LoadRow();
            if (string.IsNullOrWhiteSpace(text)) return false;

            Dictionary<string, string> f = AttrReader.ParseFields(text);
            string v;

            if (TryField(f, out v, "LC", "LoadCase", "Case")) row.Case = v;
            if (TryField(f, out v, "sys", "system")) row.System = NormalizeSystem(v);

            switch (kind)
            {
                case LoadKind.Nodal:
                case LoadKind.MemberPoint:
                    if (TryField(f, out v, "F", "Force")) ParseInto(v, row.V);
                    if (kind == LoadKind.MemberPoint && TryField(f, out v, "at", "pos", "position"))
                        row.Pos = ParsePos(v, out row.ByLength);
                    return true;

                case LoadKind.MemberDist:
                    if (TryField(f, out v, "q1", "q")) ParseInto(v, row.V);
                    if (TryField(f, out v, "q2"))
                    {
                        row.V2 = new double[6];
                        ParseInto(v, row.V2);
                    }
                    bool byLenFrom = false, byLenTo = false;
                    if (TryField(f, out v, "from", "start")) row.From = ParsePos(v, out byLenFrom); else row.From = 0;
                    if (TryField(f, out v, "to", "end")) row.To = ParsePos(v, out byLenTo); else row.To = 1;
                    row.ByLength = byLenFrom || byLenTo;
                    return true;

                case LoadKind.SurfaceArea:
                    if (TryField(f, out v, "q", "q1")) ParseInto(v, row.V);
                    return true;

                case LoadKind.SurfacePoint:
                    if (TryField(f, out v, "F", "Force")) ParseInto(v, row.V);
                    if (TryField(f, out v, "at", "pos", "position"))
                    {
                        double[] p = new double[3];
                        ParseInto(v, p);
                        row.X = p[0]; row.Y = p[1]; row.Z = p[2];
                    }
                    return true;
            }
            return false;
        }

        public static string NormalizeSystem(string v)
        {
            string k = (v ?? "").Trim().ToLowerInvariant();
            if (k.StartsWith("l")) return "Local";
            if (k.StartsWith("p")) return "Projected";
            return "Global";
        }

        private static bool TryField(Dictionary<string, string> f, out string value, params string[] names)
        {
            foreach (string n in names)
                if (f.TryGetValue(n, out value) && !string.IsNullOrEmpty(value)) return true;
            value = null;
            return false;
        }

        private static void ParseInto(string text, double[] target)
        {
            string[] parts = text.Trim().Trim('[', ']', '(', ')').Split(',');
            for (int i = 0; i < parts.Length && i < target.Length; i++)
            {
                double d;
                if (double.TryParse(parts[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out d)) target[i] = d;
            }
        }

        /// <summary>"ratio:0.5", "len:2400", "50%" or a bare number (ratio).</summary>
        private static double ParsePos(string text, out bool byLength)
        {
            byLength = false;
            string s = text.Trim();
            double d;

            if (s.EndsWith("%"))
            {
                double.TryParse(s.TrimEnd('%').Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out d);
                return d / 100.0;
            }

            int colon = s.IndexOf(':');
            if (colon > 0)
            {
                string kind = s.Substring(0, colon).Trim().ToLowerInvariant();
                byLength = kind == "len" || kind == "length" || kind == "abs" || kind == "l";
                s = s.Substring(colon + 1).Trim();
            }

            double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d);
            return d;
        }
    }
}
