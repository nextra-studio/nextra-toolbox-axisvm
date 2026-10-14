using System;
using System.Collections.Generic;
using System.Linq;
using AxisVM_plugin.Core;

namespace AxisVM_plugin.Checks
{
    /// <summary>
    /// Automated checks of ConnectionLoadMath (plan §5A). Each check prints PASS / FAIL; the exit code is the
    /// number of failures, so 0 means everything passed.
    /// </summary>
    internal static class Program
    {
        private static int failures;
        private const double Tol = 1e-9;

        private static int Main()
        {
            TransformRoundTrip();
            KnownRotations();
            MagnitudePreserved();
            FrameFromMatrix();
            SignTable();
            CriticalSearch();
            DelimitedText();

            Console.WriteLine();
            Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : failures + " CHECK(S) FAILED");
            return failures;
        }

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------

        private static void Check(string name, bool ok, string detail = "")
        {
            Console.WriteLine((ok ? "PASS  " : "FAIL  ") + name + (ok || detail == "" ? "" : "  -> " + detail));
            if (!ok) failures++;
        }

        private static bool Near(double[] a, double[] b, double tol = Tol)
        {
            for (int i = 0; i < a.Length; i++) if (Math.Abs(a[i] - b[i]) > tol) return false;
            return true;
        }

        private static string Show(double[] v) { return "[" + string.Join(", ", v.Select(ConnectionLoadMath.Num)) + "]"; }

        /// <summary>A random right-handed orthonormal frame (Gram-Schmidt on two random vectors).</summary>
        private static LocalFrame RandomFrame(Random rnd)
        {
            Vec3 a = new Vec3(rnd.NextDouble() - .5, rnd.NextDouble() - .5, rnd.NextDouble() - .5).Normalized();
            Vec3 b = new Vec3(rnd.NextDouble() - .5, rnd.NextDouble() - .5, rnd.NextDouble() - .5);
            Vec3 y = (b - Vec3.Dot(b, a) * a).Normalized();
            return new LocalFrame(a, y, Vec3.Cross(a, y));
        }

        private static double[] RandomVector(Random rnd)
        {
            return Enumerable.Range(0, 6).Select(i => (rnd.NextDouble() - .5) * 200).ToArray();
        }

        // ------------------------------------------------------------------
        // Checks
        // ------------------------------------------------------------------

        /// <summary>1. local -> global -> local returns the input, for random frames and loads.</summary>
        private static void TransformRoundTrip()
        {
            Random rnd = new Random(1);
            bool ok = true;
            for (int i = 0; i < 1000 && ok; i++)
            {
                LocalFrame f = RandomFrame(rnd);
                double[] v = RandomVector(rnd);
                ok = Near(ConnectionLoadMath.GlobalToLocal(ConnectionLoadMath.LocalToGlobal(v, f), f), v, 1e-9);
            }
            Check("1. Transform round trip (1000 random frames)", ok);
        }

        /// <summary>2. Hand-computed rotations.</summary>
        private static void KnownRotations()
        {
            // Beam along global X, default axes x=X, y=Y, z=Z: local equals global
            LocalFrame alongX = new LocalFrame(new Vec3(1, 0, 0), new Vec3(0, 1, 0), new Vec3(0, 0, 1));
            double[] local = { 10, 2, 3, 4, 5, 6 };
            Check("2a. Beam along X: global = local", Near(ConnectionLoadMath.LocalToGlobal(local, alongX), local));

            // Vz on a beam along X goes to Fz
            Check("2b. Beam along X: Vz -> Fz", Near(ConnectionLoadMath.LocalToGlobal(new double[] { 0, 0, 7, 0, 0, 0 }, alongX), new double[] { 0, 0, 7, 0, 0, 0 }));

            // Beam along global Y (x=Y, y=-X, z=Z): N -> Fy, Vy -> -Fx, My -> -Mx
            LocalFrame alongY = new LocalFrame(new Vec3(0, 1, 0), new Vec3(-1, 0, 0), new Vec3(0, 0, 1));
            double[] g = ConnectionLoadMath.LocalToGlobal(new double[] { 10, 3, 0, 0, 5, 0 }, alongY);
            Check("2c. Beam along Y: N -> Fy, Vy -> -Fx, My -> -Mx", Near(g, new double[] { -3, 10, 0, -5, 0, 0 }), Show(g));

            // Beam at 45 degrees in plan: N splits equally into Fx and Fy
            double c = Math.Sqrt(0.5);
            LocalFrame at45 = new LocalFrame(new Vec3(c, c, 0), new Vec3(-c, c, 0), new Vec3(0, 0, 1));
            g = ConnectionLoadMath.LocalToGlobal(new double[] { 10, 0, 0, 0, 0, 0 }, at45);
            Check("2d. 45 deg beam: N=10 -> Fx = Fy = 7.071", Near(g, new double[] { 10 * c, 10 * c, 0, 0, 0, 0 }), Show(g));

            // Column along global Z (x=Z, y=Y, z=-X): N -> Fz, Vz -> -Fx
            LocalFrame column = new LocalFrame(new Vec3(0, 0, 1), new Vec3(0, 1, 0), new Vec3(-1, 0, 0));
            g = ConnectionLoadMath.LocalToGlobal(new double[] { -50, 0, 4, 0, 0, 0 }, column);
            Check("2e. Column: N=-50 -> Fz=-50, Vz=4 -> Fx=-4", Near(g, new double[] { -4, 0, -50, 0, 0, 0 }), Show(g));
        }

        /// <summary>3. Rotation keeps the length of the force and of the moment vector.</summary>
        private static void MagnitudePreserved()
        {
            Random rnd = new Random(2);
            bool ok = true;
            for (int i = 0; i < 1000 && ok; i++)
            {
                LocalFrame f = RandomFrame(rnd);
                double[] v = RandomVector(rnd);
                double[] g = ConnectionLoadMath.LocalToGlobal(v, f);
                double fl = new Vec3(v[0], v[1], v[2]).Length, fg = new Vec3(g[0], g[1], g[2]).Length;
                double ml = new Vec3(v[3], v[4], v[5]).Length, mg = new Vec3(g[3], g[4], g[5]).Length;
                ok = Math.Abs(fl - fg) < 1e-9 && Math.Abs(ml - mg) < 1e-9;
            }
            Check("3. |F| and |M| preserved by the transform", ok);
        }

        /// <summary>Frame read from a matrix as rows / columns, orthonormal error and the 180 deg flip.</summary>
        private static void FrameFromMatrix()
        {
            // Rotation of 30 deg about Z: rows = local axes
            double c = Math.Cos(Math.PI / 6), s = Math.Sin(Math.PI / 6);
            LocalFrame rows = LocalFrame.FromMatrix(c, s, 0, -s, c, 0, 0, 0, 1, true);
            LocalFrame cols = LocalFrame.FromMatrix(c, s, 0, -s, c, 0, 0, 0, 1, false);
            Check("4a. Rows: Ex = (cos30, sin30, 0)", Math.Abs(rows.Ex.X - c) < Tol && Math.Abs(rows.Ex.Y - s) < Tol);
            Check("4b. Columns: Ex = (cos30, -sin30, 0)", Math.Abs(cols.Ex.X - c) < Tol && Math.Abs(cols.Ex.Y + s) < Tol);
            Check("4c. Both orthonormal and right-handed", rows.OrthonormalError() < 1e-12 && cols.OrthonormalError() < 1e-12);

            LocalFrame flipped = rows.FlippedAboutZ();
            Check("4d. Flip about z: x and y reversed, still right-handed",
                Math.Abs(flipped.Ex.X + c) < Tol && flipped.OrthonormalError() < 1e-12 && Math.Abs(flipped.Ez.Z - 1) < Tol);

            // Left-handed or skewed frames must be caught
            LocalFrame leftHanded = new LocalFrame(new Vec3(1, 0, 0), new Vec3(0, 1, 0), new Vec3(0, 0, -1));
            Check("4e. Left-handed frame detected", leftHanded.OrthonormalError() > 1);
            LocalFrame skew = new LocalFrame(new Vec3(1, 0, 0), new Vec3(0.1, 1, 0).Normalized(), new Vec3(0, 0, 1));
            Check("4f. Skewed frame detected", skew.OrthonormalError() > 0.05);
        }

        /// <summary>4. Start and end of a bar in pure tension give equal and opposite node forces.</summary>
        private static void SignTable()
        {
            // A bar with the same internal force all along (e.g. pure tension N = 25, constant torsion)
            double[] raw = { 25, 0, 0, 3, 0, 0 };
            double[] atStart = ConnectionLoadMath.RawToNodeLocal(raw, true);
            double[] atEnd = ConnectionLoadMath.RawToNodeLocal(raw, false);
            Check("5a. Tension: member pulls the start node along +x", Near(atStart, new double[] { 25, 0, 0, 3, 0, 0 }), Show(atStart));
            Check("5b. Tension: member pulls the end node along -x", Near(atEnd, new double[] { -25, 0, 0, -3, 0, 0 }), Show(atEnd));
            double[] sum = atStart.Zip(atEnd, (a, b) => a + b).ToArray();
            Check("5c. Start + end = 0 for a self-balanced bar", Near(sum, new double[6]));
            Check("5d. Sign tables have 6 entries of +-1",
                ConnectionLoadMath.StartFactor.Length == 6 && ConnectionLoadMath.EndFactor.Length == 6 &&
                ConnectionLoadMath.StartFactor.Concat(ConnectionLoadMath.EndFactor).All(f => Math.Abs(Math.Abs(f) - 1) < Tol));
        }

        /// <summary>5. Max+ / Min- search.</summary>
        private static void CriticalSearch()
        {
            CriticalPick p = ConnectionLoadMath.FindCritical(new List<double> { 3, -7, 12, 0, -2 });
            Check("6a. Mixed: max = 12 (#2), min = -7 (#1)", p.MaxIndex == 2 && p.MinIndex == 1 && p.HasPositive && p.HasNegative);

            p = ConnectionLoadMath.FindCritical(new List<double> { 4, 9, 1 });
            Check("6b. All positive: Min- flagged as no negative value", p.MinIndex == 2 && p.HasPositive && !p.HasNegative);

            p = ConnectionLoadMath.FindCritical(new List<double> { -4, -9, -1 });
            Check("6c. All negative: Max+ flagged as no positive value", p.MaxIndex == 2 && p.MinIndex == 1 && !p.HasPositive && p.HasNegative);

            p = ConnectionLoadMath.FindCritical(new List<double> { 5, 5, -5, -5 });
            Check("6d. Ties: first combination wins", p.MaxIndex == 0 && p.MinIndex == 2);

            p = ConnectionLoadMath.FindCritical(new List<double> { 8 });
            Check("6e. Single combination is both max and min", p.MaxIndex == 0 && p.MinIndex == 0);

            p = ConnectionLoadMath.FindCritical(new List<double>());
            Check("6f. Empty list: nothing picked", p.MaxIndex == -1 && p.MinIndex == -1 && !p.HasPositive && !p.HasNegative);

            p = ConnectionLoadMath.FindCritical(new List<double> { double.NaN, 2, double.NaN, -1 });
            Check("6g. NaN (missing result) is never critical", p.MaxIndex == 1 && p.MinIndex == 3);
        }

        /// <summary>6. CSV / TSV text.</summary>
        private static void DelimitedText()
        {
            string csv = ConnectionLoadMath.ToDelimited(new[] { "Node", "Combination", "Fy [kN]" },
                new List<IList<string>> { new[] { "12", "ULS 1; wind", ConnectionLoadMath.Num(-1234.5678) } }, ';', true);
            string[] lines = csv.Split(new[] { "\r\n" }, StringSplitOptions.None);
            Check("7a. First line is sep=;", lines[0] == "sep=;");
            Check("7b. Header columns", lines[1] == "Node;Combination;Fy [kN]");
            Check("7c. Cell with separator quoted, '.' decimal, 3 decimals", lines[2] == "12;\"ULS 1; wind\";-1234.568", lines[2]);

            string tsv = ConnectionLoadMath.ToDelimited(new[] { "a", "b" }, new List<IList<string>> { new[] { "1", "x\"y" } }, '\t', false);
            Check("7d. TSV: no sep line, quotes escaped", tsv == "a\tb\r\n1\t\"x\"\"y\"\r\n", tsv);

            // Decimal point must not depend on the PC's culture (Dutch uses ',')
            var old = System.Threading.Thread.CurrentThread.CurrentCulture;
            System.Threading.Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("nl-NL");
            string n = ConnectionLoadMath.Num(1.5);
            System.Threading.Thread.CurrentThread.CurrentCulture = old;
            Check("7e. Number format culture-independent (nl-NL)", n == "1.500", n);
        }
    }
}
