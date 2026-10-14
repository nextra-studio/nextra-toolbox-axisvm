using System;
using System.Collections.Generic;
using System.Globalization;

namespace AxisVM_plugin.Core
{
    /// <summary>
    /// How the numbers written into the Rhino attributes are to be read.
    ///
    /// AxisVM's COM API always works in metres and kilonewtons (kN, kNm, kN/m, kN/m2), whatever the
    /// program happens to be *displaying*. So every value is converted once, from what the user typed
    /// in Rhino into those units.
    ///
    /// Only geometric quantities take the length factor - coordinates, thickness, eccentricity, a
    /// distance along a beam. Loads and stiffnesses carry their own unit (kN/m, kN/m2, kNm/rad...),
    /// and each kind converts with its own factor, because e.g. 1 N/mm = 1 kN/m but 1 N/mm2 = 1000 kN/m2.
    /// </summary>
    public class AxUnits
    {
        /// <summary>Multiply a Rhino length by this to get metres in AxisVM.</summary>
        public double LengthToM = 0.001;

        /// <summary>0 = loads written in kN and m, 1 = loads written in N and mm.</summary>
        public int ForceSystem = 0;

        public string LengthName = "mm";
        public string ForceName = "kN, m";

        private bool Nmm { get { return ForceSystem == 1; } }

        public double Length(double v) { return v * LengthToM; }

        /// <summary>Force: kN.</summary>
        public double Force(double v) { return Nmm ? v * 1e-3 : v; }

        /// <summary>Moment: kNm (from Nmm = 1e-6 kNm).</summary>
        public double Moment(double v) { return Nmm ? v * 1e-6 : v; }

        /// <summary>Line load: kN/m (1 N/mm = 1 kN/m).</summary>
        public double LineLoad(double v) { return v; }

        /// <summary>Distributed moment along a line: kNm/m (1 Nmm/mm = 1e-3 kNm/m).</summary>
        public double LineMoment(double v) { return Nmm ? v * 1e-3 : v; }

        /// <summary>Area load: kN/m2 (1 N/mm2 = 1000 kN/m2).</summary>
        public double AreaLoad(double v) { return Nmm ? v * 1e3 : v; }

        /// <summary>Translational stiffness: kN/m (1 N/mm = 1 kN/m).</summary>
        public double Stiffness(double v) { return v; }

        /// <summary>Rotational stiffness: kNm/rad (1 Nmm/rad = 1e-6 kNm/rad).</summary>
        public double RotStiffness(double v) { return Nmm ? v * 1e-6 : v; }

        /// <summary>Line support / edge hinge stiffness per length: kN/m/m (1 N/mm/mm = 1000 kN/m/m).</summary>
        public double LineStiffness(double v) { return Nmm ? v * 1e3 : v; }

        /// <summary>Line support / edge hinge rotational stiffness per length: kNm/rad/m (1 Nmm/rad/mm = 1e-3).</summary>
        public double LineRotStiffness(double v) { return Nmm ? v * 1e-3 : v; }

        public static readonly string[] LengthUnitNames = { "mm", "m" };
        public static readonly double[] LengthUnitFactors = { 0.001, 1.0 };

        public static readonly string[] ForceUnitNames = { "kN, m  (kN, kN/m, kN/m², kNm)", "N, mm  (N, N/mm, N/mm², Nmm)" };

        /// <summary>Maps Rhino's unit-system name onto an index into <see cref="LengthUnitNames"/>.</summary>
        public static int LengthIndexFromRhinoName(string rhinoUnitSystem)
        {
            if (string.IsNullOrEmpty(rhinoUnitSystem)) return 0;
            switch (rhinoUnitSystem.Trim().ToLowerInvariant())
            {
                case "meters": case "metres": case "m": return 1;
                default: return 0; // mm
            }
        }

        /// <summary>Rhino unit systems this addon can convert. Anything else gets a warning.</summary>
        public static bool IsSupportedRhinoUnit(string rhinoUnitSystem)
        {
            if (string.IsNullOrEmpty(rhinoUnitSystem)) return true;
            string k = rhinoUnitSystem.Trim().ToLowerInvariant();
            return k == "millimeters" || k == "millimetres" || k == "meters" || k == "metres" || k == "mm" || k == "m";
        }
    }

    /// <summary>Per-DOF release, as AxisVM wants it: a type plus a stiffness for the semi-rigid case.</summary>
    public struct ReleaseDof
    {
        /// <summary>0 = hinged, 1 = rigid, 2 = semi-rigid.</summary>
        public int Kind;
        public double Stiffness;

        public static ReleaseDof Rigid { get { return new ReleaseDof { Kind = 1 }; } }
        public static ReleaseDof Hinged { get { return new ReleaseDof { Kind = 0 }; } }
        public static ReleaseDof Semi(double k) { return new ReleaseDof { Kind = 2, Stiffness = k }; }
    }

    /// <summary>Releases at one end of a beam or rib: x, y, z, xx, yy, zz.</summary>
    public class ReleaseSet
    {
        public ReleaseDof[] Dof = new ReleaseDof[6];

        public static ReleaseSet AllRigid()
        {
            ReleaseSet r = new ReleaseSet();
            for (int i = 0; i < 6; i++) r.Dof[i] = ReleaseDof.Rigid;
            return r;
        }

        public bool IsAllRigid
        {
            get
            {
                foreach (ReleaseDof d in Dof) if (d.Kind != 1) return false;
                return true;
            }
        }
    }

    /// <summary>Both ends of one curve (Rhino start / end). Either may be null = "leave AxisVM's default".</summary>
    public class ReleaseSpec
    {
        public ReleaseSet Start;
        public ReleaseSet End;
    }

    /// <summary>Load component coordinate system as written in the attribute (sys=).</summary>
    public enum LoadSystem { Global, Local, Projected }

    /// <summary>A concentrated load, in kN / kNm once parsed.</summary>
    public class PointLoadSpec
    {
        public string LoadCase = "";
        public double[] F = new double[6];      // Fx Fy Fz Mx My Mz
        public LoadSystem System = LoadSystem.Global;

        /// <summary>true when the position is a 0..1 ratio, false when it is a distance in metres.</summary>
        public bool PositionIsRatio = true;
        public double Position = 0.5;

        /// <summary>For a load on a surface: where it sits, in metres.</summary>
        public RhinoXyz At;
        public bool HasAt;
    }

    /// <summary>A distributed load, in kN/m (members) or kN/m2 (surfaces) once parsed.</summary>
    public class DistLoadSpec
    {
        public string LoadCase = "";
        public double[] Q1 = new double[6];     // qx qy qz mx my mz at the start
        public double[] Q2 = new double[6];     // ... at the end
        public LoadSystem System = LoadSystem.Global;
        public bool PositionIsRatio = true;
        public double From = 0.0;
        public double To = 1.0;

        /// <summary>true when Q1 and Q2 differ, which is what AxisVM calls a trapezoidal load.</summary>
        public bool IsTrapezoid
        {
            get
            {
                for (int i = 0; i < 6; i++)
                    if (Math.Abs(Q1[i] - Q2[i]) > 1e-12) return true;
                return false;
            }
        }
    }

    /// <summary>
    /// Turns the attribute strings written in Rhino into typed values in AxisVM units.
    ///
    /// Every entry point returns false and fills <c>err</c> rather than throwing: a mistyped attribute
    /// on one object should cost that one attribute, be reported in the run summary, and let the rest
    /// of the import proceed.
    /// </summary>
    public static class AttributeParser
    {
        /// <summary>Stiffness at or above this counts as rigid rather than semi-rigid [kN/m, kNm/rad].</summary>
        public const double RigidThreshold = 1e9;

        /// <summary>What a "fixed" support DOF gets [kN/m, kNm/rad].</summary>
        public const double RigidStiffness = 1e10;

        /// <summary>
        /// What a coupled DOF of a node-node link gets. Kept at the same order as AxisVM's own "rigid"
        /// value - much stiffer only makes the stiffness matrix ill-conditioned.
        /// </summary>
        public const double LinkRigidStiffness = 1e10;

        // ==================================================================
        // Primitives
        // ==================================================================

        /// <summary>
        /// Splits "LC=Dead; F=0,0,-10,0,0,0" into its named fields. Values keep their internal commas,
        /// which is exactly why the format is named-field rather than positional.
        /// </summary>
        public static Dictionary<string, string> ParseFields(string value)
        {
            Dictionary<string, string> fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(value)) return fields;

            foreach (string raw in value.Split(';'))
            {
                string part = raw.Trim();
                if (part.Length == 0) continue;

                int eq = part.IndexOf('=');
                if (eq <= 0)
                {
                    // A bare token with no "=" - keep it under "" so callers can offer a shorthand.
                    if (!fields.ContainsKey("")) fields[""] = part;
                    continue;
                }

                string key = part.Substring(0, eq).Trim();
                string val = part.Substring(eq + 1).Trim();
                if (key.Length > 0) fields[key] = val;
            }
            return fields;
        }

        /// <summary>Splits a semicolon-separated list ("Roof; Grid A") into trimmed, non-empty entries.</summary>
        public static List<string> SplitList(string value)
        {
            List<string> items = new List<string>();
            if (string.IsNullOrEmpty(value)) return items;

            foreach (string raw in value.Split(';'))
            {
                string s = raw.Trim();
                if (s.Length > 0 && s != "-") items.Add(s);
            }
            return items;
        }

        /// <summary>Parses "1,2,3" or "1e10, 0, 0" into exactly <paramref name="expected"/> numbers.</summary>
        public static bool TryParseNumbers(string value, int expected, out double[] result, out string err)
        {
            result = null;
            err = null;

            if (string.IsNullOrEmpty(value)) { err = "the value is empty"; return false; }

            string trimmed = value.Trim().Trim('[', ']', '(', ')');
            string[] parts = trimmed.Split(',');
            if (parts.Length != expected)
            {
                err = string.Format("expected {0} comma-separated numbers but found {1} ('{2}')", expected, parts.Length, value);
                return false;
            }

            double[] values = new double[expected];
            for (int i = 0; i < expected; i++)
            {
                string token = parts[i].Trim();
                if (token.Length == 0) { values[i] = 0; continue; }
                if (!double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]))
                {
                    err = string.Format("'{0}' is not a number (in '{1}')", token, value);
                    return false;
                }
            }

            result = values;
            return true;
        }

        /// <summary>Reads an optional sys= field (Global, Local, Projected). Unknown text is Global.</summary>
        public static LoadSystem ParseSystem(Dictionary<string, string> f)
        {
            string s = Field(f, "sys", "system", "");
            if (string.IsNullOrEmpty(s)) return LoadSystem.Global;
            string k = s.Trim().ToLowerInvariant();
            if (k.StartsWith("l")) return LoadSystem.Local;
            if (k.StartsWith("p")) return LoadSystem.Projected;
            return LoadSystem.Global;
        }

        // ==================================================================
        // Supports
        // ==================================================================

        /// <summary>
        /// Reads Ax_Support: either six stiffnesses, or one of the shorthands. For a nodal support the
        /// result is kN/m and kNm/rad; for a line support (<paramref name="perLength"/>) per metre.
        /// </summary>
        public static bool TryParseSupport(string value, AxUnits units, bool perLength, out double[] stiff, out string err)
        {
            stiff = null;
            err = null;

            switch (value.Trim().ToLowerInvariant())
            {
                case "fixed":
                case "fix":
                    stiff = new[] { RigidStiffness, RigidStiffness, RigidStiffness, RigidStiffness, RigidStiffness, RigidStiffness };
                    return true;

                case "pinned":
                case "pin":
                    stiff = new[] { RigidStiffness, RigidStiffness, RigidStiffness, 0.0, 0.0, 0.0 };
                    return true;

                case "roller-z":
                case "rollerz":
                case "roller":
                    // Free to slide in the horizontal plane, held vertically, free to rotate.
                    stiff = new[] { 0.0, 0.0, RigidStiffness, 0.0, 0.0, 0.0 };
                    return true;
            }

            double[] raw;
            if (!TryParseNumbers(value, 6, out raw, out err)) return false;

            stiff = new double[6];
            for (int i = 0; i < 3; i++) stiff[i] = perLength ? units.LineStiffness(raw[i]) : units.Stiffness(raw[i]);
            for (int i = 3; i < 6; i++) stiff[i] = perLength ? units.LineRotStiffness(raw[i]) : units.RotStiffness(raw[i]);
            return true;
        }

        /// <summary>Reads one Ax_NodalLoad.n: "LC=Dead; F=0,0,-10,0,0,0".</summary>
        public static bool TryParseNodalLoad(string value, AxUnits units, out PointLoadSpec load, out string err)
        {
            load = null;
            err = null;

            Dictionary<string, string> f = ParseFields(value);
            string lc = Field(f, "LC", "LoadCase", "Case");
            if (string.IsNullOrEmpty(lc)) { err = "no LC= (load case) given"; return false; }

            string forces = Field(f, "F", "Force", "");
            if (string.IsNullOrEmpty(forces)) { err = "no F= (force) given"; return false; }

            double[] raw;
            if (!TryParseNumbers(forces, 6, out raw, out err)) return false;

            load = new PointLoadSpec { LoadCase = lc };
            for (int i = 0; i < 3; i++) load.F[i] = units.Force(raw[i]);
            for (int i = 3; i < 6; i++) load.F[i] = units.Moment(raw[i]);
            return true;
        }

        // ==================================================================
        // Line attributes
        // ==================================================================

        /// <summary>
        /// Reads Ax_Release. Accepts the three shorthands, or explicit per-end stiffnesses:
        /// "start=1e10,1e10,1e10,1e10,0,0; end=...". A stiffness of 0 is a hinge, anything at or above
        /// <see cref="RigidThreshold"/> is rigid, everything between is semi-rigid at that stiffness.
        /// </summary>
        public static bool TryParseRelease(string value, AxUnits units, out ReleaseSpec spec, out string err)
        {
            spec = null;
            err = null;

            switch (value.Trim().ToLowerInvariant())
            {
                case "fix":
                case "fixed":
                    spec = new ReleaseSpec { Start = ReleaseSet.AllRigid(), End = ReleaseSet.AllRigid() };
                    return true;

                case "pin":
                case "pinned":
                    spec = new ReleaseSpec { Start = Pinned(), End = Pinned() };
                    return true;

                case "semi-pin":
                case "semipin":
                    // A nearly-hinged connection: 1 kNm/rad about both bending axes, as the
                    // EndPinAssign addon uses. Deliberately absolute, whatever force unit was chosen.
                    spec = new ReleaseSpec { Start = SemiPinned(), End = SemiPinned() };
                    return true;
            }

            Dictionary<string, string> f = ParseFields(value);
            ReleaseSpec result = new ReleaseSpec();

            string startText = Field(f, "start", "s", "");
            string endText = Field(f, "end", "e", "");

            if (string.IsNullOrEmpty(startText) && string.IsNullOrEmpty(endText))
            {
                // Bare six numbers - apply the same set to both ends.
                ReleaseSet both;
                if (!TryParseReleaseSet(value, units, out both, out err)) return false;
                spec = new ReleaseSpec { Start = both, End = both };
                return true;
            }

            if (!string.IsNullOrEmpty(startText) && !TryParseReleaseSet(startText, units, out result.Start, out err))
            {
                err = "start: " + err;
                return false;
            }
            if (!string.IsNullOrEmpty(endText) && !TryParseReleaseSet(endText, units, out result.End, out err))
            {
                err = "end: " + err;
                return false;
            }

            spec = result;
            return true;
        }

        private static ReleaseSet Pinned()
        {
            ReleaseSet r = ReleaseSet.AllRigid();
            r.Dof[4] = ReleaseDof.Hinged;   // yy
            r.Dof[5] = ReleaseDof.Hinged;   // zz
            return r;
        }

        private static ReleaseSet SemiPinned()
        {
            ReleaseSet r = ReleaseSet.AllRigid();
            r.Dof[4] = ReleaseDof.Semi(1.0);
            r.Dof[5] = ReleaseDof.Semi(1.0);
            return r;
        }

        private static bool TryParseReleaseSet(string value, AxUnits units, out ReleaseSet set, out string err)
        {
            set = null;

            double[] raw;
            if (!TryParseNumbers(value, 6, out raw, out err)) return false;

            set = new ReleaseSet();
            for (int i = 0; i < 6; i++)
            {
                double si = i < 3 ? units.Stiffness(raw[i]) : units.RotStiffness(raw[i]);
                if (si <= 0) set.Dof[i] = ReleaseDof.Hinged;
                else if (si >= RigidThreshold) set.Dof[i] = ReleaseDof.Rigid;
                else set.Dof[i] = ReleaseDof.Semi(si);
            }
            return true;
        }

        /// <summary>Reads Ax_Section: "HEA200" or "HEA200; HEA300" for a tapered member.</summary>
        public static void ParseSectionNames(string value, out string startName, out string endName)
        {
            List<string> names = SplitList(value);
            if (names.Count == 0) { startName = null; endName = null; return; }
            startName = names[0];
            endName = names.Count > 1 ? names[1] : names[0];
        }

        /// <summary>Reads Ax_Eccentricity: one vector for both ends, or "start=...; end=...". Metres out.</summary>
        public static bool TryParseEccentricity(string value, AxUnits units,
                                                out RhinoXyz start, out RhinoXyz end, out string err)
        {
            start = new RhinoXyz();
            end = new RhinoXyz();

            Dictionary<string, string> f = ParseFields(value);
            string startText = Field(f, "start", "s", "");
            string endText = Field(f, "end", "e", "");

            if (string.IsNullOrEmpty(startText) && string.IsNullOrEmpty(endText))
            {
                RhinoXyz both;
                if (!TryParseVector(value, units, out both, out err)) return false;
                start = both;
                end = both;
                return true;
            }

            if (!string.IsNullOrEmpty(startText) && !TryParseVector(startText, units, out start, out err)) return false;
            if (!string.IsNullOrEmpty(endText) && !TryParseVector(endText, units, out end, out err)) return false;
            if (string.IsNullOrEmpty(endText)) end = start;
            if (string.IsNullOrEmpty(startText)) start = end;

            err = null;
            return true;
        }

        /// <summary>Reads a length-valued vector such as Ax_Eccentricity. Metres out.</summary>
        public static bool TryParseVector(string value, AxUnits units, out RhinoXyz v, out string err)
        {
            v = new RhinoXyz();
            double[] raw;
            if (!TryParseNumbers(value, 3, out raw, out err)) return false;
            v = new RhinoXyz(units.Length(raw[0]), units.Length(raw[1]), units.Length(raw[2]));
            return true;
        }

        /// <summary>Reads a direction such as Ax_LocalAxis. Not a length - no unit conversion applies.</summary>
        public static bool TryParseDirection(string value, out RhinoXyz v, out string err)
        {
            v = new RhinoXyz();
            err = null;

            switch (value.Trim().ToLowerInvariant())
            {
                case "x": v = new RhinoXyz(1, 0, 0); return true;
                case "y": v = new RhinoXyz(0, 1, 0); return true;
                case "z": v = new RhinoXyz(0, 0, 1); return true;
            }

            double[] raw;
            if (!TryParseNumbers(value, 3, out raw, out err)) return false;

            if (Math.Abs(raw[0]) < 1e-12 && Math.Abs(raw[1]) < 1e-12 && Math.Abs(raw[2]) < 1e-12)
            {
                err = "the direction is a zero vector";
                return false;
            }

            v = new RhinoXyz(raw[0], raw[1], raw[2]);
            return true;
        }

        /// <summary>Reads Ax_Stiffness of a spring element: six values in kN/m and kNm/rad.</summary>
        public static bool TryParseStiffness(string value, AxUnits units, out double[] stiff, out string err)
        {
            stiff = null;
            double[] raw;
            if (!TryParseNumbers(value, 6, out raw, out err)) return false;

            stiff = new double[6];
            for (int i = 0; i < 3; i++) stiff[i] = units.Stiffness(raw[i]);
            for (int i = 3; i < 6; i++) stiff[i] = units.RotStiffness(raw[i]);
            return true;
        }

        /// <summary>Reads Ax_Stiffness of an edge hinge: six values per metre of edge.</summary>
        public static bool TryParseLineStiffness(string value, AxUnits units, out double[] stiff, out string err)
        {
            stiff = null;
            double[] raw;
            if (!TryParseNumbers(value, 6, out raw, out err)) return false;

            stiff = new double[6];
            for (int i = 0; i < 3; i++) stiff[i] = units.LineStiffness(raw[i]);
            for (int i = 3; i < 6; i++) stiff[i] = units.LineRotStiffness(raw[i]);
            return true;
        }

        /// <summary>
        /// Reads Ax_CoupledDOF: six flags, each "rigid"/"free" or 1/0. Coupled DOFs come back as
        /// <see cref="LinkRigidStiffness"/>, free ones as 0, ready to drop into a link's stiffnesses.
        /// A number other than 0/1 is taken as that stiffness (a flexible link).
        /// </summary>
        public static bool TryParseCoupledDofs(string value, AxUnits units, out double[] stiff, out string err)
        {
            stiff = null;
            err = null;

            string trimmed = value.Trim().Trim('[', ']');
            string[] parts = trimmed.Split(',');
            if (parts.Length != 6)
            {
                err = string.Format("expected 6 flags but found {0} ('{1}')", parts.Length, value);
                return false;
            }

            stiff = new double[6];
            for (int i = 0; i < 6; i++)
            {
                string token = parts[i].Trim().ToLowerInvariant();
                double d;
                if (token == "rigid" || token == "1" || token == "fix" || token == "fixed" || token == "true")
                    stiff[i] = LinkRigidStiffness;
                else if (token == "free" || token == "0" || token == "false" || token == "")
                    stiff[i] = 0.0;
                else if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out d) && d >= 0)
                    stiff[i] = i < 3 ? units.Stiffness(d) : units.RotStiffness(d);
                else
                {
                    err = string.Format("'{0}' is neither 1 (coupled), 0 (free) nor a stiffness (in '{1}')", parts[i].Trim(), value);
                    stiff = null;
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Reads one Ax_PointLoad.n on a member:
        /// "LC=Dead; sys=Global; at=ratio:0.5; F=0,0,-10,0,0,0" or "... at=len:2400; ...".
        /// </summary>
        public static bool TryParseBeamPointLoad(string value, AxUnits units, out PointLoadSpec load, out string err)
        {
            load = null;

            Dictionary<string, string> f = ParseFields(value);
            string lc = Field(f, "LC", "LoadCase", "Case");
            if (string.IsNullOrEmpty(lc)) { err = "no LC= (load case) given"; return false; }

            string forces = Field(f, "F", "Force", "");
            if (string.IsNullOrEmpty(forces)) { err = "no F= (force) given"; return false; }

            double[] raw;
            if (!TryParseNumbers(forces, 6, out raw, out err)) return false;

            PointLoadSpec spec = new PointLoadSpec { LoadCase = lc, System = ParseSystem(f) };
            for (int i = 0; i < 3; i++) spec.F[i] = units.Force(raw[i]);
            for (int i = 3; i < 6; i++) spec.F[i] = units.Moment(raw[i]);

            string at = Field(f, "at", "pos", "position");
            if (!string.IsNullOrEmpty(at))
            {
                bool isRatio;
                double pos;
                if (!TryParsePosition(at, units, out isRatio, out pos, out err)) return false;
                spec.PositionIsRatio = isRatio;
                spec.Position = pos;
            }

            load = spec;
            err = null;
            return true;
        }

        /// <summary>
        /// Reads one Ax_DistLoad.n on a member:
        /// "LC=Dead; sys=Global; from=ratio:0; to=ratio:1; q1=0,0,-5,0,0,0; q2=0,0,-8,0,0,0".
        /// A single "q=" (or no q2) gives a uniform load.
        /// </summary>
        public static bool TryParseBeamDistLoad(string value, AxUnits units, out DistLoadSpec load, out string err)
        {
            load = null;

            Dictionary<string, string> f = ParseFields(value);
            string lc = Field(f, "LC", "LoadCase", "Case");
            if (string.IsNullOrEmpty(lc)) { err = "no LC= (load case) given"; return false; }

            string q1Text = Field(f, "q1", "q", "");
            string q2Text = Field(f, "q2", "", "");
            if (string.IsNullOrEmpty(q1Text)) { err = "no q= or q1= (intensity) given"; return false; }

            double[] q1;
            if (!TryParseLoadVector(q1Text, out q1, out err)) { err = "q1: " + err; return false; }

            double[] q2 = q1;
            if (!string.IsNullOrEmpty(q2Text) && !TryParseLoadVector(q2Text, out q2, out err)) { err = "q2: " + err; return false; }

            DistLoadSpec spec = new DistLoadSpec { LoadCase = lc, System = ParseSystem(f) };
            for (int i = 0; i < 3; i++)
            {
                spec.Q1[i] = units.LineLoad(q1[i]);
                spec.Q2[i] = units.LineLoad(q2[i]);
            }
            for (int i = 3; i < 6; i++)
            {
                spec.Q1[i] = units.LineMoment(q1[i]);
                spec.Q2[i] = units.LineMoment(q2[i]);
            }

            string fromText = Field(f, "from", "start", "");
            string toText = Field(f, "to", "end", "");

            bool fromIsRatio = true, toIsRatio = true;
            if (!string.IsNullOrEmpty(fromText) &&
                !TryParsePosition(fromText, units, out fromIsRatio, out spec.From, out err)) { err = "from: " + err; return false; }
            if (!string.IsNullOrEmpty(toText) &&
                !TryParsePosition(toText, units, out toIsRatio, out spec.To, out err)) { err = "to: " + err; return false; }

            if (string.IsNullOrEmpty(fromText) && string.IsNullOrEmpty(toText))
            {
                spec.From = 0.0;
                spec.To = 1.0;
                spec.PositionIsRatio = true;
            }
            else if (string.IsNullOrEmpty(fromText) || string.IsNullOrEmpty(toText))
            {
                // Only one end given: the other is the matching curve end.
                bool ratio = string.IsNullOrEmpty(fromText) ? toIsRatio : fromIsRatio;
                if (string.IsNullOrEmpty(fromText)) spec.From = 0.0;
                if (string.IsNullOrEmpty(toText)) spec.To = ratio ? 1.0 : double.PositiveInfinity;
                spec.PositionIsRatio = ratio;
            }
            else if (fromIsRatio != toIsRatio)
            {
                err = "from= and to= must both be ratios or both be lengths";
                return false;
            }
            else
            {
                spec.PositionIsRatio = fromIsRatio;
            }

            if (spec.To < spec.From)
            {
                err = "to= is before from=";
                return false;
            }

            load = spec;
            err = null;
            return true;
        }

        /// <summary>Reads one Ax_PointLoad.n on a surface: "LC=Dead; sys=Global; at=1.2,3.4,0; F=0,0,-10,0,0,0".</summary>
        public static bool TryParseDomainPointLoad(string value, AxUnits units, out PointLoadSpec load, out string err)
        {
            load = null;

            Dictionary<string, string> f = ParseFields(value);
            string lc = Field(f, "LC", "LoadCase", "Case");
            if (string.IsNullOrEmpty(lc)) { err = "no LC= (load case) given"; return false; }

            string forces = Field(f, "F", "Force", "");
            if (string.IsNullOrEmpty(forces)) { err = "no F= (force) given"; return false; }

            double[] raw;
            if (!TryParseLoadVector(forces, out raw, out err)) return false;

            string at = Field(f, "at", "pos", "position");
            if (string.IsNullOrEmpty(at)) { err = "no at= (position) given - a surface point load needs a location"; return false; }

            RhinoXyz where;
            if (!TryParseVector(at, units, out where, out err)) { err = "at: " + err; return false; }

            PointLoadSpec spec = new PointLoadSpec { LoadCase = lc, At = where, HasAt = true, System = ParseSystem(f) };
            for (int i = 0; i < 3; i++) spec.F[i] = units.Force(raw[i]);
            for (int i = 3; i < 6; i++) spec.F[i] = units.Moment(raw[i]);

            load = spec;
            err = null;
            return true;
        }

        /// <summary>
        /// Reads one Ax_DistLoad.n on a surface: "LC=Dead; sys=Global; q=0,0,-2.5". Uniform only.
        /// </summary>
        public static bool TryParseDomainDistLoad(string value, AxUnits units, out DistLoadSpec load, out string err)
        {
            load = null;

            Dictionary<string, string> f = ParseFields(value);
            string lc = Field(f, "LC", "LoadCase", "Case");
            if (string.IsNullOrEmpty(lc)) { err = "no LC= (load case) given"; return false; }

            string qText = Field(f, "q", "q1", "");
            if (string.IsNullOrEmpty(qText)) { err = "no q= (intensity) given"; return false; }

            double[] raw;
            if (!TryParseLoadVector(qText, out raw, out err)) return false;

            DistLoadSpec spec = new DistLoadSpec { LoadCase = lc, System = ParseSystem(f) };
            for (int i = 0; i < 3; i++)
            {
                spec.Q1[i] = units.AreaLoad(raw[i]);
                spec.Q2[i] = spec.Q1[i];
            }

            load = spec;
            err = null;
            return true;
        }

        // ==================================================================
        // Surface attributes
        // ==================================================================

        /// <summary>
        /// Reads Ax_SpanDirection of a one-way load panel. Returns true with an explicit angle in the
        /// global XY plane [deg] for "x", "y" or a vector; false with <paramref name="legacyDir"/> = 0
        /// (auto, shorter span), 1 or 2 (the older "along / across the first edge" values).
        /// </summary>
        public static bool TryParseSpanAngle(string value, out double angleDeg, out int legacyDir, out string err)
        {
            angleDeg = 0;
            legacyDir = 0;
            err = null;
            if (string.IsNullOrWhiteSpace(value)) return false;

            string k = value.Trim().ToLowerInvariant();
            switch (k)
            {
                case "0": case "auto": case "short": return false;
                case "1": case "dir1": case "long": legacyDir = 1; return false;
                case "2": case "dir2": legacyDir = 2; return false;
                case "x": angleDeg = 0; return true;
                case "y": angleDeg = 90; return true;
            }

            RhinoXyz v;
            if (!TryParseDirection(value, out v, out err)) return false;
            if (Math.Abs(v.X) < 1e-12 && Math.Abs(v.Y) < 1e-12)
            {
                err = "the span direction is vertical";
                return false;
            }
            angleDeg = Math.Atan2(v.Y, v.X) * 180.0 / Math.PI;
            return true;
        }

        // ==================================================================
        // Helpers
        // ==================================================================

        /// <summary>A force/intensity vector of 3 or 6 numbers, padded to 6.</summary>
        private static bool TryParseLoadVector(string text, out double[] values, out string err)
        {
            values = null;
            int n = text.Trim().Trim('[', ']', '(', ')').Split(',').Length;
            double[] raw;
            if (n == 3 || n == 6)
            {
                if (!TryParseNumbers(text, n, out raw, out err)) return false;
                values = new double[6];
                Array.Copy(raw, values, n);
                return true;
            }
            err = string.Format("expected 3 or 6 comma-separated numbers but found {0} ('{1}')", n, text);
            return false;
        }

        /// <summary>
        /// Parses "ratio:0.5", "len:2400", a bare "0.5" (taken as a ratio), or "50%".
        /// A length comes back in metres.
        /// </summary>
        private static bool TryParsePosition(string text, AxUnits units, out bool isRatio, out double value, out string err)
        {
            isRatio = true;
            value = 0;
            err = null;

            string s = text.Trim();

            if (s.EndsWith("%", StringComparison.Ordinal))
            {
                double pct;
                if (!double.TryParse(s.Substring(0, s.Length - 1).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out pct))
                {
                    err = string.Format("'{0}' is not a percentage", s);
                    return false;
                }
                value = pct / 100.0;
                return true;
            }

            int colon = s.IndexOf(':');
            string kind = colon > 0 ? s.Substring(0, colon).Trim().ToLowerInvariant() : "ratio";
            string number = colon > 0 ? s.Substring(colon + 1).Trim() : s;

            double raw;
            if (!double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out raw))
            {
                err = string.Format("'{0}' is not a number", number);
                return false;
            }

            switch (kind)
            {
                case "ratio":
                case "rel":
                case "r":
                    isRatio = true;
                    value = raw;
                    return true;

                case "len":
                case "length":
                case "abs":
                case "l":
                    isRatio = false;
                    value = units.Length(raw);
                    return true;

                default:
                    err = string.Format("'{0}' is not a position kind - use ratio: or len:", kind);
                    return false;
            }
        }

        /// <summary>First of up to three alternative field names that is present and non-empty.</summary>
        private static string Field(Dictionary<string, string> fields, string a, string b, string c)
        {
            string v;
            if (!string.IsNullOrEmpty(a) && fields.TryGetValue(a, out v) && !string.IsNullOrEmpty(v)) return v;
            if (!string.IsNullOrEmpty(b) && fields.TryGetValue(b, out v) && !string.IsNullOrEmpty(v)) return v;
            if (!string.IsNullOrEmpty(c) && fields.TryGetValue(c, out v) && !string.IsNullOrEmpty(v)) return v;
            return null;
        }
    }
}
