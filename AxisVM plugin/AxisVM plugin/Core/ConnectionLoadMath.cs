using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

/*
 * =====================================================================================
 * CONNECTION LOAD MATH - pure maths for the Connection Load Extractor addon
 * =====================================================================================
 *
 * Nothing in this file touches AxisVM / COM. That is deliberate: every number the addon reports goes
 * through these functions, so they are compiled a second time into the Checks console project
 * (Checks/ConnectionLoadMathChecks.csproj) and tested there without AxisVM running.
 *
 * Vectors carry six components in a fixed order:
 *   index 0..2 = forces  (local: N,  Vy, Vz   | global: Fx, Fy, Fz)
 *   index 3..5 = moments (local: Tx, My, Mz   | global: Mx, My, Mz)
 * =====================================================================================
 */

namespace AxisVM_plugin.Core
{
    /// <summary>Which axes a six-component load vector is expressed in.</summary>
    public enum LoadSystem
    {
        Local,   // the member's own x (along the bar), y, z
        Global   // the model's X, Y, Z
    }

    /// <summary>Which extreme a critical row represents.</summary>
    public enum CriticalKind
    {
        MaxPositive, // the largest value of the component over all combinations
        MinNegative  // the smallest (most negative) value of the component over all combinations
    }

    /// <summary>Minimal 3D vector - just what the transforms and checks need.</summary>
    public struct Vec3
    {
        public double X, Y, Z;

        public Vec3(double x, double y, double z) { X = x; Y = y; Z = z; }

        public static Vec3 operator +(Vec3 a, Vec3 b) { return new Vec3(a.X + b.X, a.Y + b.Y, a.Z + b.Z); }
        public static Vec3 operator -(Vec3 a, Vec3 b) { return new Vec3(a.X - b.X, a.Y - b.Y, a.Z - b.Z); }
        public static Vec3 operator *(double s, Vec3 a) { return new Vec3(s * a.X, s * a.Y, s * a.Z); }
        public static Vec3 operator -(Vec3 a) { return new Vec3(-a.X, -a.Y, -a.Z); }

        /// <summary>Scalar (dot) product.</summary>
        public static double Dot(Vec3 a, Vec3 b) { return a.X * b.X + a.Y * b.Y + a.Z * b.Z; }

        /// <summary>Vector (cross) product, right-handed.</summary>
        public static Vec3 Cross(Vec3 a, Vec3 b)
        {
            return new Vec3(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
        }

        /// <summary>Euclidean length.</summary>
        public double Length { get { return Math.Sqrt(X * X + Y * Y + Z * Z); } }

        /// <summary>Unit vector in the same direction (zero vector stays zero).</summary>
        public Vec3 Normalized()
        {
            double l = Length;
            return l < 1e-12 ? this : (1.0 / l) * this;
        }

        public override string ToString()
        {
            return string.Format(CultureInfo.InvariantCulture, "({0:0.###}, {1:0.###}, {2:0.###})", X, Y, Z);
        }
    }

    /// <summary>
    /// A member's local axes, each one a unit vector written in global coordinates.
    /// Local -> global for any vector v is: v_global = vx*Ex + vy*Ey + vz*Ez.
    /// </summary>
    public class LocalFrame
    {
        public Vec3 Ex; // local x - along the bar, start node -> end node
        public Vec3 Ey; // local y
        public Vec3 Ez; // local z

        public LocalFrame(Vec3 ex, Vec3 ey, Vec3 ez) { Ex = ex; Ey = ey; Ez = ez; }

        /// <summary>
        /// Builds the frame from a 3x3 matrix given row by row (e11..e33). When <paramref name="rowsAreAxes"/>
        /// is true row i is local axis i; when false, column i is local axis i (the transpose).
        /// </summary>
        public static LocalFrame FromMatrix(double e11, double e12, double e13,
                                            double e21, double e22, double e23,
                                            double e31, double e32, double e33,
                                            bool rowsAreAxes)
        {
            if (rowsAreAxes)
                return new LocalFrame(new Vec3(e11, e12, e13), new Vec3(e21, e22, e23), new Vec3(e31, e32, e33));
            return new LocalFrame(new Vec3(e11, e21, e31), new Vec3(e12, e22, e32), new Vec3(e13, e23, e33));
        }

        /// <summary>The same frame turned 180 degrees about local z: x and y reverse, z stays.</summary>
        public LocalFrame FlippedAboutZ() { return new LocalFrame(-Ex, -Ey, Ez); }

        /// <summary>
        /// Largest deviation from an orthonormal, right-handed frame:
        /// |each axis| - 1, pairwise dot products, and (Ex x Ey) - Ez. Zero for a perfect frame.
        /// </summary>
        public double OrthonormalError()
        {
            double err = 0;
            err = Math.Max(err, Math.Abs(Ex.Length - 1));
            err = Math.Max(err, Math.Abs(Ey.Length - 1));
            err = Math.Max(err, Math.Abs(Ez.Length - 1));
            err = Math.Max(err, Math.Abs(Vec3.Dot(Ex, Ey)));
            err = Math.Max(err, Math.Abs(Vec3.Dot(Ey, Ez)));
            err = Math.Max(err, Math.Abs(Vec3.Dot(Ez, Ex)));
            err = Math.Max(err, (Vec3.Cross(Ex, Ey) - Ez).Length); // right-handed: det = +1
            return err;
        }

        /// <summary>Local -> global for one 3-vector.</summary>
        public Vec3 ToGlobal(Vec3 local) { return local.X * Ex + local.Y * Ey + local.Z * Ez; }

        /// <summary>Global -> local for one 3-vector (projection onto each axis).</summary>
        public Vec3 ToLocal(Vec3 global) { return new Vec3(Vec3.Dot(global, Ex), Vec3.Dot(global, Ey), Vec3.Dot(global, Ez)); }
    }

    /// <summary>Result of the max+/min- search over one list of values.</summary>
    public class CriticalPick
    {
        public int MaxIndex = -1;    // index of the largest value (first one on a tie), -1 for an empty list
        public int MinIndex = -1;    // index of the smallest value (first one on a tie), -1 for an empty list
        public double MaxValue;      // the largest value itself
        public double MinValue;      // the smallest value itself
        public bool HasPositive { get { return MaxIndex >= 0 && MaxValue > 0; } } // false: "Max+" is really <= 0
        public bool HasNegative { get { return MinIndex >= 0 && MinValue < 0; } } // false: "Min-" is really >= 0
    }

    public static class ConnectionLoadMath
    {
        // ---------------------------------------------------------------------------------
        // Component naming
        // ---------------------------------------------------------------------------------

        /// <summary>Component names for the local system, in vector order.</summary>
        public static readonly string[] LocalNames = { "N", "Vy", "Vz", "Tx", "My", "Mz" };

        /// <summary>Component names for the global system, in vector order.</summary>
        public static readonly string[] GlobalNames = { "Fx", "Fy", "Fz", "Mx", "My", "Mz" };

        /// <summary>Names of the six components in the given system.</summary>
        public static string[] Names(LoadSystem system) { return system == LoadSystem.Local ? LocalNames : GlobalNames; }

        /// <summary>Unit of component <paramref name="index"/>: forces kN, moments kNm.</summary>
        public static string Unit(int index) { return index < 3 ? "kN" : "kNm"; }

        // ---------------------------------------------------------------------------------
        // Sign: AxisVM internal force -> force the member exerts on the node
        // ---------------------------------------------------------------------------------

        /*
         * AxisVM reports an internal force S at a section as the action on the cut face whose outward
         * normal is +x (the "positive face"). Using that:
         *   - at the START node (x = 0) the member is on the + side of the cut, so it pushes the node with +S;
         *   - at the END node   (x = L) the member is on the - side of the cut, so it pushes the node with -S.
         * That gives the default factors below. They are per component because AxisVM's beam sign
         * convention for moments/shears is not spelled out in the COM docs; if the calibration model
         * (docs/addons/ConnectionLoadExtractorAddon.md, "Verification") shows a component disagreeing with
         * the support reaction, flip that one entry in both arrays - nothing else needs to change.
         */

        /// <summary>Factor per component applied at the start node (x = 0).</summary>
        public static readonly double[] StartFactor = { +1, +1, +1, +1, +1, +1 };

        /// <summary>Factor per component applied at the end node (x = L).</summary>
        public static readonly double[] EndFactor = { -1, -1, -1, -1, -1, -1 };

        /// <summary>
        /// Converts the raw AxisVM internal force vector at a member end into the force the member exerts
        /// on the node, still in member local axes.
        /// </summary>
        public static double[] RawToNodeLocal(double[] raw, bool isStart)
        {
            double[] f = isStart ? StartFactor : EndFactor; // pick the factor set for this end
            double[] r = new double[6];
            for (int i = 0; i < 6; i++) r[i] = f[i] * raw[i];
            return r;
        }

        // ---------------------------------------------------------------------------------
        // Coordinate transforms
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// Local six-vector -> global six-vector. Forces and moments are both true vectors, so each triple
        /// is rotated with the same frame: (N, Vy, Vz) -> (Fx, Fy, Fz) and (Tx, My, Mz) -> (Mx, My, Mz).
        /// </summary>
        public static double[] LocalToGlobal(double[] local, LocalFrame frame)
        {
            Vec3 f = frame.ToGlobal(new Vec3(local[0], local[1], local[2])); // force triple
            Vec3 m = frame.ToGlobal(new Vec3(local[3], local[4], local[5])); // moment triple
            return new[] { f.X, f.Y, f.Z, m.X, m.Y, m.Z };
        }

        /// <summary>Global six-vector -> local six-vector (inverse of <see cref="LocalToGlobal"/>).</summary>
        public static double[] GlobalToLocal(double[] global, LocalFrame frame)
        {
            Vec3 f = frame.ToLocal(new Vec3(global[0], global[1], global[2]));
            Vec3 m = frame.ToLocal(new Vec3(global[3], global[4], global[5]));
            return new[] { f.X, f.Y, f.Z, m.X, m.Y, m.Z };
        }

        // ---------------------------------------------------------------------------------
        // Critical search
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// Finds the largest and the smallest value. On a tie the earliest index wins, so the result
        /// follows the order of the load combinations in the model.
        /// </summary>
        public static CriticalPick FindCritical(IList<double> values)
        {
            CriticalPick pick = new CriticalPick();
            for (int i = 0; i < values.Count; i++)
            {
                double v = values[i];
                if (double.IsNaN(v)) continue; // a missing result never becomes critical
                if (pick.MaxIndex < 0 || v > pick.MaxValue) { pick.MaxIndex = i; pick.MaxValue = v; }
                if (pick.MinIndex < 0 || v < pick.MinValue) { pick.MinIndex = i; pick.MinValue = v; }
            }
            return pick;
        }

        // ---------------------------------------------------------------------------------
        // Text export
        // ---------------------------------------------------------------------------------

        /// <summary>A number as text: invariant culture, fixed 3 decimals, so "1.234" on every PC.</summary>
        public static string Num(double v) { return v.ToString("0.000", CultureInfo.InvariantCulture); }

        /// <summary>
        /// Builds delimited text (CSV or TSV). With <paramref name="excelSepLine"/> the first line is
        /// "sep=;" so Excel splits the columns correctly whatever the PC's list separator is (the Dutch
        /// locale expects ';' and would otherwise misread '.' decimals in a ',' file).
        /// </summary>
        public static string ToDelimited(IList<string> header, IEnumerable<IList<string>> rows, char sep, bool excelSepLine)
        {
            StringBuilder sb = new StringBuilder();
            if (excelSepLine) sb.Append("sep=").Append(sep).Append("\r\n");
            AppendRow(sb, header, sep);
            foreach (IList<string> row in rows) AppendRow(sb, row, sep);
            return sb.ToString();
        }

        /// <summary>Writes one row, quoting any cell that contains the separator, a quote or a line break.</summary>
        private static void AppendRow(StringBuilder sb, IList<string> cells, char sep)
        {
            for (int i = 0; i < cells.Count; i++)
            {
                if (i > 0) sb.Append(sep);
                string c = cells[i] ?? "";
                bool quote = c.IndexOf(sep) >= 0 || c.IndexOf('"') >= 0 || c.IndexOf('\n') >= 0 || c.IndexOf('\r') >= 0;
                sb.Append(quote ? "\"" + c.Replace("\"", "\"\"") + "\"" : c);
            }
            sb.Append("\r\n");
        }
    }
}
